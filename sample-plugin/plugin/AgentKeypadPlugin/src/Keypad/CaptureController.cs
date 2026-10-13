namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    internal enum CapturePhase { Idle, Selecting, Recording, Processing, InBox, NotSent }

    // Dictate and Select Area. The receiving app is fixed when capture starts: the last of
    // Claude or Codex that was in front. Doing records and transcribes; the plugin brings the
    // receiving app forward before Doing pastes. The plugin never presses Return: the user sends the message.
    internal sealed class CaptureController : IDisposable
    {
        public const String ClaudeBundle = "com.anthropic.claudefordesktop";
        public const String CodexBundle = "com.openai.codex";

        private readonly Plugin _plugin;
        private readonly Action _redraw;
        private readonly Timer _poll;
        private readonly Object _lock = new();
        private Process _selector;

        public CapturePhase Phase { get; private set; } = CapturePhase.Idle;
        public String Kind { get; private set; }            // "voice" or "area"
        public String Target { get; private set; }          // bundle id fixed at capture start
        public String Note { get; private set; }            // short reason for NotSent
        public String LastAi { get; private set; }          // last Claude or Codex bundle in front
        public DateTime StartedAt { get; private set; }
        public String Step { get; private set; }
        public DateTime ProcessingSince { get; private set; }

        public CaptureController(Plugin plugin, Action redraw)
        {
            this._plugin = plugin;
            this._redraw = () =>
            {
                if (!this._disposed)
                {
                    redraw();
                }
            };
            this._poll = new Timer(_ => this.Poll(), null, 0, 500);
        }

        public static String AppName(String bundle) => bundle == ClaudeBundle ? "Claude" : bundle == CodexBundle ? "Codex" : null;

        public Boolean Busy => this.Phase is CapturePhase.Selecting or CapturePhase.Recording or CapturePhase.Processing;

        private const Int32 SettleMs = 450;   // after an app comes to the front, before pasting into it
        private Int32 _ticks;
        private Int32 _generation;   // one number per capture; work from an older capture may not change the key
        private Int32 _sending;      // 1 while Keep in wiki is pasting
        private volatile Boolean _disposed;
        private String _lastFront;

        private void Poll()
        {
            try
            {
                var front = MacNative.FrontmostBundleId();
                if (front == ClaudeBundle || front == CodexBundle)
                {
                    this.LastAi = front;
                }

                // The switch key offers the other agent, so redraw when the front app changes.
                if (front != this._lastFront)
                {
                    this._lastFront = front;
                    this._redraw();
                }

                // Recording and processing show a running count of seconds, so redraw once a second.
                if (this.Phase is CapturePhase.Recording or CapturePhase.Processing && ++this._ticks % 2 == 0)
                {
                    this._redraw();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "capture: poll failed");
            }
        }

        // Returns a reason when capture cannot start, otherwise null.
        public String Start(String kind)
        {
            Int32 gen;
            lock (this._lock)
            {
                if (this.Busy)
                {
                    return "busy";
                }

                if (this.LastAi == null)
                {
                    return "no AI app yet";
                }

                gen = ++this._generation;
                this.Kind = kind;
                this.Target = this.LastAi;
                this.Note = null;
                this.Phase = kind == "area" ? CapturePhase.Selecting : CapturePhase.Recording;
                this.StartedAt = DateTime.UtcNow;
            }

            this._redraw();
            Task.Run(() =>
            {
                if (kind == "area")
                {
                    this.SelectThenRecord(gen);
                }
                else
                {
                    this.BeginRecording(gen);
                }
            });
            return null;
        }

        private Boolean Current(Int32 gen) => !this._disposed && this._generation == gen;

        private void BeginRecording(Int32 gen)
        {
            if (!this.Current(gen))
            {
                return;
            }

            try
            {
                MacNative.FnHandsFree();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "capture: could not start Doing");
                this.Finish(gen, CapturePhase.NotSent, "Doing failed");
                return;
            }

            PluginLog.Info($"capture: recording for {AppName(this.Target)}");
        }

        // Brings the target forward and waits up to 1.5 s for it, asking once more halfway. Returns whether it is in front.
        private Boolean BringForward(String target)
        {
            MacNative.Activate(target);
            for (var i = 0; i < 15; i++)
            {
                if (MacNative.FrontmostBundleId() == target)
                {
                    return true;
                }

                if (i == 7)
                {
                    MacNative.Activate(target);
                }

                Thread.Sleep(100);
            }

            return MacNative.FrontmostBundleId() == target;
        }

        private void SelectThenRecord(Int32 gen)
        {
            try
            {
                var before = MacNative.ClipboardChangeCount();
                var psi = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
                psi.ArgumentList.Add("-i");   // drag to choose an area; Esc cancels
                psi.ArgumentList.Add("-c");   // put the image on the clipboard
                var selector = Process.Start(psi);
                lock (this._lock)
                {
                    this._selector = selector;
                    if (!this.Current(gen) || this.Phase != CapturePhase.Selecting)
                    {
                        TryKill(selector);   // cancelled while screencapture was starting
                        return;
                    }
                }

                selector.WaitForExit();
                lock (this._lock)
                {
                    if (!this.Current(gen) || this.Phase != CapturePhase.Selecting)
                    {
                        return;   // cancelled from the keypad
                    }
                }

                if (MacNative.ClipboardChangeCount() == before)
                {
                    this.Finish(gen, CapturePhase.Idle, null, "no area chosen");
                    return;   // Esc pressed: nothing captured, no recording started
                }

                // Attach the image to the receiving app's message box, then start recording.
                if (!this.BringForward(this.Target))
                {
                    this.Finish(gen, CapturePhase.NotSent, "image copied", $"{MacNative.FrontmostBundleId()} was in front instead of {this.Target}");
                    return;
                }

                Thread.Sleep(SettleMs);   // the app is in front; give its message box time to take keyboard focus
                if (!this.Current(gen))
                {
                    return;
                }

                this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.KeyV, ModifierKey.Command);
                Thread.Sleep(200);
                lock (this._lock)
                {
                    if (!this.Current(gen) || this.Phase != CapturePhase.Selecting)
                    {
                        PluginLog.Info("capture: cancelled after the image was pasted; no recording started");
                        return;
                    }

                    this.Phase = CapturePhase.Recording;
                    this.StartedAt = DateTime.UtcNow;
                }

                this._redraw();
                this.BeginRecording(gen);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "capture: area selection failed");
                this.Finish(gen, CapturePhase.NotSent, "failed");
            }
        }

        // Second press on Dictate, or Cancel while recording: stop, and let Doing transcribe and paste
        // into the receiving app's message box. The text is not sent.
        public void Stop()
        {
            Int32 gen;
            lock (this._lock)
            {
                if (this.Phase != CapturePhase.Recording)
                {
                    return;
                }

                gen = this._generation;
                this.Phase = CapturePhase.Processing;
                this.Step = "Transcribing";
                this.ProcessingSince = DateTime.UtcNow;
            }

            this._redraw();
            Task.Delay(45000).ContinueWith(_ =>
            {
                if (this.Phase == CapturePhase.Processing)
                {
                    PluginLog.Warning("capture: no result after 45 s");
                    this.Finish(gen, CapturePhase.NotSent, "timed out");
                }
            });
            Task.Run(() =>
            {
              try
              {
                var target = this.Target;
                var before = MacNative.ClipboardChangeCount();
                PluginLog.Info($"capture: stopping, clipboard count {before}, activating {target}");
                // Doing pastes into whatever app is in front, so make sure that is the target before stopping it.
                // Doing has to be stopped either way, or it keeps recording.
                var inFront = this.BringForward(target);
                if (!inFront)
                {
                    PluginLog.Warning($"capture: {target} is not in front ({MacNative.FrontmostBundleId()}); stopping Doing anyway");
                }

                Thread.Sleep(300);   // let the app take focus before Doing pastes into it
                if (this._disposed)
                {
                    return;
                }

                MacNative.FnTap();
                PluginLog.Info("capture: fn sent to stop Doing");

                // Doing copies the transcript to the clipboard and pastes it. Wait for that copy.
                var watch = Stopwatch.StartNew();
                while (MacNative.ClipboardChangeCount() == before && watch.Elapsed < TimeSpan.FromSeconds(25) && this.Current(gen))
                {
                    Thread.Sleep(100);
                }

                if (MacNative.ClipboardChangeCount() == before)
                {
                    PluginLog.Warning("capture: Doing produced no transcript within 25 s");
                    this.Finish(gen, CapturePhase.NotSent, "no words");
                    return;
                }

                PluginLog.Info("capture: transcript arrived");

                lock (this._lock)
                {
                    if (this.Current(gen))
                    {
                        this.Step = "Pasting";
                    }
                }

                this._redraw();
                Thread.Sleep(500);   // let the paste land

                var front = MacNative.FrontmostBundleId();
                if (front != target)
                {
                    this.Finish(gen, CapturePhase.NotSent, "text copied", $"{front} was in front instead of {target}");
                    return;
                }

                this.Finish(gen, CapturePhase.InBox, null);
              }
              catch (Exception ex)
              {
                PluginLog.Error(ex, "capture: stop failed");
                this.Finish(gen, CapturePhase.NotSent, "failed");
              }
            });
        }

        // Keep in wiki: copy one line of text, bring the last AI app forward, paste it and press Return,
        // checking at each step. Returns a reason when it could not send.
        public String SendText(String text)
        {
            var target = this.LastAi;
            if (target == null)
            {
                return "no AI app yet";
            }

            if (this.Busy)
            {
                return "busy";
            }

            if (Interlocked.CompareExchange(ref this._sending, 1, 0) != 0)
            {
                return "busy";
            }

            // Keep what the user had copied, and put it back after the paste.
            List<List<(String type, Byte[] data)>> saved = null;
            try
            {
                saved = MacNative.SaveClipboard();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "keep in wiki: could not save the clipboard; it will not be restored");
            }

            var ours = -1L;
            try
            {
                var before = MacNative.ClipboardChangeCount();
                var psi = new ProcessStartInfo("/usr/bin/pbcopy") { UseShellExecute = false, RedirectStandardInput = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Write(text);
                    p.StandardInput.Close();
                    if (!p.WaitForExit(3000) || p.ExitCode != 0 || MacNative.ClipboardChangeCount() == before)
                    {
                        // Without this check, Cmd+V would paste whatever was copied before.
                        PluginLog.Warning("keep in wiki: could not copy the instruction");
                        return "copy failed";
                    }
                }

                ours = MacNative.ClipboardChangeCount();

                if (!this.BringForward(target))
                {
                    return "app not open";
                }

                Thread.Sleep(SettleMs);
                this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.KeyV, ModifierKey.Command);
                Thread.Sleep(250);
                if (MacNative.FrontmostBundleId() != target)
                {
                    PluginLog.Warning("keep in wiki: the app changed before Return; not sent");
                    return "not sent";
                }

                MacNative.Return();
                PluginLog.Info($"keep in wiki: sent to {AppName(target)}");
                return null;
            }
            finally
            {
                if (ours >= 0 && saved != null)
                {
                    Thread.Sleep(400);   // the app reads the clipboard when it handles Cmd+V
                    // Only if nothing else was copied in the meantime; a newer copy is the user's and stays.
                    if (MacNative.ClipboardChangeCount() == ours)
                    {
                        var restored = MacNative.RestoreClipboard(saved);
                        PluginLog.Info($"keep in wiki: clipboard {(restored ? "restored" : "could not be restored")} ({saved.Count} item(s))");
                    }
                }

                Interlocked.Exchange(ref this._sending, 0);
            }
        }

        // Cancel: while choosing an area, stops the selection; while recording, stops exactly as Dictate does,
        // because Doing pastes its text either way.
        public Boolean Cancel()
        {
            Process selector;
            lock (this._lock)
            {
                switch (this.Phase)
                {
                    case CapturePhase.Selecting:
                        this.Phase = CapturePhase.Idle;
                        selector = this._selector;
                        break;
                    case CapturePhase.Recording:
                        selector = null;
                        break;
                    default:
                        return false;
                }
            }

            if (selector == null)
            {
                PluginLog.Info("capture: Cancel pressed while recording; stopping like Dictate");
                this.Stop();
                return true;
            }

            PluginLog.Info("capture: area selection cancelled");
            TryKill(selector);
            this._redraw();
            return true;
        }

        private static void TryKill(Process p)
        {
            try
            {
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                }
            }
            catch (Exception)
            {
            }
        }

        // Pressing a finished key clears its In box or Not sent state.
        public void Clear()
        {
            lock (this._lock)
            {
                if (this.Busy)
                {
                    return;
                }

                this.Phase = CapturePhase.Idle;
            }

            this._redraw();
        }

        // Ends capture gen. Work left over from an earlier capture, or from before an unload, changes nothing.
        private void Finish(Int32 gen, CapturePhase phase, String note, String detail = null)
        {
            lock (this._lock)
            {
                if (!this.Current(gen))
                {
                    PluginLog.Info($"capture: ignored a late {phase} from an earlier capture");
                    return;
                }

                this.Phase = phase;
                this.Note = note;
            }

            PluginLog.Info($"capture: finished {phase}{(note != null ? ", " + note : "")}{(detail != null ? " (" + detail + ")" : "")}");
            this._redraw();
            if (phase == CapturePhase.InBox)
            {
                Task.Delay(3000).ContinueWith(_ =>
                {
                    lock (this._lock)
                    {
                        if (!this.Current(gen) || this.Phase != CapturePhase.InBox)
                        {
                            return;
                        }

                        this.Phase = CapturePhase.Idle;
                    }

                    this._redraw();
                });
            }
        }

        public String Elapsed() => Clock(this.StartedAt);

        public String ProcessingElapsed() => Clock(this.ProcessingSince);

        private static String Clock(DateTime since)
        {
            var s = Math.Max(0, (Int32)(DateTime.UtcNow - since).TotalSeconds);
            return $"{s / 60}:{s % 60:00}";
        }

        // Stops the poll and any area selection. Background work checks _disposed before it presses keys.
        public void Dispose()
        {
            this._disposed = true;
            this._poll.Dispose();
            TryKill(this._selector);
        }
    }
}

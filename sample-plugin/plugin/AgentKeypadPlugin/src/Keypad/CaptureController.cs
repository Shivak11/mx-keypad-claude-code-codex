namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    internal enum CapturePhase { Idle, Selecting, Recording, Processing, Sent, NotSent }

    // Dictate and Select Area. The receiving app is fixed when capture starts: the last of
    // Claude or Codex that was in front. Doing records and transcribes; the plugin brings the
    // receiving app forward before Doing pastes, and presses Return only if it is still in front.
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

        public CaptureController(Plugin plugin, Action redraw)
        {
            this._plugin = plugin;
            this._redraw = redraw;
            this._poll = new Timer(_ => this.Poll(), null, 0, 500);
        }

        public static String AppName(String bundle) => bundle == ClaudeBundle ? "Claude" : bundle == CodexBundle ? "Codex" : null;

        public Boolean Busy => this.Phase is CapturePhase.Selecting or CapturePhase.Recording or CapturePhase.Processing;

        private Int32 _ticks;
        private Int32 _generation;
        private String _lastFront;

        private void Poll()
        {
            var front = MacNative.FrontmostBundleId();
            if (front == ClaudeBundle || front == CodexBundle)
            {
                if (this.LastAi != front)
                {
                    this.LastAi = front;
                }
            }

            // The switch key offers the other agent, so redraw when the front app changes.
            if (front != this._lastFront)
            {
                this._lastFront = front;
                this._redraw();
            }

            // Recording shows a running timer, so redraw once a second while recording.
            if (this.Phase == CapturePhase.Recording && ++this._ticks % 2 == 0)
            {
                this._redraw();
            }
        }

        // Returns a reason when capture cannot start, otherwise null.
        public String Start(String kind)
        {
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

                this.Kind = kind;
                this.Target = this.LastAi;
                this.Note = null;
                this.Phase = kind == "area" ? CapturePhase.Selecting : CapturePhase.Recording;
                this.StartedAt = DateTime.UtcNow;
            }

            this._redraw();
            Task.Run(kind == "area" ? this.SelectThenRecord : this.BeginRecording);
            return null;
        }

        private void BeginRecording()
        {
            try
            {
                MacNative.FnHandsFree();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "capture: could not start Doing");
                this.Finish(CapturePhase.NotSent, "Doing failed");
                return;
            }

            PluginLog.Info($"capture: recording for {AppName(this.Target)}");
        }

        private void SelectThenRecord()
        {
            var before = MacNative.ClipboardChangeCount();
            try
            {
                var psi = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
                psi.ArgumentList.Add("-i");   // drag to choose an area; Esc cancels
                psi.ArgumentList.Add("-c");   // put the image on the clipboard
                this._selector = Process.Start(psi);
                this._selector.WaitForExit();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "screencapture failed");
            }

            if (this.Phase != CapturePhase.Selecting)
            {
                return;   // cancelled from the keypad
            }

            if (MacNative.ClipboardChangeCount() == before)
            {
                this.Finish(CapturePhase.Idle, null);
                return;   // Esc pressed: nothing captured, no recording started
            }

            // Attach the image to the receiving app's message box, then start recording.
            MacNative.Activate(this.Target);
            Thread.Sleep(450);
            this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.KeyV, ModifierKey.Command);
            Thread.Sleep(200);
            this.Phase = CapturePhase.Recording;
            this.StartedAt = DateTime.UtcNow;
            this._redraw();
            this.BeginRecording();
        }

        // Second press: stop, let Doing transcribe and paste into the receiving app, then send.
        public void Stop(Boolean send = true)
        {
            lock (this._lock)
            {
                if (this.Phase != CapturePhase.Recording)
                {
                    return;
                }

                this.Phase = CapturePhase.Processing;
                this.Step = "Transcribing";
            }

            this._redraw();
            var generation = ++this._generation;
            Task.Delay(30000).ContinueWith(_ =>
            {
                if (this._generation == generation && this.Phase == CapturePhase.Processing)
                {
                    PluginLog.Warning("capture: no result after 30 s");
                    this.Finish(CapturePhase.NotSent, "timed out");
                }
            });
            Task.Run(() =>
            {
              try
              {
                var before = MacNative.ClipboardChangeCount();
                PluginLog.Info($"capture: stopping, clipboard count {before}, activating {this.Target}");
                MacNative.Activate(this.Target);
                Thread.Sleep(300);
                MacNative.FnTap();
                PluginLog.Info("capture: fn sent to stop Doing");

                // Doing copies the transcript to the clipboard and pastes it. Wait for that copy.
                var watch = Stopwatch.StartNew();
                while (MacNative.ClipboardChangeCount() == before && watch.Elapsed < TimeSpan.FromSeconds(25))
                {
                    Thread.Sleep(100);
                }

                if (MacNative.ClipboardChangeCount() == before)
                {
                    PluginLog.Warning("capture: Doing produced no transcript within 25 s");
                    this.Finish(CapturePhase.NotSent, "no words");
                    return;
                }

                PluginLog.Info("capture: transcript arrived");

                this.Step = "Sending";
                this._redraw();
                Thread.Sleep(500);   // let the paste land

                if (!send)
                {
                    this.Finish(CapturePhase.NotSent, "not sent");
                    return;
                }

                if (MacNative.FrontmostBundleId() != this.Target)
                {
                    this.Finish(CapturePhase.NotSent, "text copied");
                    return;
                }

                MacNative.Return();
                PluginLog.Info("capture: sent");
                this.Finish(CapturePhase.Sent, null);
              }
              catch (Exception ex)
              {
                PluginLog.Error(ex, "capture: stop failed");
                this.Finish(CapturePhase.NotSent, "failed");
              }
            });
        }

        // Sends one line of text to the last AI app: copy it, bring the app forward, paste, and press
        // Return only if that app is still in front. Returns a reason when it could not send.
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

            var psi = new ProcessStartInfo("/usr/bin/pbcopy") { UseShellExecute = false, RedirectStandardInput = true };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                p.WaitForExit(3000);
            }

            MacNative.Activate(target);
            Thread.Sleep(450);
            if (MacNative.FrontmostBundleId() != target)
            {
                return "app not open";
            }

            this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.KeyV, ModifierKey.Command);
            Thread.Sleep(250);
            MacNative.Return();
            return null;
        }

        // Cancel: while choosing an area, stops the selection; while recording, stops Doing
        // and leaves whatever it pastes unsent.
        public Boolean Cancel()
        {
            switch (this.Phase)
            {
                case CapturePhase.Selecting:
                    this.Phase = CapturePhase.Idle;
                    try
                    {
                        this._selector?.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    this._redraw();
                    return true;
                case CapturePhase.Recording:
                    this.Stop(send: false);
                    return true;
                default:
                    return false;
            }
        }

        // Pressing a finished key clears its Sent or Not sent state.
        public void Clear()
        {
            if (!this.Busy)
            {
                this.Phase = CapturePhase.Idle;
                this._redraw();
            }
        }

        private void Finish(CapturePhase phase, String note)
        {
            this.Phase = phase;
            this.Note = note;
            this._redraw();
            if (phase == CapturePhase.Sent)
            {
                Task.Delay(3000).ContinueWith(_ =>
                {
                    if (this.Phase == CapturePhase.Sent)
                    {
                        this.Phase = CapturePhase.Idle;
                        this._redraw();
                    }
                });
            }
        }

        public String Elapsed()
        {
            var s = (Int32)(DateTime.UtcNow - this.StartedAt).TotalSeconds;
            return $"{s / 60}:{s % 60:00}";
        }

        public void Dispose() => this._poll.Dispose();
    }
}

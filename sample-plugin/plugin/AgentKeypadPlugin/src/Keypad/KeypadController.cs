namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    // Owns page state for the two keypad groups and turns presses into actions and feedback.
    // Group "h" is the Options+ page holding Home keys 1-9: Home, Waiting, Ready, Codex, Claude Code.
    // Group "m" is the Options+ page holding Moves keys 1-9: Moves and Usage.
    //
    // Layout rule for every page below Home or Moves: keys 1-6 hold content, key 7 is More when
    // there is another page of content, key 8 is Back and key 9 is Home.
    internal sealed class KeypadController
    {
        private const Int32 PerPage = 6;

        // With no key pressed for this long, the keypad goes back to the Home screen on the first page.
        private static readonly TimeSpan IdleReturnAfter = TimeSpan.FromSeconds(60);
        private const Int32 IdleCheckMs = 5000;

        public void Dispose()
        {
            this._disposed = true;
            this._idleTimer.Dispose();
            this._usageTimer.Dispose();
            this._animTimer.Dispose();
            this._capture.Dispose();
            this._sessions.Dispose();
        }

        private readonly Plugin _plugin;
        private readonly Action _redraw;
        private readonly Object _lock = new();
        private readonly SessionStore _sessions;
        private readonly CaptureController _capture;
        private String _lastAiBeforeDia;
        private readonly Timer _usageTimer;
        private readonly Timer _animTimer;
        private readonly Action<String> _redrawKey;
        private readonly Dictionary<String, KeyAnim> _anims = new();            // one-off effects per key id
        private readonly Dictionary<String, Int32> _lastBadge = new();          // badge per key id, to roll changes
        private readonly Dictionary<String, String> _lastStatus = new();        // status per session, to notice a finish
        private readonly Dictionary<String, Double> _arrivedAt = new();         // when each session finished
        private Double _usageShownAt = -1e9;
        private readonly Timer _idleTimer;
        private volatile Boolean _disposed;
        private Int32 _diaBusy;
        private readonly Dictionary<String, (KeyFace face, Int64 at)> _downFaces = new();   // the face each key showed when pressed down
        private readonly Dictionary<String, Int32> _labelTokens = new();                     // newest Deny or Flash per key
        private Int32 _labelToken;
        private DateTime _usageReadAt = DateTime.MinValue;

        // Idle return state, guarded by _lock. Groups: "h" Home keys, "m" Moves keys, "x" Go to Home page,
        // "none" when Options+ drew no key of this plugin (a page such as Logitech's defaults).
        private DateTime _lastActivity = DateTime.UtcNow;
        private Boolean _idleReturned;
        private Boolean _idleCheckPending;
        private readonly HashSet<String> _renderedGroups = new();
        private String _shownGroup;

        private readonly Dictionary<String, List<String>> _stacks = new()
        {
            ["h"] = new() { "home" },
            ["m"] = new() { "moves" },
        };

        private readonly Dictionary<String, Int32> _pageNo = new() { ["h"] = 0, ["m"] = 0 };
        private readonly Dictionary<String, String> _flash = new();   // key id -> temporary label ("Opened", "Done")
        private readonly Dictionary<String, String> _denied = new();  // key id -> reason shown while denied
        private readonly Dictionary<String, Int64> _pressTicks = new();
        private (Limit five, Limit week) _codexUsage = (new Limit(), new Limit());
        private (Limit five, Limit week) _claudeUsage = (new Limit(), new Limit());

        public KeypadController(Plugin plugin, Action redraw, Action<String> redrawKey)
        {
            this._plugin = plugin;
            this._redrawKey = id =>
            {
                if (!this._disposed)
                {
                    redrawKey(id);
                }
            };
            this._redraw = () =>
            {
                if (!this._disposed)
                {
                    redraw();
                }
            };
            this._sessions = new SessionStore(redraw);
            this._capture = new CaptureController(plugin, redraw);
            this._usageTimer = new Timer(_ => this.RefreshUsage(), null, 0, 600000);
            this._animTimer = new Timer(_ => this.AnimTick(), null, Motion.FrameMs, Motion.FrameMs);
            this._idleTimer = new Timer(_ => this.IdleTick(), null, IdleCheckMs, IdleCheckMs);
        }

        // ---------- parameters ----------

        public static IEnumerable<(String id, String name)> Slots()
        {
            for (var i = 0; i < 9; i++)
            {
                yield return ($"h{i}", $"Home key {i + 1}");
            }

            for (var i = 0; i < 9; i++)
            {
                yield return ($"m{i}", $"Moves key {i + 1}");
            }

            // A Home key for any other keypad page, such as Logitech's default media page.
            yield return ("x0", "Go to Home page");
        }

        private static (String group, Int32 slot) Parse(String id) =>
            id != null && id.Length == 2 && (id[0] == 'h' || id[0] == 'm') && Char.IsDigit(id[1]) ? (id[0].ToString(), id[1] - '0') : (null, -1);

        // ---------- rendering ----------

        public BitmapImage Render(String id, PluginImageSize size)
        {
            if (String.IsNullOrEmpty(id))
            {
                return KeyRenderer.Draw(KeyFace.Empty(), size, false, false);
            }

            lock (this._lock)
            {
                // Options+ asks only for the keys on the page it is showing, so this tells the idle check which page that is.
                this._renderedGroups.Add(id.Substring(0, 1));
            }

            var face = this.FaceFor(id);
            String deniedWhy;
            KeyAnim anim;
            lock (this._lock)
            {
                this._denied.TryGetValue(id, out deniedWhy);
                anim = this.AnimFor(id, face);
                if (anim.Now - anim.PressAt < 200 && this._pressTicks.TryGetValue(id, out var t))
                {
                    PluginLog.Verbose($"redraw {id} {Stopwatch.GetElapsedTime(t).TotalMilliseconds:F0} ms after press");
                }
            }

            if (deniedWhy != null)
            {
                face.Why = deniedWhy;
            }

            KeyRenderer.DebugName = id;
            try
            {
                return KeyRenderer.Draw(face, size, deniedWhy != null, anim);
            }
            catch (Exception ex)
            {
                // A key that fails to draw would otherwise show Logitech's warning sign.
                PluginLog.Error(ex, $"key {id} failed to draw");
                return KeyRenderer.Draw(KeyFace.Empty(), size, false, false);
            }
        }

        // Debug only: draws every screen of both groups and logs any key that fails to draw.
        public void DrawAllScreens()
        {
            this.RefreshUsage();
            foreach (var (group, page) in new[] { ("m", "moves"), ("m", "usage"), ("h", "home"), ("h", "waiting"), ("h", "ready"), ("h", "codex"), ("h", "claude") })
            {
                lock (this._lock)
                {
                    this._stacks[group].Clear();
                    this._stacks[group].Add(group == "h" ? "home" : "moves");
                    if (page is not ("home" or "moves"))
                    {
                        this._stacks[group].Add(page);
                    }
                }

                for (var i = 0; i < 9; i++)
                {
                    var id = $"{group}{i}";
                    foreach (var size in new[] { PluginImageSize.Width90, PluginImageSize.Width116 })
                    {
                        try
                        {
                            KeyRenderer.DebugName = $"screen-{page}-{id}";
                            KeyRenderer.Draw(this.FaceFor(id), size, false, false);
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Error(ex, $"screen {page} key {id} size {size} failed to draw");
                        }
                    }
                }
            }

            this.Go("m", "moves");
            this.Go("h", "home");
        }

        private KeyFace FaceFor(String id)
        {
            if (id == "x0")
            {
                return Nav("home", "Home", () => this.ToHomePage("x0"));
            }

            var (group, slot) = Parse(id);
            if (group == null)
            {
                return KeyFace.Empty();
            }

            KeyFace[] faces;
            try
            {
                faces = this.CurrentPage(group) switch
                {
                    "home" => this.HomeFaces(),
                    "moves" => this.MovesFaces(),
                    "usage" => this.UsageFaces(),
                    "waiting" => this.ListFaces("h", this._sessions.Waiting(), "None waiting"),
                    "ready" => this.ListFaces("h", this._sessions.ReadyList(), "None ready"),
                    "codex" => this.ListFaces("h", this._sessions.ForApp("codex"), "No sessions"),
                    "claude" => this.ListFaces("h", this._sessions.ForApp("claude"), "No sessions"),
                    _ => this.HomeFaces(),
                };
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"could not build page for {id}");
                return KeyFace.Empty();
            }

            var face = faces[slot];
            String label;
            lock (this._lock)
            {
                this._flash.TryGetValue(id, out label);
            }

            return label != null
                ? new KeyFace { Icon = "check", IconColor = Colors.Green, Label = label, Tone = KeyTone.Done, Act = face.Act }
                : face;
        }

        private String CurrentPage(String group)
        {
            lock (this._lock)
            {
                return this._stacks[group][^1];
            }
        }

        // ---------- pages ----------

        private KeyFace[] HomeFaces()
        {
            var waiting = this._sessions.Waiting();
            var ready = this._sessions.ReadyList();
            var all = this._sessions.All();
            // App keys count sessions that are working right now; the badge turns amber when one of them is waiting for you.
            Int32 Needs(String app) => all.Count(s => s.App == app && (s.State == "working" || s.State == "waiting"));
            String NeedsColor(String app) => waiting.Any(s => s.App == app) ? Colors.Amber : Colors.Blue;

            return new[]
            {
                new KeyFace { Icon = "bell", IconColor = Colors.Amber, Label = "Waiting", Badge = waiting.Count, BadgeColor = Colors.Amber, Act = () => this.Go("h", "waiting") },
                new KeyFace { Icon = "codex", IconColor = Colors.Teal, Label = "Codex", Badge = Needs("codex"), BadgeColor = NeedsColor("codex"), Act = () => this.Go("h", "codex") },
                new KeyFace { Icon = "claude", IconColor = Colors.Orange, Label = "Claude Code", Badge = Needs("claude"), BadgeColor = NeedsColor("claude"), Act = () => this.Go("h", "claude") },
                this.CaptureFace("area", "h3"),
                this.CaptureFace("voice", "h4"),
                this._capture.Phase == CapturePhase.Recording
                    ? new KeyFace { Icon = "stop", IconColor = Colors.Red, Label = "Stop", Act = () => this._capture.Cancel() }
                    : this._capture.Phase is CapturePhase.Selecting or CapturePhase.Processing
                    ? new KeyFace { Icon = "x", IconColor = Colors.Slate, Label = "Cancel", Act = () => this._capture.Cancel() }
                    : new KeyFace { Icon = "x", IconColor = Colors.Slate, Label = "Cancel", Tone = KeyTone.Off, Why = "nothing to stop" },
                new KeyFace { Icon = "dia", IconColor = Colors.Violet, Label = "Open in Dia", Act = () => this.OpenInDia("h6") },
                this.SwitchAgentFace(),
                new KeyFace { Icon = "result", IconColor = Colors.Green, Label = "Ready", Badge = ready.Count, BadgeColor = Colors.Green, Act = () => this.Go("h", "ready") },
            };
        }

        // Select Area and Dictate share one capture. The key that started it shows its progress.
        private KeyFace CaptureFace(String kind, String keyId)
        {
            var c = this._capture;
            var idle = kind == "area"
                ? new KeyFace { Icon = "area", IconColor = Colors.Blue, Label = "Select Area" }
                : new KeyFace { Icon = "mic", IconColor = Colors.Red, Label = "Dictate" };
            idle.Act = () =>
            {
                var why = c.Start(kind);
                if (why != null)
                {
                    this.Deny(keyId, why);
                }
            };

            if (c.Kind != kind || c.Phase == CapturePhase.Idle)
            {
                if (c.Busy)
                {
                    idle.Tone = KeyTone.Off;
                    idle.Act = null;
                    idle.Why = "busy";
                }

                return idle;
            }

            var to = CaptureController.AppName(c.Target);
            return c.Phase switch
            {
                CapturePhase.Selecting => new KeyFace { Icon = "area", IconColor = Colors.Blue, Label = "Drag area", Small = "Esc to cancel", Tone = KeyTone.Selecting, Live = "selecting", LiveSince = c.PhaseSince },
                CapturePhase.Recording => new KeyFace { Icon = "stop", IconColor = Colors.Red, Label = "Stop", Small = $"{c.Elapsed()} · {to}", Tone = KeyTone.Recording, Live = "recording", LiveSince = c.PhaseSince, Act = () => c.Stop() },
                CapturePhase.Processing => new KeyFace { Icon = "spin", IconColor = Colors.Amber, Label = c.Step ?? "Working", Small = $"{c.ProcessingElapsed()} · {to}", Tone = KeyTone.Sending, Live = "processing", LiveSince = c.PhaseSince, Act = () => c.Cancel() },
                CapturePhase.InBox => new KeyFace { Icon = "check", IconColor = Colors.Green, Label = "In box", Small = "Enter to send", Tone = KeyTone.Sent, Live = "inbox", LiveSince = c.PhaseSince, LiveLength = 3000, Act = c.Clear },
                CapturePhase.NotSent => new KeyFace { Icon = "clip", IconColor = Colors.Slate, Label = "Not sent", Small = c.Note, Tone = KeyTone.NotSent, Live = "notsent", LiveSince = c.PhaseSince, LiveLength = 5000, Act = c.Clear },
                _ => idle,
            };
        }

        // Opens the page shown in the built-in browser panel of the Codex or Claude window in Dia.
        private void OpenInDia(String keyId)
        {
            var front = MacNative.FrontmostBundleId();
            var app = front == CaptureController.ClaudeBundle || front == CaptureController.CodexBundle ? front : this._capture.LastAi;
            if (app == null)
            {
                this.Deny(keyId, "no AI window");
                return;
            }

            if (Interlocked.CompareExchange(ref this._diaBusy, 1, 0) != 0)
            {
                this.Deny(keyId, "busy");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var (url, why) = BrowserPanel.CurrentUrl(app);
                    if (url == null)
                    {
                        this.Deny(keyId, why);
                        return;
                    }

                    this._lastAiBeforeDia = app;
                    MacNative.Run("/usr/bin/open", "-b", "company.thebrowser.dia", url);
                    this.Flash(keyId, "Opened");
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "open in Dia failed");
                    this.Deny(keyId, "failed");
                }
                finally
                {
                    Interlocked.Exchange(ref this._diaBusy, 0);
                }
            });
        }

        private void SaveNote()
        {
            Task.Run(() =>
            {
                var why = this._capture.SendText("Save the point we are on right now to my notes.");
                if (why != null)
                {
                    this.Deny("m0", why);
                }
                else
                {
                    this.Flash("m0", "Sent to " + CaptureController.AppName(this._capture.LastAi));
                }
            });
        }

        // Switches between Claude and Codex: the key always offers the agent you are not in.
        private String SwitchTarget()
        {
            var front = MacNative.FrontmostBundleId();
            if (front == CaptureController.ClaudeBundle)
            {
                return CaptureController.CodexBundle;
            }

            if (front == CaptureController.CodexBundle)
            {
                return CaptureController.ClaudeBundle;
            }

            return this._capture.LastAi ?? CaptureController.ClaudeBundle;
        }

        private KeyFace SwitchAgentFace()
        {
            var target = this.SwitchTarget();
            var claude = target == CaptureController.ClaudeBundle;
            return new KeyFace
            {
                Icon = "swap",
                IconColor = claude ? Colors.Orange : Colors.Teal,
                Label = claude ? "To Claude" : "To Codex",
                Act = () => Task.Run(() =>
                {
                    MacNative.Activate(this.SwitchTarget());
                    this._redraw();
                }),
            };
        }

        private KeyFace[] MovesFaces() => new[]
        {
            new KeyFace { Icon = "bookmark", IconColor = Colors.Teal, Label = "Save note", Act = this.SaveNote },
            new KeyFace { Icon = "gauge", IconColor = Colors.Violet, Label = "Usage", Act = this.OpenUsage },
            new KeyFace { Icon = "clip", IconColor = Colors.Cyan, Label = "Clipboard", Tone = KeyTone.Off, Why = "being fixed" },
            new KeyFace { Icon = "camera", IconColor = Colors.Blue, Label = "Screenshot", Act = () => this.Run("m3", "Xnapper", this.Screenshot) },
            new KeyFace { Icon = "smile", IconColor = Colors.Yellow, Label = "Emojis", Act = () => this.Run("m4", "Opened", this.Emojis) },
            new KeyFace { Icon = "folder", IconColor = Colors.Blue, Label = "Downloads", Act = () => this.Run("m5", "Opened", () => OpenFolder("Downloads")) },
            new KeyFace { Icon = "folder", IconColor = Colors.Orange, Label = "Projects", Act = () => this.Run("m6", "Opened", () => OpenFolder("Projects")) },
            Nav("back", "Back", () => this.ToPreviousPage("m7")),
            Nav("home", "Home", () => this.ToHomePage("m8")),
        };

        private KeyFace[] UsageFaces()
        {
            var (five, week) = this._codexUsage;
            var (cFive, cWeek) = this._claudeUsage;
            KeyFace Stat(String app, String title, Limit l, String fallback) => new()
            {
                Kind = KeyKind.Stat,
                App = app,
                Label = title,
                Percent = l?.Percent,
                Value = l?.Percent is Double p ? $"{p:0}%" : null,
                Small = l?.Percent != null ? (l.Resets != null ? "resets " + l.Resets : null) : fallback,
                ShownAt = this._usageShownAt,
                Act = () => { },
            };

            return new[]
            {
                Stat("claude", "5 hours", cFive, cFive.Known ? "no data" : cFive.Why ?? "no data yet"),
                Stat("claude", "This week", cWeek, cWeek.Known ? "no data" : cWeek.Why ?? "no data yet"),
                KeyFace.Empty(),
                Stat("codex", "5 hours", five, five.Known ? "not on plan" : "updating"),
                Stat("codex", "This week", week, week.Known ? "no data" : "updating"),
                KeyFace.Empty(),
                KeyFace.Empty(),
                Nav("back", "Back", () => this.Back("m")),
                Nav("home", "Home", () => this.ToHomePage("m8")),
            };
        }

        // Content list with the shared navigation row: More, Back, Home.
        private KeyFace[] ListFaces(String group, List<Session> items, String emptyText)
        {
            var faces = new KeyFace[9];
            var pages = Math.Max(1, (Int32)Math.Ceiling(items.Count / (Double)PerPage));
            Int32 page;
            lock (this._lock)
            {
                if (this._pageNo[group] >= pages)
                {
                    this._pageNo[group] = 0;
                }

                page = this._pageNo[group];
            }

            var slice = items.Skip(page * PerPage).Take(PerPage).ToList();
            for (var i = 0; i < PerPage; i++)
            {
                faces[i] = i < slice.Count ? this.SessionFace(slice[i], $"{group}{i}") : KeyFace.Empty();
            }

            if (items.Count == 0)
            {
                faces[0] = new KeyFace { Kind = KeyKind.Note, Label = emptyText };
            }

            // More moves forward one page and Back moves back one page, so the last page shows no More key.
            faces[6] = page + 1 < pages
                ? new KeyFace { Kind = KeyKind.Nav, Icon = "next", Label = "More", Small = $"{page + 1} of {pages}", Act = () => this.ChangeScreen(group, () => { lock (this._lock) { this._pageNo[group] = Math.Min(this._pageNo[group] + 1, pages - 1); } }) }
                : KeyFace.Empty();
            faces[7] = Nav("back", "Back", () => this.Back(group));
            faces[8] = Nav("home", "Home", () => this.Go(group, "home"));
            return faces;
        }

        private KeyFace SessionFace(Session s, String keyId)
        {
            String status, color;
            if (s.State == "waiting")
            {
                status = s.Wait ?? "Waiting";
                color = Colors.Amber;
            }
            else if (s.Ready)
            {
                status = "Ready";
                color = Colors.Green;
            }
            else if (s.State == "working")
            {
                status = "Working";
                color = Colors.Blue;
            }
            else if (s.State == "idle")
            {
                status = "Idle";
                color = Colors.Muted;
            }
            else
            {
                status = "Open";   // known only from the app's own records, no hook report yet
                color = Colors.Muted;
            }

            var since = s.State == "waiting" && s.WaitSince > 0 ? s.WaitSince : s.Ready && s.ReadyAt > 0 ? s.ReadyAt : s.Updated;
            return new KeyFace
            {
                Kind = KeyKind.Session,
                SessionId = s.App + "-" + s.Id,
                App = s.App,
                Label = s.Title,
                Status = status,
                StatusColor = color,
                Age = SessionStore.Age(since),
                Act = () =>
                {
                    var exact = this._sessions.Open(s);
                    if (s.Ready && s.State != "waiting")
                    {
                        this._sessions.MarkRead(s);
                    }

                    this.Flash(keyId, exact ? "Opened" : "App opened");
                },
            };
        }

        private static KeyFace Nav(String icon, String label, Action act) => new() { Kind = KeyKind.Nav, Icon = icon, Label = label, Act = act };

        // ---------- presses ----------

        // Physical key down: show press feedback at once, before the action runs.
        public void PressDown(String id)
        {
            var face = this.FaceFor(id);
            lock (this._lock)
            {
                this._downFaces[id] = (face, Stopwatch.GetTimestamp());
                this._pressTicks[id] = Stopwatch.GetTimestamp();
                this.Anim(id).PressAt = Motion.Now;   // dip and ring, drawn by the animation timer
                this.MarkActivity();
            }

            this._redrawKey(id);
        }

        // Runs the action of the face the key showed on the downstroke, so a list that reorders or a state that
        // times out between down and up cannot turn the press into a different action.
        public void Press(String id)
        {
            KeyFace face = null;
            lock (this._lock)
            {
                this.MarkActivity();
                if (this._downFaces.Remove(id, out var down) && Stopwatch.GetElapsedTime(down.at) < TimeSpan.FromSeconds(5))
                {
                    face = down.face;
                }
            }

            face ??= this.FaceFor(id);
            if (face.Tone == KeyTone.Empty || face.Kind == KeyKind.Note)
            {
                return;
            }

            if (face.Act == null)
            {
                this.Deny(id, face.Why ?? "unavailable");
                return;
            }

            try
            {
                face.Act();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"action failed on {id}");
                this.Deny(id, "failed");
            }

            this._redraw();
        }

        private void Go(String group, String page) => this.ChangeScreen(group, () => this.GoCore(group, page));

        private void GoCore(String group, String page)
        {
            lock (this._lock)
            {
                var s = this._stacks[group];
                if (page == "home" || page == "moves")
                {
                    s.Clear();
                    s.Add(page);
                }
                else
                {
                    var i = s.IndexOf(page);
                    if (i >= 0)
                    {
                        s.RemoveRange(i + 1, s.Count - i - 1);
                    }
                    else
                    {
                        s.Add(page);
                    }
                }

                this._pageNo[group] = 0;
            }
        }

        private void Back(String group) => this.ChangeScreen(group, () => this.BackCore(group));

        private void BackCore(String group)
        {
            lock (this._lock)
            {
                // Back retraces one step: the previous page of a list first, then the previous screen.
                if (this._pageNo[group] > 0)
                {
                    this._pageNo[group]--;
                    return;
                }

                var s = this._stacks[group];
                if (s.Count > 1)
                {
                    s.RemoveAt(s.Count - 1);
                }
            }
        }

        // Caller holds _lock. The key's one-off effects, plus what follows from its face: a badge that changed,
        // a session that just finished, and the countdown before the idle return.
        private KeyAnim AnimFor(String id, KeyFace face)
        {
            var now = Motion.Now;
            var stored = this._anims.GetValueOrDefault(id);
            var anim = new KeyAnim { Now = now };
            if (stored != null)
            {
                anim.PressAt = stored.PressAt;
                anim.FlashAt = stored.FlashAt;
                anim.BadgeAt = stored.BadgeAt;
                anim.BadgeFrom = stored.BadgeFrom;
            }

            // A badge that changed since this key was last drawn rolls to its new number.
            if (face.Badge > 0 || this._lastBadge.ContainsKey(id))
            {
                if (this._lastBadge.TryGetValue(id, out var before) && before != face.Badge)
                {
                    stored = this.Anim(id);
                    stored.BadgeAt = anim.BadgeAt = now;
                    stored.BadgeFrom = anim.BadgeFrom = before;
                }

                this._lastBadge[id] = face.Badge;
            }

            // A session that turns Ready plays its finish once, on whichever key shows it.
            if (face.Kind == KeyKind.Session && face.SessionId != null)
            {
                if (this._lastStatus.TryGetValue(face.SessionId, out var was) && was != "Ready" && face.Status == "Ready")
                {
                    this._arrivedAt[face.SessionId] = now;
                }

                this._lastStatus[face.SessionId] = face.Status;
                anim.ArriveAt = this._arrivedAt.GetValueOrDefault(face.SessionId, -1e9);
            }

            // The last five seconds before the idle return: the Home key drains a ring, the other keys dim.
            var left = (IdleReturnAfter - (DateTime.UtcNow - this._lastActivity)).TotalMilliseconds;
            var group = id.Substring(0, 1);
            var onHome = this._shownGroup == "h" && this._stacks["h"].Count == 1 && this._pageNo["h"] == 0;
            if (!this._idleReturned && !onHome && !this._capture.Busy && left > 0 && left < IdleWarnMs && (this._shownGroup == null || this._shownGroup == group))
            {
                var frac = left / IdleWarnMs;
                if (id is "h8" or "m8" or "x0" && face.Icon == "home")
                {
                    anim.IdleRing = frac;
                    anim.IdleSmall = $"in {Math.Ceiling(left / 1000):0} s";
                }
                else
                {
                    anim.Dim = (1 - frac) * 0.45;
                }
            }

            return anim;
        }

        // Caller holds _lock.
        private KeyAnim Anim(String id)
        {
            if (!this._anims.TryGetValue(id, out var a))
            {
                a = this._anims[id] = new KeyAnim();
            }

            return a;
        }

        private const Double IdleWarnMs = 5000;

        // Eight times a second: redraw only the keys on the visible page that are moving.
        private void AnimTick()
        {
            try
            {
                String shown;
                lock (this._lock)
                {
                    shown = this._shownGroup;
                }

                var ids = shown switch
                {
                    "h" => Enumerable.Range(0, 9).Select(i => $"h{i}"),
                    "m" => Enumerable.Range(0, 9).Select(i => $"m{i}"),
                    "x" => new[] { "x0" },
                    "none" => Enumerable.Empty<String>(),
                    _ => Enumerable.Range(0, 9).SelectMany(i => new[] { $"h{i}", $"m{i}" }),
                };
                var now = Motion.Now;
                foreach (var id in ids)
                {
                    var face = this.FaceFor(id);
                    Boolean moving;
                    lock (this._lock)
                    {
                        moving = face.Animated(now) || this.AnimFor(id, face).Playing;
                    }

                    if (moving)
                    {
                        this._redrawKey(id);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "animation tick failed");
            }
        }

        // Changes what one group shows. The new screen appears at once: a flip between screens looked slow at
        // the keypad's frame rate. Badges on a new screen do not roll, since their numbers did not change.
        private void ChangeScreen(String group, Action change)
        {
            change();
            lock (this._lock)
            {
                for (var i = 0; i < 9; i++)
                {
                    this._lastBadge.Remove($"{group}{i}");
                }
            }
        }

        // Caller holds _lock. A press, a change of page and a running capture each restart the idle countdown.
        private void MarkActivity()
        {
            this._lastActivity = DateTime.UtcNow;
            this._idleReturned = false;
        }

        // Every 5 seconds: work out which page Options+ is showing from the keys it drew since the last check,
        // then return to Home once the keypad has been idle for IdleReturnAfter on any other screen.
        // The redraw at the end asks Options+ to draw the visible page again, which answers the next check.
        private void IdleTick()
        {
            try
            {
                String shown, why = null;
                lock (this._lock)
                {
                    // Two groups in one window means a page change mid-window or two kinds of key on one page: keep the last reading.
                    shown = this._renderedGroups.Count switch
                    {
                        0 => "none",
                        1 => this._renderedGroups.First(),
                        _ => this._shownGroup ?? "none",
                    };
                    this._renderedGroups.Clear();
                    if (shown != this._shownGroup)
                    {
                        if (this._shownGroup != null)
                        {
                            PluginLog.Info($"idle check: keypad now shows {GroupName(shown)}");
                            this.MarkActivity();
                        }

                        this._shownGroup = shown;
                    }

                    if (this._idleCheckPending)
                    {
                        this._idleCheckPending = false;
                        if (shown != "h")
                        {
                            PluginLog.Warning($"idle return: asked Options+ for the first page, but it still shows {GroupName(shown)}");
                        }
                    }

                    if (this._capture.Busy)
                    {
                        this.MarkActivity();
                    }

                    if (!this._idleReturned && DateTime.UtcNow - this._lastActivity >= IdleReturnAfter)
                    {
                        this._idleReturned = true;
                        var screens = this._stacks["h"][^1] + "/" + this._stacks["m"][^1];
                        var onHome = shown == "h" && this._stacks["h"].Count == 1 && this._pageNo["h"] == 0;
                        if (!onHome)
                        {
                            why = $"{GroupName(shown)}, screens {screens}";
                        }
                        else if (this._stacks["m"].Count > 1)
                        {
                            // Already on Home: only make sure Moves opens on its first screen next time.
                            this._stacks["m"].RemoveRange(1, this._stacks["m"].Count - 1);
                        }
                    }
                }

                if (why != null)
                {
                    PluginLog.Info($"idle return: no key pressed for {IdleReturnAfter.TotalSeconds:0} s on {why}; going to Home");
                    lock (this._lock)
                    {
                        this._idleCheckPending = true;
                    }

                    this.ToHomePage("idle", quiet: true);
                }

                this._redraw();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "idle check failed");
            }
        }

        private static String GroupName(String group) => group switch
        {
            "h" => "the Home page",
            "m" => "the Moves page",
            "x" => "a page with the Go to Home page key",
            _ => "a page without plugin keys",
        };

        // Usage is read in the background every 10 minutes, and again on opening if the last read is over 2 minutes old.
        private void OpenUsage()
        {
            this._usageShownAt = Motion.Now;
            this.Go("m", "usage");
            if (DateTime.UtcNow - this._usageReadAt > TimeSpan.FromMinutes(2))
            {
                Task.Run(this.RefreshUsage);
            }
        }

        private void RefreshUsage()
        {
            try
            {
                this._codexUsage = UsageStore.Codex();
                this._claudeUsage = UsageStore.Claude();
                this._usageReadAt = DateTime.UtcNow;
                if (this.CurrentPage("m") == "usage")
                {
                    this._redraw();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "usage refresh failed");
            }
        }

        // Home on any page outside the Home keys: reset both screens and show the first keypad page,
        // which holds Waiting, Codex and Claude Code.
        private void ToHomePage(String keyId, Boolean quiet = false)
        {
            this.Go("m", "moves");
            this.Go("h", "home");
            this.SwitchPage(keyId, "@FirstTouchPage", quiet);
        }

        // Back on the Moves page: the Home keys are on the page before it.
        private void ToPreviousPage(String keyId)
        {
            this.Go("m", "moves");
            this.Go("h", "home");
            this.SwitchPage(keyId, "@PreviousTouchPage");
        }

        // ExecuteGenericAction only queues the request; Options+ carries it out afterwards with no link to a key press,
        // so the same call works from the idle timer. "ran" means the request was sent.
        private void SwitchPage(String keyId, String action, Boolean quiet = false)
        {
            try
            {
                this._plugin.ExecuteGenericAction(action, null, 0);
                PluginLog.Info($"{keyId}: ran {action}");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{keyId}: {action} failed");
                if (!quiet)
                {
                    this.Deny(keyId, "use ‹ › keys");
                }
            }
        }

        // Shows a reason on a key for 1.2 s. A newer Deny or Flash on the same key keeps its own full time.
        private void Deny(String id, String why)
        {
            Int32 token;
            lock (this._lock)
            {
                this._denied[id] = why;
                token = this._labelTokens[id] = ++this._labelToken;
            }

            this._redraw();
            this.After(1200, () =>
            {
                lock (this._lock)
                {
                    if (this._labelTokens.GetValueOrDefault(id) == token)
                    {
                        this._denied.Remove(id);
                    }
                }
            });
        }

        private void Flash(String id, String label)
        {
            Int32 token;
            lock (this._lock)
            {
                this._flash[id] = label;
                this.Anim(id).FlashAt = Motion.Now;
                token = this._labelTokens[id] = ++this._labelToken;
            }

            this._redraw();
            this.After(1000, () =>
            {
                lock (this._lock)
                {
                    if (this._labelTokens.GetValueOrDefault(id) == token)
                    {
                        this._flash.Remove(id);
                    }
                }
            });
        }

        // Runs an instant action and shows a green confirmation on the key for one second.
        private void Run(String id, String label, Action action)
        {
            action();
            this.Flash(id, label);
        }

        private void After(Int32 ms, Action action) =>
            Task.Delay(ms).ContinueWith(_ =>
            {
                action();
                this._redraw();
            });

        // ---------- Mac actions ----------

        private static void OpenFolder(String name)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name);
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }

        // Control+Command+Option+4 is the capture shortcut used by Xnapper; change it for your screenshot app.
        private void Screenshot() =>
            this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Key4, ModifierKey.Control | ModifierKey.Command | ModifierKey.Option);

        private void Emojis() =>
            this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Space, ModifierKey.Control | ModifierKey.Command);

        // Spotlight's clipboard history: open Spotlight, then switch to its clipboard view.
        private void OpenClipboardHistory()
        {
            this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Space, ModifierKey.Command);
            Thread.Sleep(250);
            this._plugin.ClientApplication.SendKeyboardShortcut(VirtualKeyCode.Key4, ModifierKey.Command);
        }
    }

}

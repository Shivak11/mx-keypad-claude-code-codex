namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;

    internal sealed class Session
    {
        public String App;        // "claude" or "codex"
        public String Id;         // id from the hook (Claude CLI session id or Codex thread id)
        public String State;      // working, waiting, idle
        public String Wait;       // Permission, Approval, Question
        public Boolean Ready;     // finished a turn that has not been opened since, from the keypad or (Claude) in the app
        public Double Updated;
        public Double ReadyAt;
        public Double WaitSince;
        public String Cwd;
        public String Title;
        public String LinkId;     // Claude Desktop session id used by claude://code/continue
        public String FilePath;

        public String AppName => this.App == "claude" ? "Claude" : "Codex";

        public Int32 Rank => this.State == "waiting" ? 0 : this.Ready ? 1 : this.State == "working" ? 2 : this.State == "idle" ? 3 : 4;
    }

    // Reads the per-session files written by keypad_hook.py and adds the titles each app shows.
    internal sealed class SessionStore : IDisposable
    {
        private static readonly String Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly String Dir = Path.Combine(Home, ".mx-keypad", "sessions");
        private static readonly String ClaudeSessions = Path.Combine(Home, "Library", "Application Support", "Claude", "claude-code-sessions");
        private static readonly String CodexDb = Path.Combine(Home, ".codex", "state_5.sqlite");
        private const Double MaxAgeSeconds = 48 * 3600;

        private readonly Action _changed;
        private readonly Object _lock = new();
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private Timer _tick;
        private List<Session> _sessions = new();
        private Dictionary<String, (String link, String title, Boolean archived, Double active, String cwd, Double focused)> _claude = new();
        private Dictionary<String, (String title, String cwd, Double updated)> _codex = new();   // threads you started, not helper agents
        private DateTime _titlesLoaded = DateTime.MinValue;
        private const Double StaleWorkingSeconds = 20 * 60;
        private readonly Dictionary<String, (DateTime stamp, String cli, (String, String, Boolean, Double, String, Double) entry)> _claudeFiles = new();

        public SessionStore(Action changed)
        {
            this._changed = changed;
            Directory.CreateDirectory(Dir);
            this._debounce = new Timer(_ => this.ReloadAndNotify());
            this.Reload();
            this._watcher = new FileSystemWatcher(Dir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
            this._watcher.Changed += (_, _) => this.Schedule();
            this._watcher.Created += (_, _) => this.Schedule();
            this._watcher.Deleted += (_, _) => this.Schedule();
            this._watcher.Renamed += (_, _) => this.Schedule();
            this._tick = new Timer(_ => this.ReloadAndNotify(), null, 30000, 30000);   // keeps ages current
        }

        private readonly Object _reloadLock = new();
        private Boolean _reloadAgain;

        // Timers and file events can ask for a reload at the same moment. One reload runs at a time,
        // and a request that arrives during it runs once more afterwards, so the newest state always wins.
        private void ReloadAndNotify()
        {
            try
            {
                if (!Monitor.TryEnter(this._reloadLock))
                {
                    this._reloadAgain = true;
                    return;
                }

                try
                {
                    do
                    {
                        this._reloadAgain = false;
                        this.Reload();
                    }
                    while (this._reloadAgain);
                }
                finally
                {
                    Monitor.Exit(this._reloadLock);
                }

                this._changed();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "session reload failed");
            }
        }

        private void Schedule()
        {
            try
            {
                this._debounce.Change(200, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // a file event that arrived after unload
            }
        }

        public List<Session> All()
        {
            lock (this._lock)
            {
                return this._sessions.ToList();
            }
        }

        public List<Session> Waiting() => this.All().Where(s => s.State == "waiting").OrderBy(s => s.WaitSince).ToList();

        public List<Session> ReadyList() => this.All().Where(s => s.Ready && s.State != "waiting").OrderBy(s => s.ReadyAt).ToList();

        // Ordered by state, then title, so a key keeps its session while sessions report activity.
        public List<Session> ForApp(String app) => this.All().Where(s => s.App == app).OrderBy(s => s.Rank).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id).ToList();

        public void Reload()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var list = new List<Session>();
            foreach (var file in SafeFiles(Dir, "*.json"))
            {
                try
                {
                    var j = JsonNode.Parse(File.ReadAllText(file));
                    var s = new Session
                    {
                        App = (String)j["app"],
                        Id = (String)j["id"],
                        State = (String)j["state"] ?? "idle",
                        Wait = (String)j["wait"],
                        Ready = (Boolean?)j["ready"] ?? false,
                        Updated = (Double?)j["updated"] ?? 0,
                        ReadyAt = (Double?)j["readyAt"] ?? 0,
                        WaitSince = (Double?)j["waitSince"] ?? 0,
                        Cwd = (String)j["cwd"],
                        FilePath = file,
                    };
                    if (s.App == null || s.Id == null)
                    {
                        continue;
                    }

                    if (s.State == "ended")
                    {
                        if (now - s.Updated > 60)
                        {
                            File.Delete(file);   // the hook marks an ended session instead of deleting it, so a late Stop cannot revive it
                        }

                        continue;
                    }

                    // Working with no report for 20 minutes usually means an event never arrived. Show it without a state.
                    if (s.State == "working" && now - s.Updated > StaleWorkingSeconds)
                    {
                        s.State = "unknown";
                    }

                    if (now - s.Updated > MaxAgeSeconds)
                    {
                        File.Delete(file);   // a session that ended without reporting
                        continue;
                    }

                    list.Add(s);
                }
                catch (Exception)
                {
                    // A half-written or foreign file is skipped; the next change event reloads it.
                }
            }

            var sinceTitles = DateTime.UtcNow - this._titlesLoaded;
            // While a Claude session is Ready, read the app's records every 10 s, so opening it there clears Ready soon.
            if ((list.Any(s => !this.Known(s)) && sinceTitles > TimeSpan.FromSeconds(5))
                || (list.Any(s => s.Ready && s.App == "claude") && sinceTitles > TimeSpan.FromSeconds(10))
                || sinceTitles > TimeSpan.FromSeconds(60))
            {
                this.LoadTitles();
            }

            var shown = new List<Session>();
            var claudeInFront = MacNative.FrontmostBundleId() == CaptureController.ClaudeBundle;
            var lastFocused = this._claude.Count == 0 ? null : this._claude.MaxBy(kv => kv.Value.focused).Key;
            var readInApp = new List<Session>();
            foreach (var s in list)
            {
                if (s.App == "claude" && this._claude.TryGetValue(s.Id, out var c))
                {
                    if (c.archived)
                    {
                        continue;
                    }

                    s.LinkId = c.link;
                    s.Title = c.title;

                    // Seeing the result in the Claude app counts as reading it, as opening it from the keypad does:
                    // either the session was switched to after it finished, or Claude is in front with this
                    // session as the one switched to most recently.
                    if (s.Ready && s.ReadyAt > 0)
                    {
                        var why = c.focused > s.ReadyAt ? "opened in Claude after it finished"
                            : claudeInFront && s.Id == lastFocused ? "on screen in Claude"
                            : null;
                        if (why != null)
                        {
                            s.Ready = false;
                            readInApp.Add(s);
                            PluginLog.Info($"session {s.Id[..Math.Min(8, s.Id.Length)]} {why}; no longer Ready");
                        }
                    }
                }
                else if (s.App == "codex")
                {
                    if (!this._codex.TryGetValue(s.Id, out var t))
                    {
                        continue;   // a Guardian review or helper agent thread
                    }

                    s.Title = t.title;
                }

                shown.Add(s);
            }

            // Save the read mark, so Ready does not come back once the session is no longer on screen.
            foreach (var s in readInApp)
            {
                this.MarkRead(s);
            }

            // Sessions that have not reported through the hooks yet still appear, from each app's own records.
            foreach (var (id, c) in this._claude)
            {
                if (!c.archived && now - c.active < MaxAgeSeconds && !shown.Any(x => x.App == "claude" && x.Id == id))
                {
                    shown.Add(new Session { App = "claude", Id = id, State = "unknown", Updated = c.active, Title = c.title, LinkId = c.link, Cwd = c.cwd });
                }
            }

            foreach (var (id, t) in this._codex)
            {
                if (now - t.updated < MaxAgeSeconds && !shown.Any(x => x.App == "codex" && x.Id == id))
                {
                    shown.Add(new Session { App = "codex", Id = id, State = "unknown", Updated = t.updated, Title = t.title, Cwd = t.cwd });
                }
            }

            foreach (var x in shown.Where(x => String.IsNullOrWhiteSpace(x.Title)))
            {
                x.Title = String.IsNullOrEmpty(x.Cwd) ? x.AppName : Path.GetFileName(x.Cwd.TrimEnd('/'));
            }

            lock (this._lock)
            {
                this._sessions = shown;
            }
        }

        private Boolean Known(Session s) => s.App == "claude" ? this._claude.ContainsKey(s.Id) : this._codex.ContainsKey(s.Id);

        private void LoadTitles()
        {
            var claude = new Dictionary<String, (String, String, Boolean, Double, String, Double)>();
            foreach (var file in SafeFiles(ClaudeSessions, "local_*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (this._claudeFiles.TryGetValue(file, out var cached) && cached.stamp == stamp)
                    {
                        if (cached.cli != null)
                        {
                            claude[cached.cli] = cached.entry;
                        }

                        continue;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("cliSessionId", out var cli) && cli.ValueKind == JsonValueKind.String)
                    {
                        var title = r.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                        var link = r.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
                        var archived = r.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True;
                        var active = r.TryGetProperty("lastActivityAt", out var la) && la.ValueKind == JsonValueKind.Number ? la.GetDouble() / 1000 : 0;
                        var cwd = r.TryGetProperty("cwd", out var cw) && cw.ValueKind == JsonValueKind.String ? cw.GetString() : null;
                        // When the session was last switched to in the Claude app.
                        var focused = r.TryGetProperty("lastFocusedAt", out var lf) && lf.ValueKind == JsonValueKind.Number ? lf.GetDouble() / 1000 : 0;
                        claude[cli.GetString()] = (link, title, archived, active, cwd, focused);
                        this._claudeFiles[file] = (stamp, cli.GetString(), (link, title, archived, active, cwd, focused));
                    }
                }
                catch (Exception)
                {
                }
            }

            // Codex threads you started yourself. Guardian reviews and helper agents are left out.
            Dictionary<String, (String, String, Double)> codex = null;
            var json = MacNative.Output("/usr/bin/sqlite3", "-readonly", "-json", "-cmd", ".timeout 2000", CodexDb,
                "select id, coalesce(nullif(name,''), nullif(title,''), '') as t, cwd, updated_at from threads " +
                "where thread_source = 'user' and archived = 0 and updated_at > strftime('%s','now') - 604800");
            if (!String.IsNullOrEmpty(json))
            {
                try
                {
                    codex = new Dictionary<String, (String, String, Double)>();
                    using var doc = JsonDocument.Parse(json);
                    foreach (var r in doc.RootElement.EnumerateArray())
                    {
                        codex[r.GetProperty("id").GetString()] = (r.GetProperty("t").GetString(), r.TryGetProperty("cwd", out var c) ? c.GetString() : null, r.GetProperty("updated_at").GetDouble());
                    }
                }
                catch (Exception ex)
                {
                    codex = null;
                    PluginLog.Warning(ex, "could not read Codex threads");
                }
            }

            this._claude = claude;
            // A failed read keeps the previous list; an empty one would hide every Codex session until the next read.
            if (codex != null || json == "")
            {
                this._codex = codex ?? new Dictionary<String, (String, String, Double)>();
            }
            this._titlesLoaded = DateTime.UtcNow;
        }

        private static IEnumerable<String> SafeFiles(String dir, String pattern, SearchOption option = SearchOption.TopDirectoryOnly)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, option) : Array.Empty<String>();
            }
            catch (Exception)
            {
                return Array.Empty<String>();
            }
        }

        // Opens the session in its app. Returns false when no exact link exists and only the app was brought forward.
        public Boolean Open(Session s)
        {
            String url = null;
            if (s.App == "claude" && !String.IsNullOrEmpty(s.LinkId))
            {
                url = $"claude://code/continue?session={Uri.EscapeDataString(s.LinkId)}&source=desktop_action";
            }
            else if (s.App == "codex")
            {
                url = $"codex://threads/{Uri.EscapeDataString(s.Id)}";
            }

            if (url != null)
            {
                Run("/usr/bin/open", url);
            }
            else
            {
                Run("/usr/bin/open", "-b", "com.anthropic.claudefordesktop");
            }

            return url != null;
        }

        // Opening a finished session from the keypad counts as reading its result.
        public void MarkRead(Session s)
        {
            if (s.FilePath == null)
            {
                return;
            }

            try
            {
                // Take the hook's lock (an advisory flock, which .NET uses for FileShare.None), so a hook
                // writing a new state at the same moment is not overwritten.
                using var hookLock = LockHooks();
                var j = JsonNode.Parse(File.ReadAllText(s.FilePath));
                if ((String)j["state"] == "ended")
                {
                    return;
                }

                j["ready"] = false;
                var tmp = s.FilePath + ".keypad-tmp";
                File.WriteAllText(tmp, j.ToJsonString());
                File.Move(tmp, s.FilePath, true);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "could not mark session read");
            }
        }

        private static FileStream LockHooks()
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    return new FileStream(Path.Combine(Dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (i < 50)
                {
                    Thread.Sleep(20);
                }
            }
        }

        private static void Run(String file, params String[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            Process.Start(psi)?.Dispose();
        }

        public static String Age(Double unix)
        {
            var s = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix;
            return s < 60 ? "now" : s < 3600 ? $"{(Int32)(s / 60)}m" : s < 86400 ? $"{(Int32)(s / 3600)}h" : $"{(Int32)(s / 86400)}d";
        }

        public void Dispose()
        {
            this._watcher?.Dispose();
            this._debounce?.Dispose();
            this._tick?.Dispose();
        }
    }
}

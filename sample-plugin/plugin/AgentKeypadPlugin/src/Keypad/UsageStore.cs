namespace Loupedeck.AgentKeypadPlugin
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;

    internal sealed class Limit
    {
        public Double? Percent;     // null when the source does not report this window
        public String Resets;       // "Thu", "4:10 pm"
        public Boolean Known;       // false when the source could not be read at all
        public String Why;          // short reason shown on the key when nothing could be read
    }

    // Reads usage limits. Codex records its limits in each session log; Claude's come from Anthropic's usage address.
    internal static class UsageStore
    {
        private static readonly String CodexSessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

        public static (Limit fiveHour, Limit week) Codex()
        {
            var five = new Limit();
            var week = new Limit();
            try
            {
                var latest = Directory.Exists(CodexSessions)
                    ? new DirectoryInfo(CodexSessions).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                        .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-8))
                        .OrderByDescending(f => f.LastWriteTimeUtc).Take(5).ToList()
                    : new();
                foreach (var file in latest)
                {
                    var line = ReadLines(file.FullName).LastOrDefault(l => l.Contains("\"rate_limits\""));
                    if (line == null)
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(line);
                    if (!TryFind(doc.RootElement, "rate_limits", out var rl))
                    {
                        continue;
                    }

                    five.Known = week.Known = true;
                    foreach (var key in new[] { "primary", "secondary" })
                    {
                        if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var minutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;
                        var target = minutes >= 1440 ? week : five;
                        target.Percent = w.TryGetProperty("used_percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
                        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number)
                        {
                            var at = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()).ToLocalTime();
                            if (at < DateTimeOffset.Now)
                            {
                                target.Percent = 0;   // the window has reset since Codex last reported
                                continue;
                            }

                            target.Resets = minutes >= 1440 ? at.ToString("ddd") : at.ToString("h:mm tt").ToLowerInvariant();
                        }
                    }

                    break;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "could not read Codex limits");
            }

            return (five, week);
        }

        // Claude's limits come from the address Claude Code's /usage reads, using the Claude Code login
        // stored in the macOS Keychain. The token is used only for this request and is never logged.
        // If that fails, the status line file below is used instead.
        // Anthropic refuses this address when it is asked too often (error 429). The last good numbers are
        // saved, with the time of the next allowed request, so reloads and page opens do not ask again early.
        private static readonly String LiveCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mx-keypad", "claude-usage-live.json");

        private sealed class LiveState
        {
            public Double? FivePct { get; set; }
            public Int64? FiveReset { get; set; }
            public Double? WeekPct { get; set; }
            public Int64? WeekReset { get; set; }
            public Int64 ReadAt { get; set; }
            public Int64 NextTryAt { get; set; }
            public Int32 BackoffMinutes { get; set; }
        }

        private static readonly Object ClaudeLock = new();

        // One read at a time, so a timer tick and a page open cannot both send a request.
        public static (Limit fiveHour, Limit week) Claude()
        {
            lock (ClaudeLock)
            {
                return ClaudeLocked();
            }
        }

        private static (Limit fiveHour, Limit week) ClaudeLocked()
        {
            var state = LoadLive();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            String why = null;
            if (now >= state.NextTryAt)
            {
                var (live, reason) = ClaudeLive();
                if (live != null)
                {
                    state = live;
                    state.ReadAt = now;
                    state.BackoffMinutes = 0;
                    state.NextTryAt = now + 10 * 60;
                }
                else
                {
                    why = reason;
                    // After a 429 wait 20 minutes, doubling up to an hour; after other failures wait 10 minutes.
                    state.BackoffMinutes = reason == "error 429" ? Math.Min(state.BackoffMinutes == 0 ? 20 : state.BackoffMinutes * 2, 60) : 10;
                    state.NextTryAt = now + state.BackoffMinutes * 60;
                    PluginLog.Info($"Claude usage: next request in {state.BackoffMinutes} minutes");
                }

                SaveLive(state);
            }

            if (state.ReadAt > 0)
            {
                return (FromLive(state.FivePct, state.FiveReset, false), FromLive(state.WeekPct, state.WeekReset, true));
            }

            // No good read yet: show why on the key.
            var shown = why == "error 429" || (why == null && state.BackoffMinutes > 0) ? "rate limited" : why ?? "waiting";
            var fallback = ClaudeFromStatusLine();
            fallback.fiveHour.Why ??= shown;
            fallback.week.Why ??= shown;
            return fallback;
        }

        private static Limit FromLive(Double? pct, Int64? reset, Boolean weekly)
        {
            var limit = new Limit { Known = true, Percent = pct };
            if (reset is Int64 r)
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(r);
                if (at < DateTimeOffset.UtcNow)
                {
                    limit.Percent = 0;   // the window has reset since the last read
                    return limit;
                }

                var local = at.ToLocalTime();
                limit.Resets = weekly ? local.ToString("ddd") : local.ToString("h:mm tt").ToLowerInvariant();
            }

            return limit;
        }

        private static LiveState LoadLive()
        {
            try
            {
                return File.Exists(LiveCache) ? JsonSerializer.Deserialize<LiveState>(File.ReadAllText(LiveCache)) ?? new LiveState() : new LiveState();
            }
            catch (Exception)
            {
                return new LiveState();
            }
        }

        private static void SaveLive(LiveState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LiveCache));
                File.WriteAllText(LiveCache + ".tmp", JsonSerializer.Serialize(state));
                File.Move(LiveCache + ".tmp", LiveCache, true);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "could not save Claude usage");
            }
        }

        private static (LiveState state, String why) ClaudeLive()
        {
            try
            {
                // Exit 44 means the Keychain has no such entry; other failures mean access was refused.
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/security") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "find-generic-password", "-s", "Claude Code-credentials", "-w" })
                {
                    psi.ArgumentList.Add(a);
                }

                String stored;
                Int32 code;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // An unanswered Keychain prompt must not hold the usage refresh forever.
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(15000))
                    {
                        p.Kill();
                        PluginLog.Info("Claude usage: Keychain read timed out");
                        return (null, "no access");
                    }

                    stored = output.GetAwaiter().GetResult().Trim();
                    code = p.ExitCode;
                }

                if (code != 0 || String.IsNullOrEmpty(stored))
                {
                    PluginLog.Info($"Claude usage: Keychain read exited {code}");
                    return (null, code == 44 ? "no login" : "no access");
                }

                String token;
                using (var login = JsonDocument.Parse(stored))
                {
                    var oauth = login.RootElement.GetProperty("claudeAiOauth");
                    token = oauth.GetProperty("accessToken").GetString();
                    if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number
                        && DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
                    {
                        PluginLog.Info("Claude usage: the stored login has expired; it renews the next time Claude Code runs");
                        return (null, "login expired");
                    }
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                using var response = Http.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    PluginLog.Info($"Claude usage: request returned {(Int32)response.StatusCode}");
                    return (null, $"error {(Int32)response.StatusCode}");
                }

                using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var state = new LiveState();
                (state.FivePct, state.FiveReset) = ReadWindow(doc.RootElement, "five_hour");
                (state.WeekPct, state.WeekReset) = ReadWindow(doc.RootElement, "seven_day");
                PluginLog.Info("Claude usage: read from Anthropic");
                return (state, null);
            }
            catch (HttpRequestException ex)
            {
                // The Mac could not reach Anthropic (no network, or the address did not resolve).
                PluginLog.Info($"Claude usage: could not reach Anthropic ({ex.InnerException?.Message ?? ex.Message})");
                return (null, "offline");
            }
            catch (Exception ex)
            {
                PluginLog.Info($"Claude usage: live read failed ({ex.GetType().Name})");
                return (null, "read failed");
            }
        }

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private static (Double? pct, Int64? reset) ReadWindow(JsonElement root, String key)
        {
            if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            Double? pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null;
            Int64? reset = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var at)
                ? at.ToUnixTimeSeconds()
                : null;
            return (pct, reset);
        }

        // Claude Code's status line saves its rate limits to ~/.mx-keypad/claude-usage.json.
        private static (Limit fiveHour, Limit week) ClaudeFromStatusLine()
        {
            var five = new Limit();
            var week = new Limit();
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mx-keypad", "claude-usage.json");
                if (!File.Exists(path))
                {
                    return (five, week);
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var rl = doc.RootElement.GetProperty("rate_limits");
                five.Known = week.Known = true;
                Fill(rl, "five_hour", five, false);
                Fill(rl, "seven_day", week, true);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "could not read Claude limits");
            }

            return (five, week);
        }

        private static void Fill(JsonElement rl, String key, Limit target, Boolean weekly)
        {
            if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()) : (DateTimeOffset?)null;
            if (resets != null && resets < DateTimeOffset.UtcNow)
            {
                target.Percent = 0;   // the window has reset since Claude last reported
                return;
            }

            target.Percent = w.TryGetProperty("used_percentage", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            if (resets is DateTimeOffset at)
            {
                var local = at.ToLocalTime();
                target.Resets = weekly ? local.ToString("ddd") : local.ToString("h:mm tt").ToLowerInvariant();
            }
        }

        private static String[] ReadLines(String path)
        {
            // Only the end of the log matters, and some logs are hundreds of megabytes.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const Int64 tail = 4 * 1024 * 1024;
            if (fs.Length > tail)
            {
                fs.Seek(-tail, SeekOrigin.End);
            }

            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n');
            return fs.Length > tail ? lines.Skip(1).ToArray() : lines;   // the first line may be cut
        }

        private static Boolean TryFind(JsonElement e, String name, out JsonElement found)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == name && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        found = p.Value;
                        return true;
                    }

                    if (TryFind(p.Value, name, out found))
                    {
                        return true;
                    }
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in e.EnumerateArray())
                {
                    if (TryFind(v, name, out found))
                    {
                        return true;
                    }
                }
            }

            found = default;
            return false;
        }
    }
}

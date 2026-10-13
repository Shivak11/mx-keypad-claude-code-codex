# Usage limits

## Codex

Codex writes `rate_limits` into its session logs under `~/.codex/sessions/**/rollout-*.jsonl`. Read the newest five logs from the last eight days and take the last line that contains `"rate_limits"`. Inside, `primary` and `secondary` each have `used_percent`, `window_minutes` and `resets_at` (Unix seconds). A window of 1440 minutes or more is the weekly limit. If `resets_at` has passed, show 0%.

Read only the end of each file. One log on a real machine was 563 MB; reading the last 4 MB is enough.

## Claude

Claude desktop does not run the Claude Code status line, so the status line's `rate_limits` never reach a file while the user works in the desktop app. The working source is the address Claude Code's `/usage` reads:

```text
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <access token>
anthropic-beta: oauth-2025-04-20
```

The token is the `claudeAiOauth.accessToken` inside the Keychain item `Claude Code-credentials` (read it with `/usr/bin/security find-generic-password -s "Claude Code-credentials" -w`). Exit code 44 means there is no such item. Check `expiresAt` (milliseconds) before using it. The reply has `five_hour` and `seven_day`, each with `utilization` (percent) and `resets_at` (ISO time).

This address is undocumented and limited:

- Ask at most once every 10 minutes and allow only one request at a time.
- On 429, wait 20 minutes, doubling to an hour. Save the last good numbers and the time of the next allowed request to a file, so reloads and page opens do not send early requests. Never save the token.
- Show the reason on the key until the first good read: rate limited, no login, no access, login expired, offline, or the status code.
- Time out the Keychain read after 15 seconds. An unanswered Keychain prompt would otherwise hold the refresh forever.
- A run of 429s that never ends usually means old plugin copies are still running and asking too (see `setup-and-build.md`, Lifecycle).

Treat the user's login with care: use it only for this request, never log it, and tell the user the plugin reads it.

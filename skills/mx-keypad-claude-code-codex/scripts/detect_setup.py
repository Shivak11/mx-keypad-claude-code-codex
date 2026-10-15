#!/usr/bin/env python3
"""Lists the coding agents, dictation apps, screenshot tools, browsers and terminals installed on this Mac.

The skill runs this first, so it can ask the user about the tools they have instead of assuming a set.
It reads only the names and bundle ids of installed apps, the names of command-line tools on the PATH, and
whether a tool's settings folder exists. It never opens a settings file, a session file or a credential.

Usage: detect_setup.py [--json]
"""

import json
import os
import plistlib
import platform
import shutil
import subprocess
import sys

HOME = os.path.expanduser("~")
APP_FOLDERS = ["/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities", os.path.join(HOME, "Applications")]
EXTRA_PATH = [os.path.join(HOME, p) for p in (".local/bin", ".npm-global/bin", ".cargo/bin", ".bun/bin", ".dotnet")] + [
    "/opt/homebrew/bin", "/usr/local/bin"]

# name: what to look for. "apps" match an installed app's file name (lower case, without .app) or bundle id.
# "cli" are command names, "config" are settings folders under the home folder.
CATALOGUE = {
    "agents": {
        "Claude Code and the Claude desktop app": {"apps": ["com.anthropic.claudefordesktop"], "cli": ["claude"], "config": [".claude"]},
        "Codex": {"apps": ["com.openai.codex"], "cli": ["codex"], "config": [".codex"]},
        "Cursor": {"apps": ["com.todesktop.230313mzl4w4u92"], "cli": ["cursor-agent", "cursor"], "config": [".cursor"]},
        "OpenCode": {"apps": ["opencode"], "cli": ["opencode"], "config": [".config/opencode", ".local/share/opencode"]},
        "pi": {"apps": [], "cli": ["pi"], "config": [".pi"]},
        "Hermes": {"apps": [], "cli": ["hermes"], "config": [".hermes"]},
        "Gemini CLI": {"apps": [], "cli": ["gemini"], "config": [".gemini"]},
        "Aider": {"apps": ["aider-desk", "aiderdesk"], "cli": ["aider"], "config": [".aider-desk"]},
        "Windsurf": {"apps": ["windsurf"], "cli": ["windsurf"], "config": [".windsurf", ".codeium"]},
        "GitHub Copilot CLI": {"apps": [], "cli": ["copilot"], "config": [".copilot"]},
        "Zed": {"apps": ["dev.zed.zed"], "cli": ["zed"], "config": [".config/zed"]},
        "Visual Studio Code": {"apps": ["com.microsoft.vscode"], "cli": ["code"], "config": [".vscode"]},
    },
    "dictation": {
        "Doing": {"apps": ["systems.doing.toucan", "doing"], "cli": [], "config": []},
        "Wispr Flow": {"apps": ["wispr flow", "wispr"], "cli": [], "config": []},
        "superwhisper": {"apps": ["superwhisper"], "cli": [], "config": []},
        "MacWhisper": {"apps": ["macwhisper"], "cli": [], "config": []},
        "Talon": {"apps": ["talon"], "cli": ["talon"], "config": [".talon"]},
        "Apple Dictation": {"apps": [], "cli": [], "config": []},
    },
    "screenshots": {
        "Xnapper": {"apps": ["xnapper"], "cli": [], "config": []},
        "CleanShot X": {"apps": ["cleanshot x", "cleanshot"], "cli": [], "config": []},
        "Shottr": {"apps": ["shottr"], "cli": [], "config": []},
        "macOS screenshot": {"apps": ["screenshot"], "cli": ["screencapture"], "config": []},
    },
    "browsers": {
        "Dia": {"apps": ["dia", "company.thebrowser.dia"], "cli": [], "config": []},
        "Arc": {"apps": ["arc", "company.thebrowser.browser"], "cli": [], "config": []},
        "Google Chrome": {"apps": ["com.google.chrome"], "cli": [], "config": []},
        "Safari": {"apps": ["com.apple.safari"], "cli": [], "config": []},
        "Firefox": {"apps": ["org.mozilla.firefox"], "cli": [], "config": []},
    },
    "terminals": {
        "Terminal": {"apps": ["com.apple.terminal"], "cli": [], "config": []},
        "iTerm2": {"apps": ["com.googlecode.iterm2"], "cli": [], "config": []},
        "Ghostty": {"apps": ["com.mitchellh.ghostty"], "cli": [], "config": []},
        "Warp": {"apps": ["warp"], "cli": [], "config": []},
        "WezTerm": {"apps": ["wezterm"], "cli": ["wezterm"], "config": [".config/wezterm"]},
        "kitty": {"apps": ["kitty"], "cli": ["kitty"], "config": [".config/kitty"]},
    },
}


def installed_apps():
    """Maps lower-case app name and bundle id to (display name, bundle id, path) for every .app found."""
    found = {}
    for folder in APP_FOLDERS:
        try:
            names = sorted(os.listdir(folder))
        except OSError:
            continue
        for entry in names:
            if not entry.endswith(".app"):
                continue
            path = os.path.join(folder, entry)
            bundle = ""
            try:
                with open(os.path.join(path, "Contents", "Info.plist"), "rb") as fh:
                    bundle = plistlib.load(fh).get("CFBundleIdentifier", "")
            except (OSError, plistlib.InvalidFileException, ValueError):
                pass
            name = entry[:-4]
            record = (name, bundle, path)
            found[name.lower()] = record
            if bundle:
                found[bundle.lower()] = record
    return found


def find_cli(command):
    path = os.pathsep.join(EXTRA_PATH + [os.environ.get("PATH", "")])
    return shutil.which(command, path=path)


def probe(spec, apps):
    evidence = []
    for key in spec["apps"]:
        record = apps.get(key.lower())
        if record:
            evidence.append({"app": record[0], "bundle_id": record[1], "path": record[2]})
            break
    for command in spec["cli"]:
        where = find_cli(command)
        if where:
            evidence.append({"command": command, "path": where})
    for folder in spec["config"]:
        if os.path.isdir(os.path.join(HOME, folder)):
            evidence.append({"settings_folder": "~/" + folder})
    return evidence


def run(command):
    try:
        return subprocess.run(command, capture_output=True, text=True, timeout=10).stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return ""


def logitech_and_build(apps):
    dotnet = find_cli("dotnet")
    return {
        "macos": platform.mac_ver()[0],
        "logi_options_plus": bool(apps.get("logioptionsplus")),
        "dotnet_sdks": run([dotnet, "--list-sdks"]).splitlines() if dotnet else [],
        "python3": find_cli("python3"),
    }


def main():
    apps = installed_apps()
    result = {category: {} for category in CATALOGUE}
    for category, tools in CATALOGUE.items():
        for name, spec in tools.items():
            evidence = probe(spec, apps)
            if evidence:
                result[category][name] = evidence
    result["apple_dictation_note"] = "Apple Dictation is part of macOS; ask whether it is switched on."
    result["build"] = logitech_and_build(apps)
    if "--json" in sys.argv:
        print(json.dumps(result, indent=2))
        return
    labels = {"agents": "Coding agents", "dictation": "Dictation", "screenshots": "Screenshots", "browsers": "Browsers", "terminals": "Terminals"}
    for category, title in labels.items():
        print(f"{title}:")
        if not result[category]:
            print("  none found")
        for name, evidence in result[category].items():
            kinds = sorted({"app" if "app" in e else "command" if "command" in e else "settings folder" for e in evidence})
            note = "  (settings folder only, so it may be a leftover)" if kinds == ["settings folder"] else ""
            print(f"  {name} ({', '.join(kinds)}){note}")
    build = result["build"]
    print("Build:")
    print(f"  macOS {build['macos']}; Logi Options+ {'installed' if build['logi_options_plus'] else 'not found'}; "
          f".NET SDKs: {', '.join(build['dotnet_sdks']) or 'none found'}")


if __name__ == "__main__":
    main()

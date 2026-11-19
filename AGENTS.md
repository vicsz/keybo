# Keybo: agent notes

C# Logi Actions plugin for the MX Creative Keypad (3×3 keys, 2 page buttons). Keybo, a pixel-art robot on his own key,
sums up the Mac and the Claude and Codex sessions. Around it: session keys with Dictate, Return, Cancel and Usage
(group Agents), live Mac stats (System), and Mic, Keep awake and Pomodoro (Tools). Keybo himself is in Pets. Read this before changing code.

## Rules
- Keep it minimal: no generated, optional, sample or research files.
- Commit only when asked.
- You can't see or press the keypad. Build, then ask the user to check on hardware.
- Never print or log tokens: `~/.claude/sessions/*.key`, or the Keychain token the Usage key reads.
- If `NOTES.local.md` exists (gitignored), read it before brainstorming or adding keys, and keep it current. Never copy its contents into tracked files.
- Pet art comes from Codex. Only the frames in `Pet/` belong in the repo; the rest of its art stays out.

## Build
- Needs the .NET 10 SDK and `LogiPluginTool`. Logitech's docs say .NET 8, but Plugin Service 6.4 runs .NET 10.
- Build from `KeyboPlugin/` only: `dotnet build`. It links Logi's Plugins folder to this repo's `bin/` and reloads the plugin. Expect 0 warnings.
- Never build from `src/` or from a copy of the repo: the build points `KeyboPlugin.link` at whatever folder it ran in, and building in `src/` creates `src/obj`, which breaks the next build (delete it to fix).
- Log: `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/Keybo.log`. A line starting `Readings:` is logged each time the monitor starts.
- Releases: Actions > Make release publishes the version in `LoupedeckPackage.yaml` as a `.lplug4`, then bumps that file for next time. Pull afterwards.
- GitHub builds skip the Logi link and reload step (`CI` is true there).

## Installing for a user
1. Check the basics: macOS, and Logi Options+ signed in to a Logi account (plugins can't be added without one) with the keypad connected. The Claude keys need the Claude desktop app; Usage needs the Claude CLI. If something is missing, tell the user.
2. `dotnet --version` must be 10 or later. If not: `brew install --cask dotnet-sdk`.
3. If `LogiPluginTool` is missing: `dotnet tool install --global LogiPluginTool`. Make sure `~/.dotnet/tools` is on `PATH`.
4. Build from `KeyboPlugin/`: `dotnet build`. The repo must stay where it is, since Logi runs the plugin from `bin/`.
5. Check the log for a clean load. If keys draw but do nothing in Claude, ask the user to allow Logi Plugin Service under Privacy & Security > Accessibility.
6. You can't place keys. Walk the user through "2. Place the keys" in the README.
7. Offer to add the session-name tip from the README to `~/.claude/CLAUDE.md`. Ask first: it's the user's file.
8. To uninstall: `dotnet clean` in `KeyboPlugin/` removes the link.

## Code map (`KeyboPlugin/src/`)
- `KeyboPlugin.cs`: owns what keys share: `SystemMonitor`, `KeepAwake`, `Pomodoro`, `ClaudeSessionStore` (started on load), `ClaudeUsage` (started by the first Usage draw) and the `MessageSent` event (Return sent). Keys never talk to each other directly; they go through these.
- `Actions/`: one file per key. `StatCommand.cs` is what the stat keys share; `ShortcutCommand.cs` what Dictate, Return and Cancel share. Action class names are what placed keys remember, so they keep their first names (`PetCommand`, `GoCommand`, `StopCommand`, `InternetCommand`, `DiskCommand`); display names and groups are free to change.
- `Groups.cs`: the four Options+ groups: Agents, System, Tools, Pets.
- `TapOrHold.cs`: tap versus hold, the same on every key with both (session keys, Cancel, Pomodoro, Keybo): taps act on release, a hold starts at LongPress, and keys that only get `RunCommand` treat it as a tap.
- `HoldMenu.cs`: hold to choose, on the held key itself: from LongPress it steps through choices a second each, release picks the one showing, and the last is always Never mind. Session keys (Pin here, Unpin) and Cancel (Archive) use it. No key ever draws for another, so it works on any page and layout.
- `Shell.cs`: every command the plugin starts (open, afplay, lsappinfo, ps, sqlite3, security, the Claude CLI), with a timeout. It never logs what a command prints.
- `KeyArt.cs`: draws the keys: shared colours, the stat keys' label, value, graph and bar, and Keybo with his sign (`KeyArt.Pet`). `AgentArt.cs` is the other half of the same class: session keys, Usage, the shortcut keys and hold choices.

Keybo (the Keybo key, group Pets; the class is still `PetCommand`):
- `PetMood.cs`: what he shows, worst first: Mac red > session waiting > Pomodoro done > session done > Mac amber > offline > focus (a Pomodoro running) > sleepy (10 min without input) > idle. Sessions come straight from `ClaudeSessionStore.All`. Memory counts only at critical pressure (4), because macOS sits at 2 for hours. A tap sets the sign aside: a session until it leaves that state, the Pomodoro until it's cleared, a Mac problem for 30 minutes. Sign layout: app mark + ! or ✓ + count.
- `Actions/PetCommand.cs`: plays the mood, handles taps and holds, and adds the moments in between. A tap on a session's sign opens it (`ClaudeSessionStore.Open`).
  - Gags: a tap with nothing to show plays one; on their own only after 15 calm minutes, 2–10 minutes without input, 10–20 minutes apart. Event gags play once, never over a red sign: Mic Drop (a session newly done, not on the first look after load), Pomodoro Rebellion (`Pomodoro.Finished`), Coffee Overflow (Keep awake on), Low Battery (waking from sleepy).
  - With no sign: `unplugged` and `online` around offline, `focus_start` and `focus_end` around a Pomodoro (no random gags while focused), and `paper_plane` when `MessageSent` fires, if it can start within 2 s.
  - Taps within 1.2 s of the last are a burst: the 1st does the job, then 2 `head_pat`, 3 `side_eye`, 5 `huff`, 8 `tantrum` then 30 s of `sulk` (taps ignored unless a sign is up; a sign ends it) and `sulk_end`.
  - Hold: `squish_1` from LongPress, `squish_2` at 2 s, `squish_3` at 4 s; release plays `spring_small` from squish_1, else `spring_big`.
  - An unchanged picture is sent again only every 2 s, which also keeps the monitor running under a held frame.
  - Loops in `animation-timing.json` drop a last frame that repeats the first, so the wrap doesn't stutter.
- `PetArt.cs` and `Pet/`: 116×116 PNG frames, badges and `animation-timing.json` (frame times, badge, sign text box), embedded in the plugin. `Pet/gags.json` and `Pet/gags/`: 37 one-off gags (12 frames each, starting and ending on idle), picked from larger Codex sets; the rest stay out on purpose (too fiddly at key size, or too tied to one game).

Claude and Codex:
- `ClaudeSessionStore.cs`: reads every session's status every 2 seconds, and opens one (`Open`, which also clears its green). All Claude file knowledge stays here.
- `CodexThreads.cs`: the same for Codex. All Codex file knowledge stays here.
- `ClaudeTranscripts.cs`: step count (2/4) for busy sessions, from their task list.
- `ClaudeUsage.cs`: usage and pace for the Usage key. All usage knowledge stays here.
- `ClaudeApp.cs`: everything that acts on the Claude window: opening a session, bringing Claude to the front, and which app is in front.
- `SessionKeyLocks.cs`: saves which session is on which key.

Mac and tools:
- `SystemMonitor.cs`: one sampler for the stat keys and Keybo, once a second (temperature every 2 s, busiest app and connection every 5 s, disk and battery every 10 s). Keys call `Use` when drawn; it stops 5 s after the last draw.
- `Mac.cs`: kernel, Foundation, Wi-Fi, AppKit and CoreGraphics calls (Mach host statistics, sysctl, disk space, CoreWLAN, app icons, seconds since last input).
- `Sensors.cs`: IOKit hardware readings: GPU, disk traffic, temperatures, battery.
- `Audio.cs`: CoreAudio: the default microphone (in use, muted, muting it) for Mic, and which apps are recording for Dictate.
- `KeepAwake.cs`: keeps the Mac and its screen on, with no lock: an IOKit `PreventUserIdleDisplaySleep` assertion (like `caffeinate -d`) plus `IOPMAssertionDeclareUserActivity` every 30 s (like `caffeinate -u`), so screen saver and lock timers never run out. macOS drops both if the plugin exits. `KeepAwakeCommand` steps the cup's steam every 240 ms, only while on.
- `Pomodoro.cs`: the 25-minute timer (ready, running, paused, done), ticking only while running. `PomodoroCommand` draws it; Keybo watches `IsDone`, `IsRunning` and `Finished`.
- `History.cs`: the last 60 samples for graphs.

## README art (`art/`)
- `keybo.png`: animated PNG (idle blink, then the DVD bounce gag), Pet frames at 3× with no smoothing.
- `moods.svg`: eight Pet frames with their signs drawn as `KeyArt.Pet` draws them, with captions.
- `keypad.svg`: one layout, hand-built SVG keys plus Keybo as an embedded frame.
- Made once with a throwaway script, from `Pet/` frames and the key drawing code. Redo them if the frames or key looks change.

## macOS sources
- CPU: `host_statistics` `HOST_CPU_LOAD_INFO`, tick deltas between samples.
- Memory used: `host_statistics64` `HOST_VM_INFO64`: (internal − purgeable + wired + compressor pages) × `hw.pagesize`, as Activity Monitor counts it. Total `hw.memsize`.
- Pressure: level from `kern.memorystatus_vm_pressure_level` (1, 2, 4); bar is 100 − `kern.memorystatus_level`.
- Disk: `NSURLVolumeAvailableCapacityForImportantUsageKey` on `/` (Finder's figure, counts purgeable space), via the Objective-C runtime.
- Network: .NET `NetworkInterface` byte counters for `en*` interfaces that are up.
- Busiest app: `ps -Axo pcpu=,comm=`, grouped by the first `.app` in each path. Icon: `NSWorkspace iconForFile:` on that `.app`, turned into a PNG and cached per app.
- Disk activity: `IOBlockStorageDriver` registry property `Statistics` → `Bytes (Read)` and `Bytes (Write)`, summed over drivers, deltas between samples.
- Mic: CoreAudio on the default input device (`dIn `): `gone` (running somewhere) for in use; `mute` on the input scope, or input `volm` when there's no mute switch.
- Recording apps (Dictate): CoreAudio `prs#` lists audio processes, `piri` says one is recording, `ppid` is its PID (macOS 14.2 or later). It never listens to the mic.
- GPU: `IOAccelerator` registry property `PerformanceStatistics` → `Device Utilization %`.
- CPU temp: private HID sensor API (`IOHIDEventSystemClient`, usage page 0xFF00, usage 5), average of sensors named `PMU tdie*`. Checked on an M4 Mac mini.
- Battery: `IOPSCopyPowerSourcesInfo`, the source whose `Type` is `InternalBattery`.
- Connection (`InternetCommand`, named before it stopped pinging): Wi-Fi name, `rssiValue`, `noiseMeasurement` and `transmitRate` from CoreWLAN (`CWWiFiClient`); the arcs come from signal minus noise. An `en*` interface with a gateway that isn't Wi-Fi counts as Ethernet, and its speed is .NET `NetworkInterface.Speed`.
- Idle time (Pet): `CGEventSourceSecondsSinceLastEventType` on the combined session state, any event.

## Claude (undocumented, can change with any update)
- `~/.claude/sessions/<pid>.json`: live sessions: `status` (busy, idle, waiting), `waitingFor`, `pid`.
- `~/Library/Application Support/Claude/claude-code-sessions/*/*/local_*.json`: `title`, `isStarred`, `isArchived`, `bridgeSessionIds`, `lastFocusedAt`. `btwLoweredFrom` marks a side chat.
- `~/Library/Logs/Claude/main.log`: `setFocusedSession` shows the open session instantly. The JSON files lag about a second.
- Links: `claude://claude.ai/code/<bridgeSessionId>` opens a session, `claude://code/new?source=desktop_action` starts one.
  Bridge IDs exist only with Remote Control on (it can be disabled by policy), so without one the plugin opens
  `claude://code/continue?session=<local_id>&source=desktop_action`. `ClaudeApp.SessionLink` picks the link.
- Usage: `GET https://api.anthropic.com/api/oauth/usage` with the CLI's token (Keychain item `Claude Code-credentials`, `claudeAiOauth.accessToken`)
  and header `anthropic-beta: oauth-2025-04-20`. Spend-limit accounts fill `spend` (`limit` and `used` in `amount_minor` with `exponent`,
  month resets 00:00 UTC on the 1st). Plans fill `limits[]` (`percent`, `severity`, `resets_at`, `is_active`). `claude -p /usage` refreshes an
  expired token without calling a model; `claude auth status` doesn't. Never print or log the token.
- Shortcuts: Dictate `Cmd+D`, Archive `Cmd+Opt+A`.

## Codex (undocumented)
- Threads: `~/.codex/state_5.sqlite`. Logs: `~/Library/Logs/com.openai.codex`.
- Open a thread: `codex://threads/<id>` (`CodexThreads.Link`).
- Dictation outside Claude: ChatGPT's single-tap shortcut, which the user sets to `Ctrl+Opt+Cmd+D`. Never hold a modifier key down from the plugin: a lost key-up left Control stuck system-wide (2026-11-15).
- ChatGPT's dictation records as the process `Codex (Aperitif)`. `Audio.IsRecording` asks CoreAudio whether it's recording, to choose the start or stop sound and to make Return stop dictation.

## Logi SDK gotchas
- Keys are 116×116. `IsWidget = true` removes the caption so the image fills the key.
- One-colour SVGs turn white, so drawings carry an invisible second colour.
- `BitmapBuilder.DrawText` takes a font name as its last argument; `Helvetica Neue Medium` gives the semibold look.
- `package/metadata/LoupedeckPackage.yaml` is YAML: a `: ` inside a value breaks it and the plugin won't load.
- Press, hold and release arrive through `ProcessButtonEvent2`; `RunCommand` is only a fallback. LongPress fires at about 0.5 s, so act on Release when a key also has a hold. No double tap (time it yourself) and no haptics on the keypad.
- A key doesn't know its position or page, so the six session keys are separate parameters. The keypad blanks when the Mac locks.
- Dynamic folders can't be opened from code, and a plugin can only draw its own keys, which are never told their position. So holds choose on the held key itself (`HoldMenu`) instead of taking over the keypad.
- Logi plugins can't call each other, which is why Keybo and the session keys live in one plugin.
- Renaming an action's class drops it from placed keys (its display name and group are safe to change), and moving to a new plugin resets saved data such as session-key pins.
- In the SDK but not yet tried on the keypad: app-specific plugins (`ClientApplication`), per-key setup fields, dynamic folders that the plugin fills, account login (`PluginPreferenceAccount`), on-screen overlay (`INativeOverlay`), lock and app-activation events (`INativeSystemEvents`, `INativeProcessEvents`), multi-frame images.

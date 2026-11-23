# Keybo: agent notes

C# Logi Actions plugin for the MX Creative Keypad (3×3 keys, 2 page buttons). Keybo, a pixel-art robot on his own key,
sums up the Mac and the Claude and Codex sessions. Around it: session keys with Dictate, Return, Cancel and Usage
(group Agents), live Mac stats (System), and Mic, Sound, Keep awake and Pomodoro (Tools). Keybo himself is in Pets. Read this before changing code.

## Rules
- Keep it minimal: no generated, optional, sample or research files.
- Commit only when asked.
- You can't see or press the keypad. Build, then ask the user to check on hardware.
- Never print or log tokens: `~/.claude/sessions/*.key`, `~/.codex/auth.json`, or the Keychain token the Usage key reads.
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
1. Check the basics: macOS, and Logi Options+ signed in to a Logi account (plugins can't be added without one) with the keypad connected. The Claude keys need the Claude desktop app; Usage needs the Claude CLI, and the ChatGPT app for Codex's limits. If something is missing, tell the user.
2. `dotnet --version` must be 10 or later. If not: `brew install --cask dotnet-sdk`.
3. If `LogiPluginTool` is missing: `dotnet tool install --global LogiPluginTool`. Make sure `~/.dotnet/tools` is on `PATH`.
4. Build from `KeyboPlugin/`: `dotnet build`. The repo must stay where it is, since Logi runs the plugin from `bin/`.
5. Check the log for a clean load. If keys draw but do nothing in Claude, ask the user to allow Logi Plugin Service under Privacy & Security > Accessibility.
6. You can't place keys. Walk the user through "2. Place the keys" in the README.
7. Offer to add the session-name tip from the README to `~/.claude/CLAUDE.md`. Ask first: it's the user's file.
8. To uninstall: `dotnet clean` in `KeyboPlugin/` removes the link.

## Code map (`KeyboPlugin/src/`)
- `KeyboPlugin.cs`: owns what keys share: `SystemMonitor`, `KeepAwake`, `Pomodoro`, `ClaudeSessionStore` and `Dictation` (started on load), `Usage` (started by the first Usage draw) and the `MessageSent` event (Return sent). Keys never talk to each other directly; they go through these.
- `Actions/`: one file per key. `StatCommand.cs` is what the stat keys share; `ShortcutCommand.cs` what Dictate, Return and Cancel share. Action class names are what placed keys remember, so they keep their first names (`PetCommand`, `GoCommand`, `StopCommand`, `InternetCommand`, `DiskCommand`); display names and groups are free to change.
- `Groups.cs`: the four Options+ groups: Agents, System, Tools, Pets.
- `TapOrHold.cs`: tap versus hold, the same on every key with both (session keys, Cancel, Pomodoro, Keybo): taps act on release, a hold starts at LongPress, and keys that only get `RunCommand` treat it as a tap.
- `HoldMenu.cs`: hold to choose, on the held key itself: from LongPress it steps through choices a second each, release picks the one showing, and the last is always Never mind. Session keys (Pin here, Unpin) and Cancel (Archive) use it. No key ever draws for another, so it works on any page and layout.
- `Shell.cs`: every command the plugin starts (open, afplay, lsappinfo, ps, sqlite3, security, the Claude and Codex CLIs), with a timeout. `Ask` talks to one that answers on stdin and stdout (Codex's app server). It never logs what a command prints.
- `KeyArt.cs`: draws the keys: shared colours, the stat keys' label, value, graph and bar, and Keybo with his sign (`KeyArt.Pet`). `AgentArt.cs` is the other half of the same class: session keys, Usage, the shortcut keys and hold choices. Session keys carry their app's real icon on the bubble's top-right corner (Claude, ChatGPT for Codex, Teams, Safari, any pinned app), found by bundle ID with `Mac.AppPath` and drawn with `Mac.AppIcon` at run time, so no logos are in the repo. A pinned app that isn't running gets its icon grey. Safari tabs and app windows draw a small window (`DrawWindow`) instead of the bubble.

Keybo (the Keybo key, group Pets; the class is still `PetCommand`):
- `PetMood.cs`: what he shows, worst first: Mac red > session waiting > Pomodoro done > session done > Mac amber > offline > live (an app recording from the mic, not muted) > focus (a Pomodoro running) > sleepy (10 min without input) > idle. Sessions come straight from `ClaudeSessionStore.All`. Memory counts only at critical pressure (4), because macOS sits at 2 for hours, and shows as `walls`, kept 10 s after it drops so they don't flap. A tap sets the sign aside: a session until it leaves that state, the Pomodoro until it's cleared, a Mac problem for 30 minutes. Sign layout: app mark + ! or ✓ + count.
- `Actions/PetCommand.cs`: plays the mood, handles taps and holds, and adds the moments in between. A tap on a session's sign opens it (`ClaudeSessionStore.Open`).
  - Gags: a tap with nothing to show plays one; on their own only after 15 calm minutes, 2–10 minutes without input, 10–20 minutes apart. Event gags play once, never over a red sign: Mic Drop (a session newly done, not on the first look after load), Pomodoro Rebellion (`Pomodoro.Finished`), Coffee Overflow (Keep awake on), Low Battery (waking from sleepy), and `jump_start` (power connected under 20% battery, after a reading without power; laptops only).
  - With no sign: `unplugged` and `online` around offline, `focus_start` and `focus_end` around a Pomodoro (no random gags while focused), `live_start` and `live_end` around `live` (the recording booth), and `paper_plane` when `MessageSent` fires, if it can start within 2 s (it waits for the booth to close).
  - The booth and the walls go by what's on the key: the booth comes back in after anything that covered it, and `walls_end` plays when memory recovers with no other sign up, or on a tap instead of `chomp`.
  - Taps within 1.2 s of the last are a burst: the 1st does the job, then 2 `head_pat`, 3 `side_eye`, 5 `huff`, 8 `tantrum` (1 in 4: `middle_finger`, censored) then 30 s of `sulk` (taps ignored unless a sign is up; a sign ends it) and `sulk_end`.
  - Hold: `squish_1` from LongPress, `squish_2` at 2 s, `squish_3` at 4 s; release plays `spring_small` from squish_1, else `spring_big`.
  - An unchanged picture is sent again only every 2 s, which also keeps the monitor running under a held frame.
  - Loops in `animation-timing.json` drop a last frame that repeats the first, so the wrap doesn't stutter.
- `PetArt.cs` and `Pet/`: 116×116 PNG frames, badges and `animation-timing.json` (frame times, badge, sign text box, and a `label` the box shows when there's no sign, like the booth's Live), embedded in the plugin. `Pet/gags.json` and `Pet/gags/`: 39 one-off gags (12 to 19 frames, starting and ending on idle), picked from larger Codex sets; the rest stay out on purpose (too fiddly at key size, or too tied to one game).

Claude and Codex:
- `ClaudeSessionStore.cs`: reads every session's status every 2 seconds, and opens one (`Open`, which also clears its green). All Claude file knowledge stays here.
- `CodexThreads.cs`: the same for Codex. All Codex file knowledge stays here.
- `TeamsChats.cs`: Teams chats on session keys (which chat is open, opening one), through Accessibility. All Teams knowledge stays here. Teams chats never join `ClaudeSessionStore.All`, so Keybo ignores them, and they're never gone.
- `SafariTabs.cs`: Safari tabs on session keys (the front tab, switching back to one), through Safari's AppleScript. All Safari knowledge stays here. Like Teams chats, tabs never join `ClaudeSessionStore.All` and are never gone.
- `AppWindows.cs`: any other app's window on session keys, or the app itself when the window doesn't show a file (the front window, raising it again, whether its app runs, its Dock badge and whether you've seen it), through Accessibility. All knowledge of other apps' windows stays here. Like Teams chats and Safari tabs, app windows never join `ClaudeSessionStore.All` and are never gone.
- `ClaudeTranscripts.cs`: step count (2/4) for busy sessions, from their task list.
- `Usage.cs`: the Usage key's shared part: each app's readings, pace and history (`UsageFeed`), and which limit shows. A tap steps through every limit with a reading, Claude's then Codex's; a week heading for red takes over, whichever app, until a tap moves past it. The key wears the app's icon. `ClaudeUsage.cs` and `CodexUsage.cs`: where each app's numbers come from. All usage knowledge stays in these three.
- `ClaudeApp.cs`: everything that acts on the Claude window: opening a session, bringing Claude to the front, and which app is in front (`Front`: bundle ID, process and name from one `lsappinfo` call).
- `SessionKeyLocks.cs`: saves which session is on which key.

Mac and tools:
- `SystemMonitor.cs`: one sampler for the stat keys and Keybo, once a second (temperature every 2 s, busiest app and connection every 5 s, disk and battery every 10 s). Keys call `Use` when drawn; it stops 5 s after the last draw.
- `Mac.cs`: kernel, Foundation, Wi-Fi, AppKit and CoreGraphics calls (Mach host statistics, sysctl, disk space, CoreWLAN, app icons, where apps are installed and running, process paths and the app responsible for a process, seconds since last input).
- `Accessibility.cs`: the Accessibility calls Teams chats and app pins share: a `Tree` per read that releases everything it copied, and Retain and Release for an element kept longer, like a pinned window.
- `Sensors.cs`: IOKit hardware readings: GPU, disk traffic, temperatures, battery.
- `Dictation.cs`: whether Claude or ChatGPT is recording, shared by Dictate and Return (started on load).
- `Audio.cs`: CoreAudio, one path for both sides: the mic an app records from and the output an app plays through, else the default (its name, in use, muted, volume, muting it, and the program using it), for Mic and Sound; and which apps are recording, for Dictate. Both keys show four states: free, in use, muted, and amber for muted while in use. Their label is the device's short name (`KeyArt.DeviceLabel` estimates widths, since the SDK can't measure text), the program's icon sits in the corner, and a tap on a device they can't mute shows No mute for 2 s. `SoundCommand` steps its waves every 200 ms, only while something plays and the key is drawn.
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
- Mic: CoreAudio's process list (`prs#`, macOS 14.2 or later): a process with `piri` is recording, and `pdv#` on the input scope lists the mics it uses. The key shows the default mic (`dIn `) if one is recording from it, else the lowest-numbered mic in use, else the default; without the list, `gone` (running somewhere) on the default stands for in use. Name: `lnam`, a CFString to release. Muted: `mute` on the input scope, or input `volm` when there's no settable mute switch (zero counts as muted).
- The program recording: each recorder's responsible process (`responsibility_get_pid_responsible_for_pid`, private, in libSystem; WebKit processes give Safari or Mail and helpers their app, but an extension or a daemon like `avconferenced` gives itself), its path from `proc_pidpath`, preferring one inside an `.app`. Its icon: the `.app`, else the `.appex` or `.xpc` it runs from, else the file. System Settings' Sound pane (`Sound.appex`) records while open, for its input meter.
- Sound: the same on the output side: `piro` and `pdv#` on the output scope, default `dOut`. With several programs playing, the one shown stays while it still plays (apps first). The volume is `volm` on element 0, or channels 1 and 2 averaged. An output with neither mute nor volume (many displays) shows Fixed.
- macOS saves each device's mute and volume in `/Library/Preferences/Audio/com.apple.audio.DeviceSettings.plist` and restores them when the device reconnects, so a mic muted once can come back muted. Keybo only mutes on a tap.
- Recording apps (Dictate): CoreAudio `prs#` lists audio processes, `piri` says one is recording, `ppid` is its PID (macOS 14.2 or later). It never listens to the mic. `Dictation.cs` checks every 0.6 s for a process named `Claude…` or `Codex…`; while one records, Dictate ripples its arcs and Return shows a dot, on one shared frame.
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

## Teams (undocumented, can change with any update)
- No local API: its third-party device API is switched off by policy here. Everything goes through Accessibility, and reads only the chat list's structure, never message text.
- Teams' web view builds its accessibility tree only after `AXManualAccessibility` is set on the `MSTeams` app (it returns an error but works).
- Main window title `Chat | <names>`; chat rows are `AXRow`s titled `Chat …`, `Group chat …` or `Meeting chat …` (unread ones start `Unread message …`), nested inside section rows. The list exists only while Teams shows its Chat view. Muted chats don't count in the Dock badge. A descendant's DOM ID `title-chat-list-item_<chat ID>` holds the chat ID (`19:…@unq.gbl.spaces` or `19:…@thread.v2`). The open chat's row has its own `AXDOMClassList`; `AXSelected` is always false. Rows have no press action.
- Open a chat: `msteams:/l/chat/<chat ID>/0` (brings Teams forward). A pinned chat's key ID is `teams:<chat ID>|<name>`.
- English UI only: the title and row prefixes are matched as text.

## Safari
- AppleScript (`osascript`): `URL` and `name` of `current tab of front window` when pinning, read only while Safari is in front so it never starts Safari. A tap looks through every window's tabs for the pinned address, then the same page (address up to `?` or `#`, since pages rewrite their query), then the same title; it makes that tab current and raises its window, and only if none matches opens the address in a new tab. Needs Automation permission for Logi Plugin Service (asked once).
- A pinned tab's key ID is `safari:<address>\n<title>`. Only http and https addresses. Never log either.
- Safari's own data (`~/Library/Safari`, including site icons) is locked by macOS, so keys show no site icons.

## Any app (Accessibility)
- With any other app in front, pinning reads its `AXFocusedWindow` (else `AXMainWindow`): `AXTitle` and `AXDocument`. A pinned window's key ID is `app:<bundle ID>\n<name>\n<document>\n<title>`. Titles can be private: never log them.
- Only a window whose `AXDocument` is a `file:` URL is pinned as itself: Pages, Preview, Terminal (its directory), VS Code (the file in its front editor, so only while a file is open). Any other window is pinned as its app, with an empty document: Messages has one window titled after the open conversation, and Mail, Notes, Music and browsers also change what one window shows. Brave's `AXDocument` is the page's address; Claude's and ChatGPT's are empty.
- A window pin's name is the first part of the title (split at ` — `, ` – `, ` - ` and ` | `) that isn't the app's name or a file name, so a VS Code window shows its folder and Terminal its directory. An app pin's name is its `.app`'s file name, worked out when the key is drawn, so pins made before this rule show it too. An app pin keeps the title in its ID only so two windows' keys stay apart.
- While the window is open the plugin keeps its element (retained); a closed one stops answering `AXRole`. Without it, a tap on a window pin matches the app's `AXWindows` by document, then exact title, then a title part equal to the key's name; with no match it runs `open -b <bundle ID>` and keeps looking for about 4 s. A tap on an app pin just runs `open -b`, like a click on its Dock icon. Accessibility usually lists only the current Space's windows, so one on another Space may only turn up once `open -b` has switched there.
- Raising: `AXMinimized` off, `AXRaise`, `AXMain`, and the app's `AXFrontmost`; if the app still isn't in front 150 ms later, `open -b`.
- Running: `NSRunningApplication runningApplicationsWithBundleIdentifier:`. Dock badges come from the Dock's own tree: its `AXList` children carry `AXStatusLabel` (the badge) and `AXURL` (the app). `lsappinfo` shows no `StatusLabel`, even for a test badge. A Dock read with no `AXList` counts as failed, so badges never seem to clear.
- A badge turns the key green only while it's news. Once the app has been in front (a tap, or any other way) its badge is seen, and the key keeps the count in grey until it goes up or other text shows: Calendar's badge counts unanswered invitations, so opening Calendar doesn't clear it. The seen badge follows the count down and resets when the badge clears, not when the app quits. In memory, like a session's green: the first reading after load or pinning counts as seen. The front app (`lsappinfo`) is asked only while a badge is news; a tap marks it seen at once and refreshes.
- Icons by bundle ID (`URLForApplicationWithBundleIdentifier:`), not a path: `/Applications/Safari.app` is a symlink into Safari's Cryptex, and an icon read through a symlink gets Finder's alias arrow.
- An empty key pins whatever is in front, so pinning a Claude session needs Claude in front.

## Codex (undocumented)
- Threads: `~/.codex/state_5.sqlite`. Logs: `~/Library/Logs/com.openai.codex`.
- Open a thread: `codex://threads/<id>` (`CodexThreads.Link`).
- Usage: `codex app-server`, from the CLI inside ChatGPT.app (`Contents/Resources/codex-cli/bin/codex`; the app's bundle ID is `com.openai.codex`), marked experimental. JSON lines on stdin: `initialize` (with `clientInfo`), `initialized`, `account/rateLimits/read`; it answers only while stdin stays open. The reply's `result.rateLimits` has `primary` (300 min) and `secondary` (10080 min): `usedPercent`, `windowDurationMins`, `resetsAt` (Unix seconds). Codex refreshes its own sign-in, so Keybo never reads `~/.codex/auth.json`. The thread files' `token_count` events used to carry the same limits but stopped on Nov 20, 2026.
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

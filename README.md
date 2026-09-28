# Chronos Timer

A portable, remotely controllable timer built on the [LinearTimecode](../LTC-dotnet) library (.NET 10). One engine, five switchable modes, SMPTE LTC out and in, and remote control from a browser, HTTP, WebSocket, OSC, the keyboard or plain text commands.

```
src/ChronosTimer.Core     engine, LTC output/input, settings catalog, text commands, HTTP+WebSocket and OSC servers, browser remote
src/ChronosTimer.Audio    dependency-free audio backends: WinMM (Windows), AudioQueue (macOS / Mac Catalyst / iOS), ALSA (Linux)
tools/ChronosTimer.Cli    chronos-timer: console utility (full-screen display, hotkeys, headless mode, remote client)
apps/ChronosTimer.App     .NET MAUI app (Windows, macOS, iOS, Android): big display, transport, every setting, control console
tests/ChronosTimer.Tests  xUnit: engine, time parsing, LTC round trips, commands, settings, OSC, HTTP, WebSocket
```

The library is referenced from the sibling `LTC-dotnet` repository (`flexorine/LTC-dotnet`). Point elsewhere with `-p:LtcRepo=<path>`.

## Modes (switchable any time, also while running)

| Mode | What it counts | LTC it sends |
|---|---|---|
| `timecode` | From a start address (default 01:00:00:00), optional end address | The address |
| `count-up` | Stopwatch from zero, optional limit | Elapsed (+ `ltc-offset`), or time left, or time of day (`ltc-source`) |
| `count-down` | A duration down to zero, amber/red at `warning`/`critical`, then `end-action` (continue into +overrun, stop, pause, loop) | Same choice as count-up; "remaining" runs down as reverse code |
| `time-of-day` | Computer clock, local or UTC, plus an offset | Clock time (BGF clock-time flag set), optional ST 309 date |
| `chase` | LTC read from the audio input; freewheels through dropouts; follows the incoming rate | The chased address (+ `chase-offset`), regenerated clean |

Transport: play, pause, stop, toggle, reset, restart, locate, nudge (frames or time), add time, jam, speed (0.01–100×, the LTC bit rate follows), reverse. Stopped/paused output can be silence or a held frame.

All 12 ST 12-1 rates (23.98 … 60, 29.97/59.94 DF and NDF), user bits as hex, four characters or the ST 309 date and time zone, and the color-frame flag.

Counting uses the audio card's sample clock while LTC output runs (`clock auto`), so generated code never drifts against the audio device. A phase lock picks each codeword's address at the moment it will be heard, so callback jitter never repeats or skips a frame.

## Control

Every command and setting has a human-readable name, description and list of allowed values, and the same names work everywhere:

| Surface | Example |
|---|---|
| Text command (app Control tab, console `:`, stdin, WebSocket) | `locate 01:00:00:00` · `duration 5m` · `set level -18` · `mode count-down` · `help` · `settings` |
| Browser remote | `http://<computer>:8480/?token=<http-token>` on any phone, tablet or PC on the network (after `http-bind all`; see Network access below) |
| HTTP | `GET /api/status` · `GET /api/settings` · `GET /api/commands` · `GET /api/play` · `GET /api/locate/10:00:00:00` · `GET /api/set?name=duration&value=10m` · `POST /api/command` (body = command line) |
| WebSocket | `ws://<computer>:8480/ws`: status JSON pushed at `status-rate`; send command lines as text |
| OSC (UDP 9000) | `/chronos/play` · `/chronos/locate "01:00:00:00"` · `/chronos/set/duration 300` · `/chronos/cmd "nudge +1s"` · `/chronos/status` → reply. Button messages (1 press / 0 release) trigger once. `osc-feedback host:port` streams `/chronos/display`, `/timecode`, `/state`, `/phase`, `/remaining`, … |
| Keyboard | Console: Space, S, R, ←/→ (frame), ↑/↓ (second), M, V, O, +/-, `:` command. App (desktop): menu accelerators, Ctrl+P play/pause, F2–F6 modes |
| Command line | `chronos-timer --mode count-down --duration 10m --output on --play` (every setting is an option) |

Times can be typed as `HH:MM:SS:FF` (`;` = drop-frame), `H:MM:SS`, `M:SS`, seconds (`90`), or units (`1h30m`, `45s`, `12f`); `+`/`-` makes them relative.

## Show: linked timers, cue light, scheduled start and hold

| Feature | How |
|---|---|
| **Linked timers** | `link-role master` on one timer, `link-role follower` on the others. Followers find the master on their own (`link-master auto`, UDP beacon on 8491) or by address (`link-master 169.254.10.20`). The link is TCP 8490 and uses the link-local **169.254.x.x (APIPA)** network by default (`link-bind apipa`; falls back to every network when the computer has no APIPA address). Each timer is a node named **ChronosTimer##**: the master is ChronosTimer01, followers get the next free number and ask for the same one on reconnect (`link-name` for a custom name). Optional shared `link-key`. |
| **Master time** | Followers' Show tab shows the master's running time, show line and cue light. |
| **Messages** | `message places please` (everyone) · `message @ChronosTimer02 standby` (one timer). Followers' messages go through the master to everyone. |
| **Cue light** | `cue standby`, `cue go` (also off, warning, end, stop) on the master; every follower shows it and can `cue ack`. With `cue-auto on` (default) the light turns to warning at the countdown's `warning` time and to end at zero (never over standby or stop). |
| **Scheduled show** | `show start 19:30` · `show end 21:30` (or the date/time pickers in the app). The timer arms as a countdown of the show's length and starts itself at the start time. Without an end it counts up from the start. |
| **Hold** | `show hold` / `show release` (the app's HOLD button). Held before the start, the timer waits until released (the artist is late) and the end moves by the delay, so the show keeps its full length. Held while running, the show pauses and the end moves by the length of the hold. |

## chronos-timer (console utility)

```
chronos-timer                                         full-screen timer with hotkeys
chronos-timer --mode timecode --start 10:00:00:00 --rate 29.97df --output on --play
chronos-timer --mode time-of-day --user-bits-mode date --output on --play
chronos-timer --mode chase --input on --output on --play
chronos-timer --headless < commands.txt               no display; commands on stdin; keeps serving remote control
chronos-timer send --token <t> 192.168.1.20 locate 01:00:00:00    remote-control another running timer
chronos-timer status studio-pc:8480
chronos-timer settings | commands | devices | help
```

**Portable:** publish gives one self-contained executable per platform. A `chronos-timer.json` next to the executable makes it keep its settings there (run it from a USB stick); otherwise settings go to the user settings folder. `save`/`load` commands, `--settings <file>`, `--save`, `--portable`.

## Chronos Timer app (MAUI)

* **Timer**: big auto-sizing display colored by phase, progress bar, play/pause, stop, reset, restart, ±frame/second, ±minute, jam, reverse, mode buttons, locate and duration entry, LTC out/in switches, signal and remote status.
* **Settings**: every setting from the catalog, grouped, with its description, default and allowed values; saved automatically.
* **Show**: the master's running time, six large cue buttons whose active one lights up as the cue light (display only, with acknowledge, on followers), messages, the show schedule with date/time pickers and the HOLD button, and the link role.
* **Control**: browser-remote/HTTP/OSC addresses, a command line, the full command reference and the log.

Android uses AudioTrack/AudioRecord; iOS and Mac use Audio Queues (system default device), Windows uses WinMM (any device). The app keeps the screen on while the timer page is open; on iOS it keeps running in the background (audio background mode).

## Build

```
./build.ps1                         # Debug + Release: core, audio, CLI, tests, and the app (Windows target on Windows, Mac Catalyst on macOS)
./build.ps1 -Configuration Release -SkipApp
build.cmd                           # same, from cmd.exe
./publish.ps1                       # portable single-file chronos-timer for this OS + the Windows app folder/zip → artifacts/
./publish.ps1 -Rids win-x64,win-arm64,osx-arm64,osx-x64,linux-x64,linux-arm64 -SkipApp

dotnet test tests/ChronosTimer.Tests
dotnet run --project tools/ChronosTimer.Cli -- --mode count-down --duration 30s --play
dotnet build apps/ChronosTimer.App -f net10.0-android     # needs the MAUI workload (dotnet workload install maui)
```

## Notes

* **Latency.** The code chosen for each codeword is the address at the moment it leaves the converter (buffered audio is accounted for). Use `output-offset` (ms) to make up for delay after the computer (e.g. a video pipeline). Smaller `buffer` = less latency, larger = safer.
* **29.97 NDF / 23.98 time of day.** Non-drop NTSC labels can't follow the clock at their real rate (they run 0.1 % slow); the output re-locks to the clock label, skipping a frame about every 33 s. Use 29.97 DF, 25 or 30 for time-of-day code.
* **Reverse / remaining.** Code whose address runs down is sent bit-reversed, exactly what a reader sees from reverse play.
* **Network access.** HTTP and OSC listen on this computer only by default. Set `http-bind` / `osc-bind` to `all` or to one network interface (e.g. `http-bind eth0`, `osc-bind "Ethernet 2"`; `get http-bind` lists this computer's interfaces) to control the timer from other devices. Every HTTP / WebSocket request needs the `http-token` (a random one is made on first run; `http-token new` makes another). The remote links shown by the timer include it, and the browser remote keeps it in a cookie after the first visit. Other tools send it as `X-Chronos-Token: <token>` or `Authorization: Bearer <token>`. Pages on other sites can't call the API unless listed in `http-origins`. OSC has no password, so bind it to the show network. A remote can `save` / `load` the settings file but not choose another file.
* **Firewall.** Allow `chronos-timer` / Chronos Timer for TCP 8480 and UDP 9000 when asked (or change `http-port` / `osc-port`).

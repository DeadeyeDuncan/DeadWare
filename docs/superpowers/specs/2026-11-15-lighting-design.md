# Lighting: DeadLight merged into DeadWare (0.19.0)

Status: approved by the user on 2026-11-15. Brainstormed with the user in the fan-control session. The user approved the scope, the desk view (plan A), the Lighting page mockup and the approach (a background device host), and chose to keep iCUE installed but disabled. An independent builder check was answered; the user approved this document on 2026-11-15. The version number is tentative, and releasing stays the user's call. Fan control, the request that started this session, follows as its own spec. It builds on the device host and the iCUE LINK hub driver defined here.

## Intent

DeadLight is the user's own RGB lighting app: Electron, React and TypeScript on a bundled OpenRGB 1.0rc3 engine, at `I:\DeadLight`. It runs alongside DeadWare. The user wants one app.

The trigger was cooling. The user wants DeadWare to replace FanControl and iCUE. iCUE has to go because its lighting fights DeadLight's on the Corsair iCUE LINK System Hub, which makes the LEDs blink. But the hub has **one** software/hardware mode that covers lighting and cooling together:

- OpenRGB switches the whole hub to software mode when it starts.
- It switches it back to hardware mode whenever it exits or rescans.
- It never sets fan or pump speeds.

Two programs owning the hub will always fight, so lighting and cooling need a single owner. The user chose to merge DeadLight into DeadWare first and make DeadWare that owner. Fan control comes next.

Success looks like this:

- One app and one tray icon for monitoring, lighting and, later, fans.
- DeadLight retired. iCUE stays installed but disabled: its Corsair Service no longer starts with Windows and its auto-start entry is off (the user's choice).
- Nothing blinks, and lighting survives a crash of DeadWare's window.
- Fan control can be added later without moving the hub driver.

## Decisions taken in brainstorming

- **Order:** merge DeadLight first, fan control second. The user chose this knowing the AIO stays on fixed speeds until fan control lands. That has been the state since iCUE's app last ran (2025-10-03); a read-only probe on 2026-11-15 found the pump at about 2,716 RPM and the fans at about 1,330 RPM with coolant at 38.4 °C.
- **Features kept:**
  - static colours per zone, with "set all colours"
  - synchronised desk effects: Wave, Breathing, Spectrum
  - the desk layout editor
  - keyboard modes and per-key painting
  - device tuning: per-device brightness and RGB calibration
  - ordered layers, with named-key masks, key fade and key ripple
  - lights off: manual toggle, global shortcut, off when idle, off when locked
  - Aerox 9 mouse: shared USB/wireless settings, battery and charging, sleep timer
  - per-device hardware modes (DeadLight's Mode menu), for devices that must stay on their own effects, such as the Lighting Node Pro (see Devices)
- **Features dropped:** screen ambient, audio-reactive lighting (and its visualisers), the OpenMW/Morrowind bridge, and **profiles**. Profiles means named profiles, the startup profile, per-application switching and profile import/export. The merged version has one saved lighting setup.
- **Nothing is imported from DeadLight.** Colours, effects and per-key paint are set up fresh. The desk layout is recreated from the user's photos (see Desk layout).
- **Desk view:** plan A, a top-down plan of the user's L-shaped desk. The tower can be tapped to show an inside view drawn from the side-window photo.
- **Lighting page:** approved as mocked, described under Proposed experience.
- **The PS5 controller** (Bluetooth, VID 054C PID 0CE6) is left out of the desk layout.
- **Approach:** a separate background process, the *device host*, owns the hardware; DeadWare's window is the interface. The user calls it the "background helper".

## Goals, non-goals and anti-goals

**Goals:** the kept features above, inside DeadWare; DeadWare as the only owner of the iCUE LINK hub; lighting that keeps running when the window crashes, hangs or restarts; a device host that fan control can extend.

**Non-goals:**
- the dropped features
- fan and pump control, including any speed command to the hub
- bundling OpenRGB in DeadWare's package
- importing DeadLight's saved data
- Corsair devices other than the iCUE LINK System Hub
- lighting on non-Windows systems

**Anti-goals.** Each of these is a failure even if everything else works:
- DeadWare makes any device blink or flicker during normal running. That includes the hub, through redundant mode switches or by fighting another program.
- Lighting writes stop the Aerox mouse from sleeping.
- Restart churn wedges USB device firmware. DeadLight hit this with a 1-second respawn; the floor is 4 s, and kill/relaunch is never cycled.
- Any log grows without bound. DeadLight's `reconcile-diag.log` reached 23 GB.
- Lighting costs noticeable CPU, or the window does per-frame work while hidden.
- Two programs drive the same device at once (DeadLight, iCUE or a second OpenRGB alongside DeadWare).
- The hub's cooling state changes without the user's say-so. Entering or leaving software mode is a cooling change; see Hardware verification.

## Constraints

- DeadWare is .NET 10 and WinUI 3, MIT-licensed, with a public repository. A `v*` tag publishes an update to every installed copy, so **never tag without asking**.
- This PC runs DeadWare elevated, polling every 0.5 s. OpenRGB needs administrator rights for SMBus/I²C devices: RAM, the GPU, the motherboard. DeadLight runs elevated today.
- **OpenRGB 1.0rc3** (GPL-2.0-or-later) stays a separate download that the user points DeadWare at. DeadWare talks to it only over its network SDK, never linking or bundling it.
- **Corsair protocol details may inform DeadWare, but no code may be copied.** OpenLinkHub (GPL-3.0), OpenRGB (GPL-2.0), liquidctl (GPL-3.0) and FanControl.CorsairLink (no licence) are off-limits. Add the courtesy attribution from `research-corsair.md` §4 to `THIRD-PARTY-NOTICES.md` with step 5's hub driver, the first code written from those facts; until then no Corsair-derived code exists, so the notices file doesn't carry it yet (whole-branch review D17). HidSharp 2.6.4 (Apache-2.0) already ships with LibreHardwareMonitor and can be used directly.
- **No elevated code from user-writable folders.** The device host runs elevated, so it inherits DeadWare's protected-folder rule (`InstallLocation`, v0.6.39). For an elevated host, OpenRGB's config folder, which OpenRGB also loads plugin DLLs from, lives in `%ProgramData%\DeadWare\openrgb`, created with the protected DACL the way `ProtectedRoot` creates the updates folder; a host without administrator rights uses `%LOCALAPPDATA%\DeadWare\openrgb`, since anything OpenRGB loads then runs with the user's own rights. The host starts OpenRGB with `APPDATA` set to that folder's parent, because OpenRGB 1.0rc3 is built before it reads `--config`. While it is built, it loads `<APPDATA>\OpenRGB\OpenRGB.json` and configures its log from that file's `LogManager` settings (`ResourceManager.cpp:136-148`), and it reads its zone sizes from the same folder (`:190-192`). Configuring the log takes an absolute log path, makes the log's folder, deletes old logs of that name and opens the log, all with OpenRGB's rights (`LogManager.cpp:65-160`), and `--config` later swaps the settings but never moves the log (`ResourceManager.cpp:710-717`). The parent's `OpenRGB` folder is the config folder itself, since Windows names ignore case, so all of that happens in the protected folder, and the host refuses to start OpenRGB for a config folder named anything but `openrgb` (F4, whole-branch review D14). Before that fix, an elevated OpenRGB read the user's `%APPDATA%\OpenRGB\OpenRGB.json` and logged into `%APPDATA%\OpenRGB\logs`, as the log of the 2026-11-18 hardware check shows. OpenRGB also starts with a cleared environment, because OpenRGB 1.0rc3 loads GPU vendor DLLs such as `atiadlxx.dll` and `nvapi64.dll` by bare name and Qt honours `QT_PLUGIN_PATH` and `QT_QPA_PLATFORM_PLUGIN_PATH`: an inherited user PATH, which includes the user-writable `%LOCALAPPDATA%\Microsoft\WindowsApps`, would let an elevated OpenRGB run a DLL any program of the user's could plant. The child gets only an allow-list of variables copied from the host (SystemRoot, windir, SystemDrive, ComSpec, PATHEXT, OS, PROCESSOR_ARCHITECTURE, NUMBER_OF_PROCESSORS, ProgramData, ProgramFiles, ProgramFiles(x86), ProgramW6432, CommonProgramFiles, CommonProgramW6432, ALLUSERSPROFILE, USERPROFILE, USERNAME, USERDOMAIN, COMPUTERNAME, TEMP, TMP, LOCALAPPDATA), APPDATA as above, and PATH set to exactly OpenRGB's own folder, System32, the Windows folder and `System32\Wbem`, never built from the inherited PATH. Every host does this, elevated or not (F5, whole-branch review R4-D15). OpenRGB's own folder is checked with the same ACL policy. If anyone but administrators can write to it, or the check can't judge it, an elevated host won't run that OpenRGB.exe and reports why (Ruling AM, final review 2026-11-18), and Settings → Lighting warns, as DeadWare already warns about its own install, and recommends `C:\Program Files\OpenRGB`. Nor does an elevated host run OpenRGB before it has locked OpenRGB's SDK port to itself: if it can't, it refuses and says why (see the SDK port lock under Processes; Ruling BG, whole-branch review D15). The only OpenRGB 1.0rc3 on this PC is `I:\DeadLight\resources\openrgb`, which ordinary users can modify. Moving it is a user step, asked for before the step 1 hardware check.
- **Updater limits:** entries under `DeadWare/`, at most 2048 of them, 256 MiB compressed, 768 MiB expanded (`Releases.cs:15`, `Packages.cs:92`). The updater also copies `DeadWare.Updater.exe` into its job folder as `helper.exe`, so no new file, class or log may be called "helper".
- **0.18.1 is in flight.** PR #132 and branch `claude/0181-polish` touch `MainWindow.xaml(.cs)`, `SettingsPage.cs`, `Collector.cs`, `Monitoring.cs`, `HistoryChartView.cs`, `OverviewPage.cs` and `WindowManager.cs`. Implementation starts from main once 0.18.1 has merged.
- **Claude's shells see shadow AppData.** They run inside the Claude app's MSIX container, so `%APPDATA%` and `%LOCALAPPDATA%` reads can return stale copies from `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\`. Never launch DeadLight or OpenRGB from a Claude shell. Verify AppData facts against something the live app is writing.
- **Hardware steps:** each live step is approved by the user one at a time. No on-screen automation while the user is gaming; check GPU load first.

## Approaches considered

1. **Device host process (chosen).** DeadLight's engine is ported to C# and runs in a small headless process that DeadWare starts and supervises. The window talks to it over a local pipe.
   - Lights survive a crash of the window, and the hub keeps one long-lived owner.
   - Fan control drops into the same process with the crash isolation it needs.
   - The cost is more work now: the pipe contract, supervision, update and quit integration, and more tests.
2. **Inside DeadWare's main process.** Cheaper and with no updater changes, but the lights freeze when the window crashes, and fan control would force the hub driver to move out later.
3. **Rejected: keep DeadLight's TypeScript engine as a sidecar.** DeadLight's Electron runtime, about 143 MiB compressed, would push the download past the updater's 256 MiB limit: the published 0.18.0 package is already 141,835,190 bytes (135.3 MiB), though the expanded size and the entry count would stay within their limits. A plain Node runtime, about 32 MiB, would fit, but it still means a second runtime, and it splits the hub across two codebases.
4. **Rejected: native drivers for every device instead of OpenRGB.** A large effort for no gain.

## Proposed experience

- **Sidebar:** a new **Lighting** item after Cooling Health.
- **Header:** a **Lights on** switch and a global **Brightness** slider. Under the title, a status line such as `8 devices through OpenRGB · the iCUE LINK hub is driven by DeadWare`, or the reason lighting is unavailable:
  - OpenRGB not found
  - DeadLight is running
  - iCUE is running
  - "Restart as administrator for RAM, motherboard and GPU lighting"
  - the device host has stopped, with the reason
- **Effects:** four tiles, **Static** (your saved colours), **Wave** (sweeps across the desk), **Breathing** (everything pulses together) and **Spectrum** (everything cycles through colours together). Under them sit the selected effect's settings: colours, speed, and direction and spread for Wave. Static has **Set all colours** with Undo for that picker session, as in DeadLight's Static card; this is the main way to colour things. A single device or zone gets its own colour in the device panel.
- **Desk card (plan A):**
  - The L-shaped desk drawn from above, with the main monitor, keyboard and mouse on the front part and the second monitor, headphones and amp on the left part. Grey items only help orientation.
  - The PC tower sits on the floor at the lower left. Tapping a lit device selects it; tapping the tower opens the **inside view**, a side-window drawing with:
    - radiator fans 1–3 across the top (iCUE LINK hub)
    - the TITAN pump ring
    - RAM ×2
    - the GPU logo
    - front fans ×3 in a column on the right (Lighting Node Pro), marked "own hardware effect" while the Node Pro runs a hardware mode
    - the rear fan, drawn unlit
  - **Edit layout** lets the user drag devices and zones and pick a desk shape preset (straight, L-left, L-right), as in DeadLight's editor.
  - A **Not placed** list holds devices without a position: the motherboard's four zones and the PS5 controller. They take the static colour but sit out the moving effects.
- **Device panel** (right side, for the selected device). Every device gets a **Colour** row: its static colour, with its zones listed for individual colours. Devices that offer hardware modes get a **Hardware mode** menu (DeadLight's Mode menu: *DeadWare effects* or one of the device's own modes). Then, by device:
  - **Keyboard (Apex Pro):** Lighting mode (*Follow the desk effect*, Static, Breathing, Color cycle, Rainbow wave), **Paint keys…** (the per-key editor on the real 112-LED map), Brightness, Calibration (R/G/B) and Layers (key ripple, key fade, with named-key masks).
  - **Mouse (Aerox 9):** brightness and calibration, battery level and charging, and the sleep timer, shared between USB and wireless.
  - **Other devices:** brightness and calibration.
- **Everyday card:** lights-off shortcut (default Ctrl+Alt+L, configurable), *Off when idle for N minutes* (off by default), *Off when the PC is locked* (off by default). These are DeadLight's defaults.
- **Tray menu:** a **Lights off** toggle.
- **Settings:** a new **Lighting** category:
  - **Enable lighting**, off by default for every install. It starts or stops the device host.
  - **OpenRGB location**, with Browse. It accepts only a file named `OpenRGB.exe`, and a fixed line below it says "DeadWare is tested with OpenRGB 1.0rc3."; Settings doesn't read the chosen file's version, which is a follow-up. It also applies the protected-folder check from Constraints and warns when the folder is user-writable, which an elevated host refuses to run OpenRGB from; a folder the check can't judge, a link or junction included, gets its reason instead, read the way the host reads it for its own refusal (polish round 2), followed by the same consequence and recommendation as the warning (polish round 3).
  - **DeadLight**, showing whether DeadLight or its startup task is present. A **Retire DeadLight** action disables DeadLight's startup task and stops it, only after the user confirms. The startup-task status and **Retire DeadLight** come in step 5; step 1 shows only a running DeadLight, in the list of running conflicts.
  - **iCUE**, read only. It shows whether any Corsair process is running, whether the Corsair Service starts automatically, and whether iCUE's auto-start entry (`Corsair iCUE5 Software` in HKLM Run) is present. The service's start type and the auto-start entry come in step 5; step 1 shows only the running conflicts. Each item that blocks the hub driver comes with plain instructions: Services for the service, Task Manager → Startup apps for the entry. DeadWare never changes another vendor's services or start-up entries; the user does that.
- **Hidden window:** the desk canvas uses Win2D like the existing charts. Live colour preview frames flow only while the Lighting page is visible, using the `Lifetime.WhileShowing` pattern, so nothing animates or repaints while the page or window is hidden.

## Architecture

### Processes

| Process | Role |
|---|---|
| `DeadWare.exe` (existing WinUI app) | The interface, monitoring as today, and the Lighting page. Starts, supervises and stops the device host, and talks to it over a named pipe. Never touches lighting hardware. |
| `DeadWare.Devices.exe` (**new**, headless, no window) | The **device host**. Owns the OpenRGB child process and SDK client, the painter and effects loop, the iCUE LINK hub driver, the Aerox wireless driver, the low-level keyboard hook, the global hotkey, idle and lock watchers, power notifications, and `lighting.json`. |
| `OpenRGB.exe` (child of the device host) | Drives every lit device except the iCUE LINK hub once phase 5 lands. Started with `--server --server-host 127.0.0.1 --server-port 6744 --noautoconnect --config <folder>`: `%ProgramData%\DeadWare\openrgb`, the protected folder from Constraints, for an elevated host, and `%LOCALAPPDATA%\DeadWare\openrgb` for one without administrator rights. It also gets `APPDATA` set to that folder's parent, so that what OpenRGB reads and writes before it reads `--config`, its first `OpenRGB.json` and its log, is in that same folder (see Constraints). Its environment is otherwise cleared to an allow-list of system variables, with a PATH of only its own folder and the system folders, since it loads GPU vendor DLLs by bare name (see Constraints). Before every start, with the Aerox detector, the host sets `LogManager.file_count_limit` to 2 in that folder's `OpenRGB.json`, keeping its other keys, so OpenRGB keeps its two newest logs in the folder's `logs`: the run in progress and the one before it (F4 item 6). 2 is the largest limit OpenRGB 1.0rc3 keeps from an empty folder: it removes only files matching its log name's template, but it counts them unsigned, so while fewer than the limit minus one are there it removes them all, and 10 would keep only the run in progress (`LogManager.cpp:487-491`). That caps the number of logs, not their size: one run's log has no size bound in 1.0rc3, against the anti-goal on logs, which is a follow-up (whole-branch review R4-D12). `--server-host 127.0.0.1` keeps its SDK server, which has no authentication, on this PC; OpenRGB 1.0rc3 otherwise listens on every network interface. An elevated host launches OpenRGB only once the SDK port lock below is in place, which keeps every other program on this PC out of that server. DeadLight uses port 6742; DeadWare's own config folder keeps the two apart. |

**The SDK port lock** (whole-branch review D15; owner's decision 2026-11-19, Ruling BG). Only DeadWare's own lighting helper, and only while it runs with administrator rights, may connect to the OpenRGB it runs as administrator.
- **Why.** OpenRGB 1.0rc3's SDK has no authentication, so `--server-host 127.0.0.1` keeps other computers out but not other programs. Its profile requests pass the client's string straight to `ProfileManager` (`NetworkServer.cpp:881-930`), which appends `.orp` and joins the name to the config folder without checking it (`ProfileManager.cpp:35-69`, `:436-446`). Without the lock, any local program, run by any user, could make an OpenRGB running as administrator create, truncate, read or delete `.orp` files at any absolute or `..\` path. `remove_null_terminating_chars` strips only trailing NULs, which suggests, from reading the source and not from a run, that an embedded NUL may cut the `.orp` off at the Windows API.
- **What.** Before it ever launches OpenRGB, an elevated host (`OpenRgbPortLock`) opens a Windows Filtering Platform session with `FWPM_SESSION_FLAG_DYNAMIC` and adds, in one transaction:
  - a sublayer of its own, at weight 0xFFFF;
  - at `FWPM_LAYER_ALE_AUTH_CONNECT_V4`, a block filter for TCP connections to remote address 127.0.0.1 and remote port 6744, with `FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT`, so that no filter in a lower-priority sublayer can turn it into a permit;
  - a permit filter of higher weight with the same conditions plus two more. `FWPM_CONDITION_ALE_APP_ID` must equal the host's own image, from `FwpmGetAppIdFromFileName0` on its own path. `FWPM_CONDITION_ALE_USER_ID` must match the self-relative descriptor `D:(A;;CC;;;BA)`, which grants only BUILTIN\Administrators `FWP_ACTRL_MATCH_FILTER` (CC). A token without administrator rights holds Administrators as deny-only, so it fails that match, and so does a copy of `DeadWare.Devices.exe` that such a program starts and controls; that copy is why the app ID alone isn't enough. A process run as administrator is out of scope: it already has those rights;
  - the same pair at `FWPM_LAYER_ALE_AUTH_CONNECT_V6` for remote address `::ffff:127.0.0.1`, so a dual-stack socket's IPv4-mapped connect is refused whichever layer Windows classifies it at. The filters match by remote address, not by `FWP_CONDITION_FLAG_IS_LOOPBACK`.
- **Lifetime.** Windows deletes everything a dynamic session added when the session's engine handle closes, which includes the host exiting or crashing, so nothing persistent is added to Windows. The host holds the lock for its whole life and disposes it at exit. Before every launch of OpenRGB, restarts included, the engine has the lock confirm its filters still exist (`FwpmFilterGetByKey0`) and add any that are missing, closing the session and trying once more with a fresh one if that fails, and then run the self-check below. Whenever the engine kills OpenRGB, on a stop, a give-up or a failed launch, it waits up to 5 seconds for that process to exit before the stop returns, so the next start's self-check doesn't meet the old OpenRGB still listening; one still running after that is logged and left, and the self-check then refuses (F6 item 9, Ruling BH).
- **Refusal.** If the filters can't be added or confirmed, the engine doesn't launch OpenRGB and says "DeadWare couldn't limit OpenRGB's network port to its lighting helper (error N: <message>), so it won't run OpenRGB as administrator. Turn lighting off and on in Settings to try again.", and the log gets a line. Like the folder refusal, it is final for that engine, neither retried nor counted in the backoff. A 32-bit host refuses the same way rather than guess WFP's structure layouts; DeadWare ships win-x64 only.
- **Self-check** (Ruling BH; whole-branch review D15 and D7/D14). Once the filters are in place, and before OpenRGB starts, the host connects to 127.0.0.1 on the port itself, the way its port probe and OpenRGB.NET do: through a parameterless `TcpClient`, which is a dual-mode IPv6 socket, so the connect goes out as `::ffff:127.0.0.1`. It allows 5 seconds, since a refused loopback connect can take about a second on Windows. The outcome decides:
  - refused (WSAECONNREFUSED, 10061): nothing listens and the permit lets the host through, so the launch goes ahead;
  - access denied (WSAEACCES, 10013): the lock would block the host itself. That happens if the app ID Windows classifies for the host's image differs from what `FwpmGetAppIdFromFileName0` returned, which a review suspected for install paths with capitals outside A-Z or 8.3 short names but couldn't confirm without a run. Without the check, that would show only as OpenRGB stopping five times; with it, the engine refuses with "DeadWare's limit on OpenRGB's network port would also block its own lighting helper (Windows refused its test connection), so it won't run OpenRGB as administrator. Turn lighting off and on in Settings to try again.";
  - a connect that succeeds: another program holds the port, and the engine refuses with "Another program is using OpenRGB's network port 6744, so DeadWare won't run OpenRGB as administrator. Quit that program, then turn lighting off and on in Settings.";
  - anything else, no answer within the 5 seconds included: the first of those refusals.

  The log gets the outcome and its code once per check. Both refusals are final, like the lock's own: neither retried nor counted in the backoff. The check also shows, on every elevated launch, that the V6 permit lets the host's dual-mode connects through.
- **Who gets one.** Only an elevated host (`Environment.IsPrivilegedProcess`). `Program` takes the engine's port guard, and the lock it holds, from `OpenRgbPortLock.GuardFor`, which the tests pin. A host without administrator rights can't add filters, and its OpenRGB has only the user's rights. Only the host connects to OpenRGB: `OpenRgbSession` and the port probe.
- **Limits.**
  - An OpenRGB left behind without the job, in the two rare cases Supervision lists, runs on unlocked once its host is gone.
  - Another program can stand in for DeadWare's OpenRGB on 127.0.0.1:6744. OpenRGB 1.0rc3 binds its port with SO_REUSEADDR (`NetworkServer.cpp:298`, in `StartServer`), and Windows lets a second program bind the same address and port with SO_REUSEADDR, so that program can take the port at any time, not only before DeadWare's OpenRGB starts, and Windows then doesn't guarantee which of them gets the helper's connections. The lock filters connects, not binds, so it doesn't stop that. The helper may then talk to that program, which can feed it wrong device data but gains no rights. The self-check catches only a program that holds the port before a launch.
  - A host without administrator rights doesn't lock the port, so other users of the PC can drive its OpenRGB with that user's rights.
  - A filter that hard-permits in a sublayer of higher priority, from other security software, could override the block.
  - Whether WSL2's mirrored networking passes these filters is unverified.
  - The lock's integration tests need administrator rights, so they run on CI's elevated runner; a run without them reports them skipped, except on CI (`GITHUB_ACTIONS=true`), where a test fails instead, so a runner without administrator rights shows red (whole-branch review D13).

The device host is a separate executable, not a mode of `DeadWare.exe`. That gives a distinct name in Task Manager, simple process detection, and no WinUI start-up in a headless process. It is published single-file and self-contained, as the updater is, and ships in the same folder as `DeadWare.exe`. It carries its own copy of the .NET runtime inside that file rather than sharing `DeadWare.exe`'s runtime files. The package check must still pass.

### Lifecycle and supervision

- **Single instance:** one device host per user and session, guarded by `Local\DeadWare.Devices.<sid>.<session>`, following `GuiInstance`. A starting window connects to a running host instead of starting a second one.
- **Start:** if **Enable lighting** is on, the window starts the host during app start-up. On a `--startup` launch at sign-in, the host waits about 10 s before OpenRGB detection so USB devices can settle; DeadLight's task used a 15 s delay.
- **Supervision:** the window restarts the host after an unexpected exit, waiting 4 s, then 8, 16 and 32, capped at 60 s. After five unexpected exits within 10 minutes it stops trying and shows the reason. Those 10 minutes are counted on the monotonic clock, so a change of the system clock neither stretches nor shortens them (whole-branch review D4). The host restarts OpenRGB under the same rule; never faster than 4 s. A stop for a conflict, or for a conflict scan that failed, counts as an exit of the OpenRGB it ends: the start once the conflict clears waits out the same backoff, counted from the stop, and the fifth such stop or exit within 10 minutes gives up, which a conflict that clears doesn't undo; only lighting turned off and on, or Quit, clears that history (F4, whole-branch review D8/D9). The 4 s floor also holds across a deliberate stop and start of the host within one window process (lighting toggled, a new OpenRGB path, or the window replacing a host that lacks its elevation), because the pacer lives in that process. A new DeadWare process, such as the one **Restart as administrator** or **Update & restart** starts, has no exit on record, so its first launch isn't paced (polish B23). OpenRGB runs in the host's kill-on-close Job Object, so it ends with the host however the host ends, unless Windows refused to add it to the job, which the host logs, or the host died in the moment between starting OpenRGB and adding it.
- **Crash of the window:** the host keeps running and the lights stay on. The next window start reconnects.
- **Quit DeadWare:** the window asks the host to stop. The host stops painting, returns the hub to hardware mode (once it owns the hub), ends OpenRGB, releases the mouse and exits. OpenRGB is ended by killing its process tree, as DeadLight does: the owner's decision of 2026-11-18 (Ruling AK, "Keep as today"). Handing the iCUE LINK hub back to hardware mode on Quit waits for DeadWare's own hub driver (step 5) or fan control. The window waits up to 5 s, then ends a hung host. If the host is hung, the hub's own watchdog, about 10 s of silence (unconfirmed on firmware 4.1.656), returns the hub to hardware mode.
- **Update and restart:** before handing over to the updater, the window stops the host exactly as on Quit. The updater waits up to 10 s for every `DeadWare.Devices.exe` that runs from the install folder, or whose folder it can't read, to exit before it replaces files, and stops without replacing any if one is still running. It doesn't check for OpenRGB: an OpenRGB a host left behind (see Supervision) keeps running, and the new host then reports it as another OpenRGB. The restarted window starts the new host.
- **Sleep and resume:** on suspend the host stops all writes. On resume it waits about 10 s, asks OpenRGB to rescan, clears every device's diff cache, re-initialises the hub (software mode only if iCUE isn't running) and repaints.
- **Elevation:** the host inherits the window's token. Without administrator rights, OpenRGB's SMBus devices are missing, and the page says so. If the window is restarted elevated while a non-elevated host runs, the window restarts the host.
- **Conflicts.** The host refuses to start lighting, and says why, if any of these is running:
  - DeadLight's `electron.exe` from a DeadLight folder
  - an `OpenRGB.exe` it didn't start
  - iCUE: `iCUE.exe` or `iCUE Launcher.exe`
  - a Corsair service executable: `C:\Program Files\Corsair\Corsair Device Control Service\bin\CorsairDeviceControlService.exe`, `CorsairDeviceListerService.exe`, or the legacy `…\Corsair iCUE5 Software\clink\Corsair.Service.exe`. Whether the legacy one touches the hub is unconfirmed, so it is treated as a conflict for the hub driver.

  Step 1 matches iCUE, iCUE Launcher and the two Corsair device services by process name, as it does the legacy service it only reports, and an `OpenRGB.exe` it didn't start by name and process id: that can over-block but never misses one, and matching them by path is a follow-up.

  Launchers that run briefly at sign-in are rechecked before the hub driver takes the hub.

  The hub driver also refuses while iCUE runs. Match processes by path, never by name alone.

### Pipe contract

- Named pipe `\\.\pipe\DeadWare.Devices.<sid>`, restricted to the current user and Administrators. Messages are length-prefixed JSON, using source-generated `System.Text.Json`, of at most 4 MiB each. A state message carries the whole settings with the device list. The settings take at most 1 MiB, since `lighting.json`'s cap is on their written form, which escapes non-ASCII text just as the pipe's compact form does, up to six bytes a character, and only adds indentation; the rest is room for a large device list (F4, whole-branch review D12). Each request has an id and gets a response; the host also sends events.
- Every message carries a protocol version. The window and host come from the same build. On a mismatch the window shows "The lighting helper is a different version. Restart DeadWare." and stops supervising that host, which keeps running; a malformed frame, one over the size cap included, takes the same path (`DeviceHostClient`, whole-branch review D6). Stopping or replacing a host of another version is a follow-up; only version 1 exists so far.
- **Requests:**
  - state: `GetState`, `Rescan`, `Stop`
  - global lighting: `SetLightsOn`, `SetBrightness`, `SetEffect(kind, settings)`
  - colours: `SetZoneColors`, `SetAllColors`, `PaintKeys(device, keys)`
  - devices and layout: `SetDeviceSettings(device, patch)`, `SetLayout(layout)`
  - everyday controls: `SetEveryday(settings)`
- **Events:**
  - `StateChanged`: devices, zones, statuses and warnings
  - `BatteryChanged`
  - `HubReadings`: coolant temperature and fan/pump RPM, every 2–3 s once the hub driver lands
  - `PreviewFrame`: current zone colours at 10 fps or less, sent only while a window has subscribed
- Effects run inside the host, so no per-frame traffic crosses the pipe except the optional preview.

### Persistence

- `%LOCALAPPDATA%\DeadWare\lighting.json` is written **only by the host**. Writes go to a temporary file that is then renamed into place, and the data is validated on every read and write; a file over 1 MiB counts as damaged and isn't read. Settings whose written form, the indented JSON in UTF-8, would be over 1 MiB are invalid as well (F4, whole-branch review D1): a change that would make the file larger is refused with an error reply and the live settings stay as they were, and a file holding such settings, as compact JSON under 1 MiB say, counts as damaged at load. An invalid file is quarantined, not overwritten, as DeadLight's stores do. One that can't be read is left alone: the host runs on defaults and saves nothing, and only a host that starts later reads it again (polish round 2). The file holds:
  - the zone static colours
  - the effect and its settings
  - per-device settings: brightness, calibration, keyboard mode, per-key paint, layers
  - the desk layout: zone positions, the tower group, the desk shape, Not placed and hidden devices
  - everyday settings
  - the Aerox settings
- DeadWare's `settings.json`, written by the window, gains only `LightingEnabled` and `OpenRgbPath`, which the window needs before any host exists.
- The host keeps a rolling, size-capped log: `devices.log`, 2 MiB × 3 files, recording changes only. Never a line per frame or per poll. A host without administrator rights logs to `%LOCALAPPDATA%\DeadWare\logs`; an elevated host logs to `%ProgramData%\DeadWare\logs\<user SID>`, created with the protected DACL like the openrgb folder, because its fixed-name appends and rotations in a user-writable folder could be redirected through a junction (Ruling AT, final review 2026-11-18). In that per-user folder only its user, SYSTEM and Administrators can read the log, since it names the user's paths and devices (other users can list the folder and read its permissions, which the updater's audit of `%ProgramData%\DeadWare` needs), and two users' elevated hosts never share one file (polish B1/B20). Every elevated start gives that folder its private rights again once it passes the check, so one made again with the logs folder's rights, which let every user read, is private again; the log itself never makes the folder, so if it goes missing while the host runs, the host logs nothing more until its next start (polish round 2).
- An elevated host also refuses a `lighting.json` path through a link or junction, and one that leads somewhere it can't confirm: it checks the path before use (`SafeFiles.NoLinks`), then checks that the file it opened has the final path it asked for, and from then on works on that open file, never on the name (whole-branch review D13/D14). A final path Windows can't give is refused like one that leads elsewhere, because the host then can't confirm which file it opened: `GetFinalPathNameByHandleW` gives none in DOS form on a drive the Mount Manager didn't create (F2). The note on the Lighting page says that the path goes through a link or leads somewhere DeadWare can't confirm. What follows depends on which check catches it:
  - **A link or junction on the path when the host starts:** defaults, no saves and the note. `SafeFiles.NoLinks` sees it even when `lighting.json` doesn't exist yet.
  - **A redirection only the final-path check catches,** such as a drive letter or network path that leads elsewhere, or a junction put in place after the check; a final path Windows can't give counts too. It is found only when a file is opened. If `lighting.json` is there when the host starts, the result is the same as for a link. If there's none, the host starts normally, and each change fails with "couldn't save", since each save is refused in turn.
  - **Either kind appearing while the host runs:** each change after it fails with "couldn't save". At the next start, a link gives defaults with the note, and so does any of the others if `lighting.json` exists then. A save creates its temporary file by name before the final-path check, so if the host ends at that moment, or the delete-on-close request fails, an empty `lighting.json.<guid>.tmp` can be left where a swapped-in junction led; nothing is written into it. Before that, a save also creates `lighting.json`'s folder by name when it is missing, so an empty `DeadWare` folder can be left where a swapped-in junction, or a redirection only the final-path check catches, led, and it stays after the save is refused (whole-branch review D7).
  - **Outside what these checks cover:** the host takes `%LOCALAPPDATA%` from the user's known-folder setting in HKCU, which a program without administrator rights can change with no link involved. The path then leads to another folder, yet both checks pass, because the path asked for comes from that same setting. Not trusting that setting in an elevated host is a follow-up (whole-branch review D11).

### Devices

- **OpenRGB devices** go through the SDK. The plan is **OpenRGB.NET 3.1.1** (MIT, NuGet), pinned. The first task must confirm protocol negotiation and controller-data parsing against the user's OpenRGB 1.0rc3. If it falls short, write a minimal client for the SDK subset DeadLight uses (controller count and data, update LEDs, set custom or Direct mode, rescan, device-list-updated).
- **DeadLight lessons to keep:**
  - pushes are rate-capped and diff-cached per device, and every cache is cleared after any OpenRGB restart or rescan
  - Direct or custom mode is set once per device
  - device keys combine serial and location, and are remapped when a USB serial drops or comes back (`remapDeviceKeys`/`reconcileDeviceKeys`)
  - an OpenRGB hardware rescan runs when the Aerox switches between USB and wireless
- **Hardware modes and the Lighting Node Pro.** DeadLight never puts the Node Pro, with its three LL fans, into Direct mode. On this PC, OpenRGB 1.0rc3's Direct writes to it render partially or strobe (tested live 2026-08-22). DeadLight sets it to its own **Rainbow Wave** by name (`HARDWARE_MODE_DEFAULTS`), and DeadWare keeps that default:
  - A device in a hardware mode sits out DeadWare's effects, brightness and calibration. Its panel and the inside view say so.
  - For lights off, a device whose modes include Static is switched to hardware Static black; otherwise it stays on and the page says so.
  - Never write "Direct" into a hardware-defaulted device's saved settings. That would defeat the fallback by mode name, as DeadLight's comment warns.
  - The same Hardware mode menu serves the motherboard.
  - The step 1 hardware check may include one Direct-mode re-test on the Node Pro, only with the user's approval. If it renders cleanly, the default can change.
- **Refresh and pacing:** port DeadLight's `device-service.ts` and `main.ts` timings unchanged:
  - An unchanged frame is re-sent once its last push is older than **400 ms** (`REFRESH_TTL_MS`). The Node Pro drops back to its hardware effect after about 2 s without it.
  - Pushes are capped per device at **33 ms** for USB/HID devices and **200 ms** for DRAM, which is OpenRGB controller type 1.
  - The paint tick is **500 ms** with no effect running and **33 ms** while one runs.
  - A rescan runs every **5 s**, because OpenRGB 1.0rc3 doesn't reliably announce device-list changes.

  Lights-off on a device in Direct mode therefore means refreshing black, not sending nothing. The idle-gated Aerox is the exception.
- **Apex Pro keyboard** goes through OpenRGB: Direct only, 112 LEDs, the real matrix map from the zone's matrix.
- **Aerox 9:** wired (`1038:1858`) through OpenRGB. Wireless (`1038:185A`) uses DeadWare's own 2.4 GHz dongle driver on HidSharp, ported from `aerox-wireless.ts`:
  - wireless opcodes are the wired opcode OR `0x40`, each with a readback
  - battery is polled only while the mouse is in use, and replies are matched to battery requests by opcode
  - there is a sleep-timer command
  - writes are gated when idle, so effect frames and keepalives never reset the mouse's idle timer
- **iCUE LINK System Hub** (VID `1B1C`, PID `0C3F`, interface MI_00; never MI_01) gets DeadWare's own HidSharp driver in **phase 5**, written from `research-corsair.md` §3 and the 2026-11-15 read. The firmware, the channels, the type codes and the pump's model are what the hub reported in that read. The LED counts aren't in it: they come from the device-type table in `2026-11-15-corsair-icue-link-notes.md`, which agrees with OpenRGB's view of the hub recorded in `research-corsair.md` §0.
  - firmware 4.1.656 (the read)
  - channels 1–3: RX RGB fans (type `0x0F`, the read; 8 LEDs each, the notes)
  - channel 13: TITAN 360 pump (type `0x11`, model `0x02`, the read; 20 LEDs, the notes)
  - find devices by type code, never by position or name

  It works like this:
  - **Every transaction** holds `Global\CorsairLinkReadWriteGuardMutex`, created if missing with a permissive ACL. Stale input reports are drained first, and replies are matched by opcode echo and data type.
  - **Taking the hub:** check that iCUE isn't running, enter software mode (`01 03 00 02`), and read devices, temperatures and speeds.
  - **Lighting:** colour-endpoint writes, chunked at 508 bytes. Hub frames are throttled to about 8 fps, because each transaction is about four 513-byte round trips. Colours are re-sent at least every 5 s while idle, inside the hub's roughly 10 s watchdog.
  - **Readings:** coolant temperature and fan/pump RPM every 2–3 s, published as `HubReadings`.
  - **Releasing:** hardware mode (`01 03 00 01`) on stop.
  - **The merge never sends a speed command** (endpoint `0x18`). An allow-list in the driver forbids it until fan control.
  - Until phase 5, the hub's lights stay on OpenRGB with its iCUE Link detector enabled, exactly as under DeadLight today. Phase 5 turns that detector off in DeadWare's OpenRGB config and hands the hub to the driver.
- **Hub readings in DeadWare's monitoring (phase 5):** the window adds the host's `HubReadings` as sensors (Sensors page: coolant temperature, pump RPM, three fan RPMs). Following the 0.18.1 session's plan for the user's request ("I would like to see my Corsair AIO water cooler's reservoir temp on a graph"), it also adds an optional **Coolant temperature** line on the CPU temperature graph. The line shares the °C scale and has a Settings toggle beside the existing pump-speed option. The pump RPM already reaches DeadWare through the board's PUMP_FAN header, which carries the TITAN pump's tach.

### Painter and effects (ported from DeadLight)

- These files at commit `0497cd3` on `codex/static-color-control` are the behavioural reference, and their vitest cases should be ported to xUnit:
  - `shared/effects.ts`, `shared/layout.ts`, `shared/device-lighting.ts`
  - `electron/painter.ts`, `electron/device-service.ts`, `electron/openrgb-host.ts`, `electron/lighting-power.ts`, `electron/aerox-wireless.ts`
- **Desk effects:**
  - **Wave** is the only effect computed from zone positions in the unit square, using DeadLight's maths: speed 0–100 maps to 0.05–2 cycles per second; direction is 0–359°, where 0 is left to right; spread is inverted, so higher spread means a shorter wavelength; an empty palette means the full hue circle.
  - **Breathing** pulses every zone in step.
  - **Spectrum** cycles every zone through the palette in step.

  DeadLight times Breathing and Spectrum by desk position. The approved mockup asks for them in step, so those DeadLight tests are rewritten, not ported.
- **Loop:** 30 frames per second in the host. While lights are off it computes no effects and sends only the refresh black frames the Refresh rule requires.
- **Colour order:** DeadLight's `compose` order, so brightness caps everything: static colour or effect, then key paint, then layers in order, then per-device RGB calibration, then global × device brightness. Key ripple and key fade use the low-level keyboard hook on a dedicated message-loop thread in the host.
- **Not placed zones** get the static colour only.
- **Lights-off rules** port `lighting-power.ts`: a manual toggle, a global hotkey through `RegisterHotKey`, idle via `GetLastInputInfo`, and lock via session notifications.

### Desk layout

- The model is DeadLight's: points in the unit square, a tower group, and desk rectangles used only as a visual guide.
- **Default for this PC:** the L-left desk, with the long part in front and the short part on the left.
  - The keyboard sits in the middle of the front part, with the mouse to its right.
  - The tower is on the floor at the lower left, holding: hub fans ×3 and pump, RAM ×2, GPU, Lighting Node Pro channels and the motherboard.
  - Inside the tower, zones sit where the inside view draws them: radiator fans along the top from rear to front, the pump at the centre, RAM to its right, the GPU logo below, and front fans down the front edge.
  - Devices are matched by identity. Anything unmatched goes to Not placed.
  - The user's current DeadLight `layout.json` (real file, 2026-11-08) is a reasonable cross-check for which zones exist.

## Reference material

- **In the repository**, next to this spec:
  - `2026-11-15-corsair-icue-link-notes.md`: the iCUE LINK protocol facts the hub driver is written from, distilled from the 0.18.1 session's research and the 2026-11-15 read, with the courtesy attribution. It holds no local paths and no device serial numbers.
  - `2026-11-15-lighting-mockups/`: the approved mockups, desk plan A with the inside view, and the Lighting page.
- **Private copies, not committed** because they contain this PC's device IDs and local paths: the full research report `research-corsair.md` and the probe output `hubread-output.txt`. They are in the shared session notes folder (`side-session-notes\local_b6185fbd-…`).
- **Hub test fixtures** are built from the decoded values: firmware 4.1.656; channels 1–3 type `0x0F`, channel 13 type `0x11` model `0x02`; coolant 38.4 °C; fans 1,337, 1,326 and 1,337 RPM; pump 2,716 RPM. Device IDs are synthetic. The 2026-11-15 probe logged only the first 24 bytes of each frame. If step 5 needs full frames, take one fresh full-frame capture, with the user's approval, and keep it out of the repository.

## Build order (proposed; each step is a PR with its own hardware check)

1. **Host and basics:**
   - device host, supervision and the pipe contract
   - OpenRGB host and SDK client, static colours, lights on/off
   - the Lighting page with header, status and device list
   - Settings → Lighting
   - conflict detection, and Quit and update integration
2. **Desk:** layout (plan A, Edit layout, inside view, Not placed), the Wave, Breathing and Spectrum effects, and preview frames.
3. **Device controls:** tuning (brightness, calibration), keyboard modes, per-key painting, and layers with key reactions.
4. **Mouse and everyday:** the Aerox (wireless driver, battery, sleep timer, shared settings), Everyday (shortcut, idle, lock), the tray item, and sleep/resume handling.
5. **The hub:** DeadWare's iCUE LINK driver (lighting plus read-only readings) with the Corsair courtesy attribution from `research-corsair.md` §4 added to `THIRD-PARTY-NOTICES.md` (see Constraints), OpenRGB's hub detector off, hub sensors and the coolant line, and the DeadLight retirement steps. Before its hardware check, the user disables iCUE as Settings → Lighting describes: Corsair Service stopped and set to Manual, auto-start entry off. iCUE stays installed.

Each step merges through the normal branch and PR flow in `docs/development.md` and `AGENTS.md`. Cutting a release is always the user's call.

## Hardware verification

- Every live step is described to the user and approved before it runs: the cutover rehearsal (DeadLight stopped, DeadWare lighting started, DeadLight restored afterwards if wanted), the hub take-over, the hub watchdog check, the Aerox sleep check, and sleep/resume.
- **The hub take-over is a cooling change.** Before it, read the hub's speeds and coolant temperature. After entering software mode, read them again. If pump or fan speeds drop below the pre-take-over reading, return the hub to hardware mode and stop.
- **The hub watchdog check** answers one question: does the hub fall back to hardware mode within about 10 s when the host stops talking? Only with the user's approval, and only once.
- At most two kill/relaunch tests per session; never cycle restarts.
- No on-screen automation while the user is gaming; check GPU load first.

## Testing

- **Core unit tests:**
  - effects maths, colour resolution, layout geometry and validation
  - keyboard modes, layers, lights-off rules, device-key remapping
  - `lighting.json` validation and quarantine
  - pipe contract round trips
  - supervisor back-off: never a restart under 4 s, and the give-up rule
- **Hub protocol tests:** framing and parsing against fixtures built as described in Reference material. Also colour-packet chunking, and the opcode allow-list, including that no speed write can be built.
- **DeadLight's tests:** port the relevant vitest cases, including `tests/fixtures/controllers.json` for SDK parsing.
- **Host integration:** against a fake OpenRGB SDK server and a fake HID transport.
- **Window:** checks in the existing `DeadWare.App.Tests` patterns for the page, settings, tray item and the hidden-window rule.

## Out of scope and later

- **Fan control**, the next spec: board headers through LibreHardwareMonitor, hub fan and pump speeds, curves, and the safety rules the user set. It reuses the device host and the hub driver. Its brainstorm already found:
  - LibreHardwareMonitor 0.9.6's NCT6687D hand-back bug
  - the crash-coverage problem
  - FanControl's and iCUE's current configurations

  The notes are in the shared notes file `fan-control-recon.md`.
- Profiles, screen ambient, audio-reactive lighting, the Morrowind bridge, bundling OpenRGB, and Corsair devices beyond the LINK hub.

## Left to the builder

- Class and project layout, the exact pipe message shapes, and test structure.
- Exact back-off numbers within the bounds above, and pipe security details.
- OpenRGB.NET versus a minimal own client, after the protocol check.
- Win2D drawing details, and how the inside view's components map to OpenRGB zones and LEDs.

## Calls made in writing this up (for the user to check)

- The device host is a separate `DeadWare.Devices.exe`, not a mode of `DeadWare.exe`.
- Lighting is off by default on every install, and turned on in Settings.
- DeadWare's OpenRGB uses port 6744 and its own config folder.
- The hub's lights stay on OpenRGB until step 5, then move to DeadWare's driver.
- The coolant-temperature line and hub sensors are part of step 5, not fan control.
- The lights-off defaults follow DeadLight: Ctrl+Alt+L, with the idle and lock rules off. The mockup's "idle 10 min: on" was only an illustration.
- The version is 0.19.0, tentatively.
- The Lighting Node Pro stays on its own Rainbow Wave, as in DeadLight, so the front fans don't follow DeadWare's effects. One Direct-mode re-test is offered at the step 1 hardware check. DeadLight's Hardware mode menu is kept.
- Colours are per device and per zone. **Set all colours** is the main action, and individual colours sit in the device panel.
- Breathing and Spectrum run in step everywhere, as in the mockup, not timed by desk position as in DeadLight.
- DeadLight's colour order is kept, so brightness applies last and caps everything.
- OpenRGB's config is in `%ProgramData%\DeadWare\openrgb` when the host runs as administrator and in `%LOCALAPPDATA%\DeadWare\openrgb` when it doesn't, and an OpenRGB folder that ordinary users can modify gets a warning; an elevated host won't run OpenRGB from it (Ruling AM, final review 2026-11-18). Moving OpenRGB to `C:\Program Files\OpenRGB` is asked for before step 1's hardware check.
- Reference material goes into the repository without personal data; the raw research and probe output stay in the private notes folder.
- iCUE stays installed but disabled (the user's decision). DeadWare only detects and explains, and the user switches off the service and the auto-start entry.

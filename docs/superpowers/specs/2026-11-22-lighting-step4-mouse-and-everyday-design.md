# Lighting step 4: mouse and everyday

Date: 2026-11-22. Base: main `2141e00` (0.21.0, lighting steps 1 to 3b). Target version: 0.22.0.
Parent design: `2026-11-15-lighting-design.md` (called "the merge design" below).
Approved mockup: `2026-11-22-lighting-step4-mockups/mouse-and-everyday.html` (approved by the owner 2026-11-22; it is the reference for every visible label and state in this document).

## Why

DeadWare is taking over DeadLight's job so DeadLight can be retired in step 5. After step 3b, four things still only work in DeadLight:

1. the Aerox 9 mouse on its wireless receiver (lighting, battery, sleep time);
2. turning the lights off without opening a window (shortcut, idle, lock);
3. a lights line in the tray menu;
4. behaving across PC sleep and wake.

Step 4 moves these four into DeadWare. Success is that the owner can leave DeadLight closed and miss nothing in daily use, and that the mouse still falls asleep to save its battery.

## Goals

- The Aerox 9 is lit by DeadWare on its wireless receiver, with the same colours and settings as on the cable, shown as one card.
- The mouse card shows battery level and charging, and lets the owner choose the mouse's sleep time.
- A global shortcut (default Ctrl+Alt+L), the tray menu and the page switch all flip the one saved "Lights on" choice.
- Optional rules turn lights off while the PC sits unused or is locked, and bring them back on return. They never change the saved choice.
- Lights stop being written when the PC sleeps and come back correctly after it wakes.

## Non-goals

- The Corsair hub's part of sleep and wake, and retiring DeadLight (step 5).
- Any change to the desk picture, the plain desk, effects, key painting, layers or saved looks.
- Bluetooth, more than one Aerox, any other mouse model.
- A low-battery warning, battery in the tray, fading lights off and on.
- The six logged minors from 3b and the open points from 3a.

## Anti-goals (a failure even if everything else works)

- **DeadWare keeps the mouse awake.** With a sleep time set, the mouse must fall asleep on time while an effect is running.
- **The helper reads what is typed or watches the mouse through a hook.** No new hook of any kind; no key-reading call. See "Input rule".
- **Idle or lock changes the saved choice**, so that a crash or restart during an idle spell leaves the lights off.
- **A sleeping or absent mouse stalls the other devices** (this is why OpenRGB's own wireless Aerox detector stays switched off).
- **A device fault or wake is answered by quickly restarting OpenRGB** (wiki `rapid-process-restart-wedges-usb-hid-firmware`: 4 s floor).

## Decisions the owner made

- The shortcut and the tray line flip the same saved "Lights on" switch as the page (2026-11-22). Lights turned off by shortcut stay off across a restart.
- Idle and lock are temporary: the switch stays on, a yellow line on the page says why the lights are dark.
- Everything in the approved mockup, including its ten listed choices:
  1. The Everyday card sits under the desk picture, full width, on the Lighting page.
  2. The shortcut turns lights off and on; it is changed by clicking Change and pressing the new keys; "No shortcut" removes it; a red line reports keys another program owns.
  3. Idle time is a list: 1, 2, 5, 10, 15, 30, 60 minutes; 10 when first switched on. Idle and lock start switched off.
  4. While idle or lock has the lights off, the Lights on switch stays on and the page says why.
  5. Lights cut off and on at once, no fade.
  6. One mouse card for cable and wireless, one set of colours and settings.
  7. Battery: a bar, a percentage and the charging state. No low-battery warning.
  8. The mouse sleep time applies as soon as it is picked; it starts on "Leave as the mouse is".
  9. Turning lights off darkens the mouse straight away, even with a sleep time set.
  10. The tray line reads "Turn lights off" / "Turn lights on".

Everything under "How it is built" is Claude's call, made because the owner handed over the how. None of it was put to the owner as a choice.

## Approaches considered

**A. Everything in the helper, on one new long-lived "watcher" thread; the mouse's activity taken from the pointer position (chosen).** The helper (`DeadWare.Devices`) gets a hidden window on its own thread that receives the shortcut, lock and sleep/wake messages. Idle time comes from `GetLastInputInfo`. "The mouse is being used" comes from polling `GetCursorPos`. The wireless mouse is a second device source inside the helper, beside OpenRGB. Trade-off: a click or scroll with no pointer movement does not count as mouse use, so in that rare case the mouse's effect resumes a moment later than in DeadLight. In return there is no hook and nothing that can read input content.

**B. As A, but with a low-level mouse hook, as DeadLight did (`uiohook`).** Sees clicks and scrolls exactly. Rejected: it is a second global hook in a process whose one existing hook is fenced by a strict rule and a source-scanning test; a slow hook callback delays every mouse movement on the PC; and the gain is cosmetic.

**C. Shortcut, idle and lock in the window program instead of the helper.** The window already has a message loop and the tray. Rejected: the merge design puts them in the helper (line 132); the rules must hold while the window is closed to the tray or has crashed; and the lights-off state would then live in a different process from the one that paints.

**Wireless mouse through OpenRGB instead of our own driver.** Rejected long ago and unchanged: OpenRGB's wireless Aerox detector hangs all detection while the mouse sleeps.

## What the owner sees

The mockup is the reference. Summary, with the exact wording to use.

### Everyday card (Lighting page)

A full-width card under the desk card (in both picture mode and plain mode), heading **Everyday**, sub-line "ways to turn the lights off without opening this page". Three columns that stack when the page is narrow:

- **Lights on/off shortcut.** Shows the keys, e.g. `Ctrl + Alt + L`. Buttons **Change** and **No shortcut**. Change puts the box into "Press the new keys…" (Change becomes **Cancel**, Esc cancels). A valid shortcut is one ordinary key together with at least one of Ctrl, Alt or the Windows key (Shift may be added but is not enough alone); otherwise the red line "Hold Ctrl, Alt or the Windows key together with another key." Hint: "Works anywhere in Windows. Same as the Lights on switch." With no shortcut: the box reads "None", hint "No shortcut. Use the switch or the tray instead." If Windows refuses the keys: red line "Another program already uses <keys>. Choose different keys." and the previous shortcut stays in force.
- **Off when the PC sits unused.** "After [list] minutes" and a switch. Hint: "Lights come back the moment you touch the mouse or keyboard."
- **Off when the PC is locked.** "Lights off at the lock screen" and a switch. Hint: "Lights come back when you sign back in."

The card is not shown while lighting is disabled in Settings. It is shown but disabled, with the page's existing status line explaining, while the helper is not connected.

### The reason line

A caution-coloured line at the top of the page, using the page's existing alert line:

- idle: "Lights are off because the PC has sat unused for N minutes. They come back when you touch the mouse or keyboard." (Seen only if the page is watched without input, or on a second screen.)
- locked: "Lights are off because the PC is locked. They come back when you sign in."
- waking: "Waking up… lights return in a few seconds."

While any of these is showing, or the switch is off, the desk picture and keyboard picture show no lights (hand-over, "Preview frames").

The page's muted status line ("… · lights off" today, read from the switch) follows the effective value, so it also reads "lights off" during idle, lock and waking and never contradicts the reason line.

A device that runs its own effect and cannot be turned off already carries the card note "Stays lit when lights are off" whatever the lights are doing (`LightingPage.cs:1611`). Nothing is added for it.

### Mouse card

One card, heading **Mouse · SteelSeries Aerox 9**, whichever way the mouse is connected. Sub-line by state:

| State | Sub-line | Battery line | Hint under the bar |
|---|---|---|---|
| wireless, fresh reading | "Wireless · Battery 85%" | "85%" | "On battery. Read while you use the mouse." |
| on its cable | "On its cable · Battery 60% · Charging" | "60%" | "Charging. Read every 10 seconds on the cable." (or "Fully charged." at 100%, or "Not charging." when the charging bit is clear) |
| receiver present, no fresh reading | "Receiver plugged in · mouse asleep or out of reach" | "85% · last known" (grey bar), or "Battery unknown" if never read | "Last known reading. Move the mouse to refresh it." |
| neither receiver nor cable | "Not connected" | "Battery unknown" | "Plug in the receiver or the cable." Sections dimmed. |

Sections, in order: Colour (as today, plus the line "The same colours are used on the cable and wireless."), **Battery**, **Sleep**, then the existing Lighting mode / Brightness / Colour correction sections unchanged.

**Sleep** section: "Mouse sleeps after" and a list: "Leave as the mouse is" (start value), "Never", "1 minute", "2 minutes", "3 minutes", "5 minutes", "10 minutes", "15 minutes", "20 minutes". Status line under it:

- start value: "DeadWare has not changed the mouse's own sleep time."
- chosen and delivered: "Saved and sent to the mouse."
- chosen while on the cable: "Saved. It is sent over the wireless receiver, so it reaches the mouse next time you use it wireless."
- chosen, receiver present but mouse not answering, or not connected: "Saved. Waiting for the mouse."

With a time chosen (not "Never", not the start value) a note box: "While the mouse lies still its lights hold steady, so it can fall asleep and save battery. Moving, clicking or scrolling brings the effect back." (Wording kept from the mockup. Because of approach A, a click or scroll alone resumes it only once the pointer also moves. The builder may shorten the note to "Moving it brings the effect back." Claude's call: use the shorter, accurate wording.)

Once a time has been chosen, "Leave as the mouse is" can be chosen again; DeadWare then stops sending a sleep time and stops holding frames, but cannot restore whatever the mouse had before.

The mouse stays in the desk picture as today; when "Not connected" it is drawn unlit and can still be selected, so the card and its settings stay reachable.

### Tray

Menu order: **Show dashboard**, **Turn lights off** / **Turn lights on**, separator, **Quit DeadWare**. The lights line is greyed and reads **Lights (not available)** when lighting is disabled in Settings, when the helper is not connected, or when the last state is not usable (helper starting, or the pipe refused because only the helper is elevated).

### Guide and release notes

`docs/` user guide gains the Everyday card, the mouse card and the tray line. Release notes and changelog for 0.22.0. The version in the tree becomes 0.22.0. No tag is created by the build; the owner is asked first.

## How it is built

### Lights-off state

One saved fact and three temporary ones, all owned by the helper:

- `Settings.LightsOn` (saved, exists today): the switch. Written by the existing `setLightsOn` request from the page and the tray, and by the shortcut inside the helper.
- `idle`: true while the idle rule is on and the system idle time is at least N minutes.
- `locked`: true while the lock rule is on and the session is locked.
- `suspended` / `waking`: see "Sleep and wake".

**Effective lights** = `LightsOn` and not idle and not locked and not suspended/waking. Everything that today asks "are the lights on" for painting, for the key-press hook (`Watch` / `StopWatching`) and for preview frames asks for the effective value instead. When effective lights turn false: paint lights-off exactly as the switch does today, remove the key-press hook, clear live presses, and raise a state. There is no "clearing frame" today and none is added: with lights off the host sends no preview frame, and the window clears its picture from the state (`LightingPage.cs:597`) and drops late frames (`:439`); the new reason in the state must drive those same two places. When effective lights turn true: clear every device's diff cache, repaint, re-install the hook if a layer needs it.

There is no single place that reads "lights on" today: the host passes `settings` into Core, which reads `LightsOn` in several places, and the window reads it in several more (the list is in the appendix). The builder either gives those Core methods the effective value or hands them a cached effective copy of the settings; `Clone` copies every table and is too heavy to call on every 33 ms tick. The hook stays driven from `DeviceHost.cs`, because `KeyPressRuleTests` pins the files that may name `IKeyPressSource`.

`HostState` gains an optional trailing member `LightsOffReason` (`null`, `"idle"`, `"locked"`, `"waking"`); the pipe `Version` stays 1. The window's picture logic (`LightingPaint.ShowsLights` and its callers) must treat a non-null reason as "no lights". The switch in the page header keeps showing `Settings.LightsOn`.

Precedence for the reason shown: waking, then locked, then idle. With the switch off, no reason is sent.

**The shortcut during an idle spell** (ported from DeadLight's `lighting-power.ts`, with its tests): pressing the shortcut is itself input, so it ends the idle spell. If, at the moment the shortcut arrives, the lights were last painted dark *only* because of idle, the press does not flip the switch: the lights simply return. This rule is for the shortcut only, because the shortcut toggles. The page switch and the tray always send an explicit target value through `setLightsOn`, and the helper simply sets it. The tray sends `setLightsOn(!on)` computed from the state that produced its label.

**Idle** is evaluated on the helper's existing run loop (500 ms; no new timer) from `GetLastInputInfo`. While the idle rule is off, the call is not made. The check, and the handling of shortcut, lock and sleep signals, must not sit behind the tick's early return for "OpenRGB is not running" (`DeviceHost.cs:379`) or wait for the 10 s start-up delay (`:257`): a shortcut pressed while OpenRGB is still starting flips and saves the switch at once, and painting follows when it can.

**Lock** comes from session notifications on the watcher window (`WTSRegisterSessionNotification`, `WM_WTSSESSION_CHANGE` lock/unlock). On start the helper assumes unlocked. A lock while the rule is off still records the fact, so switching the rule on while locked (not reachable in practice) is consistent.

### The watcher thread

A new class in its own file in `DeadWare.Devices` (not `KeyPressHook.cs`, whose imports are pinned): one long-lived STA-style thread that creates a hidden, never-shown top-level window (not a message-only window, which does not receive broadcasts), runs a message loop, and raises plain .NET events to `DeviceHost` (which only calls `Wake()` and reads flags on its own loop; no painting on the watcher thread):

- `WM_HOTKEY` from `RegisterHotKey` (with `MOD_NOREPEAT`). Registration and re-registration happen on this thread (a request from the run loop is marshalled to it by posting a message). Result (ok / refused) is reported back and held in state.
- `WM_WTSSESSION_CHANGE` for lock and unlock.
- `WM_POWERBROADCAST` for `PBT_APMSUSPEND`, `PBT_APMRESUMEAUTOMATIC` and `PBT_APMRESUMESUSPEND`. On a Modern Standby PC a top-level window may not get these; the builder also registers with `RegisterSuspendResumeNotification` for the same window. Resume may be signalled twice; the handler is idempotent.

The thread starts with the helper and ends on `stop`/exit (post `WM_QUIT`, join with a short limit, unregister the hotkey and session notification). If the window cannot be created, the helper logs it, carries on without shortcut, lock and sleep handling, and reports it in state so the Everyday card can say "Shortcut and lock are unavailable: <reason>".

The interfaces are small so `DeviceHost` tests run against fakes: `IEverydaySignals` (events: shortcut pressed, locked, unlocked, suspending, resumed; method: set shortcut → result) and `IInputClock` (idle milliseconds; pointer position).

### Input rule (extends 3b's key-press rule)

Stated for review, and enforced by extending `KeyPressRuleTests`:

- The shortcut is `RegisterHotKey` only. Idle is `GetLastInputInfo` only. Mouse use is `GetCursorPos` only.
- No `SetWindowsHookEx` outside `KeyPressHook.cs` (already enforced). No mouse hook anywhere: `WH_MOUSE_LL` and `WH_MOUSE` must not appear in either scanned project.
- The existing forbidden list stays as it is; nothing is removed from it.
- The watcher file and the input-clock file each get a pinned, exact import list in the test, in the same way `KeyPressHook.cs` has one, so adding an import to them is a deliberate, reviewed act. Today that pin is hard-wired to `KeyPressHook.cs` (`KeyPressRuleTests.cs:100-108`); it is reworked into a per-file table. It counts only `DllImport` + `extern` declarations, so the new files use that style (not `LibraryImport`), and the window procedure is a kept-alive delegate as in `src/DeadWare.WinUI/Tray.cs` (`delegate* unmanaged` is forbidden).
- The scan reads comments too. No file in `DeadWare.Devices` or `DeadWare.Core`, the new ones included, may name a forbidden function or `WH_MOUSE_LL` even in a comment.
- Turning the saved shortcut into words ("Ctrl + Alt + L") is done by a small table of DeadWare's own in the window project or Core; `MapVirtualKey` and `GetKeyNameText` stay forbidden.

Recording a new shortcut happens in the window (WinUI key events on the focused Everyday box, in DeadWare's own window), which is ordinary in-app key handling and outside the helper. Accepted keys: A to Z, 0 to 9, F1 to F24, and the named keys the table covers (builder's list; at least Space, Insert, Delete, Home, End, Page Up, Page Down, arrows, Pause). A key not in the table is refused with the same red line as an invalid combination.

### Everyday settings and requests

`LightingSettings` gains an optional `Everyday` member: `Shortcut` (modifiers + virtual key, or none), `IdleOff` (bool), `IdleMinutes` (one of the list values), `LockOff` (bool). Defaults: Ctrl+Alt+L, false, 10, false.

New request `setEveryday(settings)` (the name the merge design chose): validated in the helper (unknown minutes value or malformed shortcut is refused with a plain-word reply), then the shortcut is re-registered if it changed. If Windows refuses the new shortcut, the reply is a refusal with the "Another program already uses…" reason, nothing is saved, and the old shortcut is registered again. If the *saved* shortcut is refused at helper start (another program got there first), the state carries a `ShortcutProblem` text and the Everyday card shows it as the red line until a shortcut is set successfully.

Requests are applied under the host's lock (`DeviceHost.cs:545`), and `setEveryday` has to wait for the watcher thread's registration result before it can reply. That wait is bounded (about 1 s); on a timeout the request is refused in plain words and the old shortcut stays.

`HostState` already carries the settings copy; `Clone` in `DeviceHost` must list the new members or the window never sees them.

### The mouse: one device, two ways in

Facts (from DeadLight's `electron/aerox-wireless.ts` at commit `0497cd3`, which is the behavioural reference; where the merge design's line 240 gives the two product IDs the other way round, the DeadLight source wins and the builder confirms against it):

- Vendor `1038`. Receiver (2.4 GHz): product `1858`. Cable: product `185A`. Both: interface 3, usage page `FFC0`, usage 1.
- 64-byte reports, first byte 0. Every write is followed by one read with a 250 ms limit. Wireless opcode = wired opcode OR `0x40`.
- **Offsets.** The byte positions in this list are DeadLight's (node-hid), where a reply starts at the opcode. HidSharp puts the report ID at index 0, so on the wire the opcode is at index 1 and the battery byte at index 2. `IAeroxTransport` speaks DeadLight's offsets (payload without the report ID); the HidSharp adapter adds and strips the report ID. The ported tests then mean the same thing against the fake and the real mouse. HidSharp has no interface-number property: the right interface is chosen by usage page `FFC0` / usage 1 from the report descriptor (or `mi_03` in the device path).
- Brightness `63 0A` before every colour frame. Zones 0 to 2: `61 01 <zone> R G B`. Reactive zone: `66 01 00 R G B`.
- Sleep time: `69` + 24-bit little-endian milliseconds, then save `51 00` (its reply is optional). Sent on the receiver only.
- Battery: `D2` on the receiver, `92` on the cable. Reply byte 1: percent = `((b & 0x7F) - 1) * 5`, bit 7 = charging. On the cable, OpenRGB's lighting acknowledgements (opcode `21`) can arrive first: skip up to 16 non-matching replies inside the one 250 ms window, never resend.
- No firmware query, no DPI initialisation.

**Lighting route.** On the cable the mouse is lit by OpenRGB as today. On the receiver it is lit by a new driver in the helper using HidSharp (`DeadWare.Devices.csproj` gets its own HidSharp reference, pinned to 2.6.4 to match the existing third-party notice). OpenRGB's "SteelSeries Aerox 9 Wireless" detector stays switched off.

**The mouse follows the helper's state as a whole.** It is lit, its battery read and its sleep time sent only while lighting is enabled, no blocking conflict is found (DeadLight running is one, and it writes the same receiver) and OpenRGB is running, exactly as for every other device. Claude's call, for simplicity: a wireless mouse that works while OpenRGB is missing or failed would need a second paint path.

**A second device source.** `LightingDeviceService` today knows only OpenRGB. The builder introduces the smallest seam that lets one more device be listed, painted and marked absent without an OpenRGB index: the receiver mouse appears as a `LightingDevice` named "SteelSeries Aerox 9 Wireless", type mouse, four one-light zones Front, Middle, Rear, Reactive, one mode (Direct). It cannot be resized or put into a hardware mode; such requests are refused in plain words. It never takes part in OpenRGB index mapping, so an OpenRGB hot-plug cannot shift a mouse frame onto another device.

**One identity.** The saved colours, tuning and desk placement of the mouse live under one fixed device key, `aerox9`, whichever way it is connected. Rule:

- The cable device is recognised among OpenRGB's devices by name (starts with "SteelSeries Aerox 9") — the same test the desk picture uses — and is presented under the key `aerox9` instead of its serial/location key.
- The receiver device is presented under the same key.
- "Cable present" and "receiver present" are decided by which product IDs HID lists (re-read on the 5 s slow work), not by OpenRGB's list.
- At most one of them is presented at a time: cable present and OpenRGB lists the cable device → the cable device (and nothing is written to the receiver); else receiver present → the receiver device; else the mouse is kept in the device list as absent ("Not connected").
- **Absent mouse.** The mouse is listed as absent when neither route is available and either it was seen earlier in this helper run or the settings hold anything under `aerox9`. No new saved fact is added for "ever seen". An absent mouse is never painted and shows unlit in the picture; requests that only change saved settings (colours, tuning, sleep time) are accepted and take effect when it returns; requests that need the live device are refused in plain words. Today every consumer of the device list treats a listed device as live (paint, preview frames at `LightingDeviceService.cs:235`, the picture match at `DeskPicture.cs:77-81`, the request check at `DeviceHost.cs:597`); the seam must cover these.
- **The one migration (a copy, not a move).** The cable device's old key is `serial|location`, and the location changes with the USB port, so several old keys may exist and none is known while the mouse is wireless. Rule: at load, and again whenever the cable device is first listed, look for old entries that belong to the mouse: the key of the listed cable device if there is one, otherwise any key other than `aerox9` whose saved zone colours use only the zone names Front, Middle, Rear, Reactive and include Reactive. For each kind of setting separately (default colour and zone colours; tuning), if `aerox9` has none of that kind, copy it from the old entry (the listed cable device's entry first, otherwise the first match). Layout is not migrated: picture mode rebuilds it from live devices, and in plain mode the mouse is arranged afresh. The old entries are left where they are, so a rollback to 0.21.0 still finds the cable mouse's colours. The copy runs before the layout arrange in `RescanLocked` (`DeviceHost.cs:483`). Existing re-keying (`LightingKeys.Remap`) neither collides with `aerox9` nor helps here.
- A hardware lighting mode saved while on the cable (the cable device offers "Rainbow Breathing") falls back to Direct on the receiver, as the existing paint rule already does (`LightingPaint.cs:16-20`). The Lighting mode section lists what the presented device offers.
- **Cable arrival needs a hardware rescan.** When the cable product appears and OpenRGB does not list the cable device, the helper asks OpenRGB for one real hardware rescan, once per arrival, not on every poll (wiki `deadlight-wired-usb-detection-requires-openrgb-rescan`). No rescan is needed when the cable leaves. A hardware rescan rebuilds every OpenRGB controller, so all lights may blink for a few seconds when the cable is plugged in, as in DeadLight. After it, the helper must forget what it remembers about every device exactly as after a reconnect (`seeded`, Direct-mode, hardware-mode and sizing caches: `LightingDeviceService.cs:37,61,108-114,132`), or Direct mode and the Node Pro's mode and size are not sent again.
- **To verify first in the plan, before anything is built on it:** OpenRGB.NET 3.1.1 has no rescan call, no command ID for it, and tops out at protocol 4. DeadLight's own client sends the packet (`I:\DeadLight\electron\openrgb-sdk-client.ts:65-66`). The plan must establish from DeadLight's client and OpenRGB's source which packet it is, which protocol version it needs, and how DeadWare can send it (a new `IOpenRgbSession` member; reflection into the package or a short second connection are the candidates). If the bundled OpenRGB will not accept it from DeadWare, stop and come back to this design: the fallback is the owner's decision, and restarting OpenRGB is not it.

**Driver behaviour** (ported, with its vitest cases, from `aerox-wireless.ts`):

- One lock around every exchange with a 1.5 s deadline. A second caller does not queue: it gets "busy" and tries on the next tick. After a real failure (the write or read throws, or the device is gone) the port is closed, the device is marked stale, and the existing 5 s slow work reopens it.
- **No reply is not a failure.** A sleeping or out-of-reach mouse does not answer. That leaves the port open, sets the connection to "receiver present, no fresh reading", and is logged once when it starts and once when replies return, not per attempt. A must-send frame that got no reply is kept pending and retried when the pointer next moves, not on a timer.
- The transport sits behind an interface (`IAeroxTransport`: open, write, read with limit, close; enumerate present product IDs) so the driver and the host are tested against a fake.
- A HID call that never returns cannot be cancelled. The driver runs each exchange on a worker it can abandon: after the deadline the device is marked stale and that port is never reused; the helper stays responsive. (DeadLight needed an app restart here; the builder keeps the helper alive but does not promise recovery of the mouse without a helper restart, and logs it.)

**The sleep gate (the anti-goal).** A colour write restarts the mouse's own sleep countdown. So, on the receiver, when a sleep time other than "Never" has been chosen and delivered:

- A frame is sent only if the pointer position changed within the last 1 s, **or** the frame is a *must-send*.
- Must-send frames: the first frame after effective lights change (off or on), after the owner edits the mouse's colours, tuning or mode, after the port is (re)opened, and after wake. One must-send frame is one burst of writes; it restarts the countdown once, which is harmless. This is decision 9: lights-off reaches the mouse at once.
- While frames are being held, the helper does not count the mouse as failed, and the diff cache for the mouse is not advanced, so the next allowed frame carries the current colours.
- With "Never" or "Leave as the mouse is", frames flow continuously, as for any other device.
- This gate is not a straight port. DeadLight holds every frame until the next activity event, with no time window and no must-send, and counts clicks and wheel. The 1 s window, the must-send list and pointer-only activity are new here and need their own tests; only the "hold while still, release on activity" cases come from DeadLight's vitest file.

Pointer position is read on the run-loop tick the helper already has (33 ms while animating, 500 ms otherwise); no extra timer.

**Sleep time.** New request `setMouseSleep(minutes)`: `null` = leave as the mouse is, `0` = never, else one of 1, 2, 3, 5, 10, 15, 20. Saved in `lighting.json` as an optional `Mouse` member (`SleepMinutes`). Sent to the mouse on the next receiver exchange and again whenever the receiver port is reopened; the `51 00` save goes once per changed value per helper run. The state reports whether the current value has been delivered ("sent" / "waiting"), which drives the status line.

**Battery.**

- Receiver: at most once per 60 s, and only within 1 s of pointer movement. Never while the mouse lies still.
- Cable: every 10 s, through a short-lived handle opened and closed per reading (OpenRGB holds the lighting side).
- A reading is stale after 120 s, after a failed or invalid read, and when the cable is plugged or unplugged. A receiver lighting failure does not invalidate a cable reading. Not saved to disk.
- Connection state: `cable` if product `185A` is present; else `none` if the receiver is absent; else `receiver` (receiver present, no fresh reading) or `wireless` (fresh reading). HID presence is re-read on the 5 s slow work.
- Skipped, not queued, when the driver lock is busy.

`DeviceView` gains one optional trailing member `Mouse` (only on the Aerox): connection, battery percent (nullable), charging (nullable), stale, sleep minutes, sleep delivered. A changed value raises a state as any other change does. **Deviation from the merge design, Claude's call:** it named a separate `BatteryChanged` event; readings change in 5% steps a few times an hour, so a member on the existing state is simpler and needs no new event type. The plan should note this in the parent design's place if it touches that file.

### Sleep and wake

- **Suspend:** set `suspended`; stop all writes (OpenRGB and mouse); remove the key-press hook; clear live presses. Nothing is sent to the lights. The handler must return quickly and not wait on device I/O.
- **Resume:** set `waking`, raise a state (the page shows the waking line). After about 10 s: re-read OpenRGB's device list, forget everything remembered about every device as after a reconnect (diff, Direct-mode, hardware-mode and sizing caches), close and reopen the receiver port (which re-sends the sleep time), mark battery stale, clear `waking`, re-evaluate idle and lock, re-install the hook if needed, repaint. A second resume signal inside the wait restarts nothing.
- **No hardware rescan on wake** (deviation from the merge design's line 170, Claude's call): a hardware rescan makes every light blink and DeadLight never needed one on wake. Hardware check item 6 shows whether every device comes back; if one does not, a rescan on wake is added then.
- A device whose key changed across wake is handled by the existing re-keying; the builder adds no new mechanism, and the hardware check below records what actually happens.
- The hub's software-mode re-initialisation is step 5.

### The settings file

`lighting.json` gains `Everyday` and `Mouse`. `CurrentVersion` becomes 4. Written version: 4 only while `Everyday` differs from its defaults or `Mouse.SleepMinutes` is set; otherwise the existing 3 / 2 / 1 rule. Members equal to their defaults are not written. So a PC that never touches step 4's settings keeps a file 0.21.0 can save, and a rollback never strips settings (hand-over, line 24). The `aerox9` key itself needs no version bump (an older build simply sees an unknown device key in the table, as it does for any unplugged device). Existing tests that use version 4 as "a newer file" move to 5; they are in four files: `tests/DeadWare.Tests/LightingSettingsCodecTests.cs`, `tests/DeadWare.Tests/DeviceTuningTests.cs`, `tests/DeadWare.Devices.Tests/DeviceHostTests.cs` and `tests/DeadWare.Devices.Tests/LightingStoreTests.cs`. The written-version rule itself is `DeviceTunings.WrittenVersion` (`src/DeadWare.Core/DeviceTuning.cs:82`).

### Tray

`Tray` gets a third command and a way to ask for the current lights state when the menu is built (it is rebuilt on every right click): a callback returning (available, lightsOn), read from the session's last `HostState` and the Settings "Enable lighting" value. Clicking sends `setLightsOn(!lightsOn)` using the value the label was built from.

### Logging (also the hardware check's evidence)

The helper's log gets one line for each of: shortcut registered / refused (keys named as words, never anything typed); shortcut pressed and the resulting state; idle spell start and end with the minutes; lock and unlock; suspend, resume, and "wake repaint done" with the device count; receiver port open / close / failure; sleep time sent and saved; each battery reading (source, percent, charging); sleep gate "holding frames" start and "released" with the reason (pointer moved / must-send). No pointer coordinates are logged.

### Where the code goes

`DeviceHost.cs` is already large (3b progress, M10). New work goes in new classes: the watcher thread, the input clock, the lights-off rule (pure, in Core, with the ported tests), the Aerox driver and its transport, the battery poller, the mouse identity rule. `DeviceHost` only wires them.

## Testing

- **Core:** lights-off rule (precedence, the shortcut-during-idle case, rapid presses before a repaint: port DeadLight's `tests/lighting-power.test.ts`); settings codec for `Everyday` and `Mouse` including the written-version rule and round-trip through a 0.21.0-shaped file; shortcut validation and the key-name table; mouse identity and migration.
- **Devices:** driver against a fake transport (frame bytes, opcode `0x40` rule, reply skipping, deadline, busy, stale and reopen, sleep time + save once, gate holds and must-send cases: port the vitest cases from DeadLight); battery cadence with a fake clock; host integration with fake signals and fake input clock (idle off and back, lock, suspend/resume sequence with the 10 s wait under a fake clock, hook removed while dark, preview cleared); `KeyPressRuleTests` extended as under "Input rule".
- **Window:** Everyday card states (recording, invalid, refused, none, disabled), mouse card states from the table, reason line, tray label and greyed state, picture dark while a reason is set.
- The build and full suite pass before every commit; the measurement tests keep to their own job.

## Hardware check (owner's PC, after the build, each item approved by the owner one at a time)

Short, and each item leaves a line in the helper log that is copied out by the owner-run snapshot script (pattern: `hw-launch.ps1`, `hw-snap.ps1`, `hw-restore.ps1` from 3b). DeadLight and its tray must not be running. Report per item what the log confirms and what it does not.

1. Shortcut: press Ctrl+Alt+L twice. Log: pressed → off, pressed → on.
2. Idle: set 1 minute, hands off for 90 s, then move the mouse. Log: idle start, idle end.
3. Lock: switch the lock rule on, Win+L, sign in. Log: lock, unlock.
4. Mouse wireless: unplug the cable, pick a colour. Log: receiver open, a battery reading.
5. Mouse sleep: Wave running, sleep time 1 minute, leave the mouse still for 2 minutes; the owner looks at whether the mouse went dark. Log: sleep time sent, gate holding. (The log cannot prove the mouse slept; the owner's eye does, and the report says so.)
6. Sleep and wake: sleep the PC for a minute and wake it. Log: suspend, resume, wake repaint done.

Cable battery, charging state and the cable/receiver hand-over are covered by tests only unless the owner adds them.

## Left to the builder

- The exact shape of the second-device seam in `LightingDeviceService`.
- Names of new types and files, within "Where the code goes".
- The full list of named keys in the shortcut table.
- How the watcher thread marshals a re-registration (posted message or equivalent).
- Layout details of the Everyday card and the battery bar in WinUI, following the mockup and the page's existing card style.
- The wording of plain-word refusals not listed here, in the style of `HostReplyCodes`.

## Appendix: where "lights on" is read today (from the builder check, 2026-11-22, at `2141e00`)

Each of these must follow the effective value. The plan re-checks the list against the source.

- Core: `src/DeadWare.Core/LightingPaint.cs:76,90,94,95,118,125,130`; `src/DeadWare.Core/LightingLayers.cs:107`.
- Host: `src/DeadWare.Devices/DeviceHost.cs:130` (views: a hardware device's shown mode), `:167`, `:192` (hook), `:270` (tick rate), `:395`, `:396`, `:411`, `:412`, `:418` (paint and preview), `:446`, `:562`, `:771`, `:780`.
- Window: `src/DeadWare.WinUI/LightingPage.cs:439`, `:448`, `:597`, `:642`, `:701` (feeding `:719`, `:819`, `:855`, `:885`, `:887`).

`DeviceHost.cs:562` is the `setLightsOn` request itself and keeps writing the saved value.

## Constraints carried from the hand-over

- Never launch DeadWare, its helper, OpenRGB or DeadLight except through an owner-approved elevated script. Never run git or dotnet in `I:\DeadWare` itself (work in the `lighting-step4` worktree). Never tag without asking.
- `HostState` and `DeviceView` grow only by optional trailing members; pipe `Version` stays 1.
- Claude shells see shadow copies of AppData; evidence is copied out by an owner-run script.

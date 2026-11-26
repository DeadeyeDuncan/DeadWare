# Fan control

Status: design, written 2026-11-25. **Approved by the owner on 2026-11-25** ("Approve design"), after the mockups ("Approve mockups") the same day. Nothing in it is built yet.

Companion files:
- Mockups: [`2026-11-25-fan-control-mockups/fan-control.html`](2026-11-25-fan-control-mockups/fan-control.html) and its three PNG renders. The yellow numbers 1 to 14 in them are referred to below as "mockup 1" and so on.
- Hardware facts the build needs: [`2026-11-25-fan-control-hardware-notes.md`](2026-11-25-fan-control-hardware-notes.md) (the board's fan chip, LibreHardwareMonitor's behaviour, the hub's speed command).
- The hub driver this builds on: lighting step 5, `2026-11-25-lighting-step5-hub-design.md` (commit `b6bee1a` on branch `worktree-lighting-step5`, not on main when this was written), and [`2026-11-15-corsair-icue-link-notes.md`](2026-11-15-corsair-icue-link-notes.md).

Words used here: **the window** is `DeadWare.exe`; **the helper** is `DeadWare.Devices.exe`; **the guard** is a second, tiny `DeadWare.Devices.exe` process described below; **board fans** are fans on motherboard headers; **hub parts** are the radiator fans and the pump on the Corsair iCUE LINK hub; **handing back** means returning a fan to the BIOS, or the hub to its own mode.

## Why

The owner wants one program to run every fan and the pump in this PC, so that FanControl and iCUE can both go. iCUE had to go because its lighting fought DeadWare's on the hub; the lighting merge (steps 1 to 5) deals with that. FanControl goes because one app should run all the cooling. Fan control is the last piece.

Done means: FanControl and iCUE are no longer needed, nothing blinks, and the fans and pump do at least as well as they do now. It is a failure, even if it works, if the PC runs hotter or louder than today, if DeadWare disturbs the lights, or if a fan is ever left with nothing driving it.

## Goals

- DeadWare drives the board's case fans (stage 1) and the hub's radiator fans and pump (stage 2) from temperature curves.
- Three modes, Quiet, Balanced and Performance, each with a default curve for every fan and the pump. The owner switches by hand and can edit every curve.
- The owner's safety rules hold (see "Decisions the owner made").
- When the helper dies, a guard hands every fan back within seconds.
- Fan control works with lighting switched off.
- Nothing changes on a PC until its owner turns fan control on.

## Non-goals

- The graphics card's own fans. They stay on the card's curve.
- Pump headers on the motherboard. They stay on the BIOS.
- Switching modes automatically, a keyboard shortcut for the switch, per-app profiles.
- Alert rules for fans (the Alerts page). Faults show as status lines and tray notifications only.
- Fan chips other than the Nuvoton NCT6687D with the plain register layout, which is this PC's. Its relatives NCT6683D and NCT6686D share the layout but cannot be tested here, so they wait.
- Storing curves in the hub or the BIOS. Nobody documents how for the hub, and no documented way to set this board's BIOS curves from another program was found.
- Importing FanControl's settings.
- The Lighting Node Pro, and finding the cause of this PC's unexplained restarts (a separate investigation).

## Anti-goals (a failure even if everything else works)

- A fan or the pump left under software control with nothing driving it.
- The pump below its floor, or any fan stopped by DeadWare.
- The PC hotter or louder in Balanced than it is today.
- The intakes above their ceiling outside the overheat rule.
- Lights blinking, or the hub changing mode, because of fan control in normal use.
- A cooling change the owner did not ask for. Every hardware step in a check is described and approved one at a time.
- Two programs driving one fan.

## Decisions the owner made

In the owner's words where quoted (2026-11-25 unless dated):

- "I do want default settings for quiet, balanced, and performance modes for the Fan/Pump Curves."
- "Quiet is for low power likes like browsing the web, watching youtube. Performance is for hardware demanding tasks like gaming for example".
- Modes are switched "By hand".
- The exhaust runs at 100% "On purpose, only one rear exhaust".
- "the intakes get very loud above 50 and make this weird harmonic sound". Asked whether Performance may go higher: "Keep 50%".
- The build approach: "With the guard" (see "Approaches considered").
- "Approve mockups": the fourteen numbered choices, including the default curves and the limits.
- Carried from the 15 November brief, non-negotiable:
  - hand control back to the BIOS or the hub's own mode on quit, crash, sleep and update;
  - a minimum pump speed, and full or default speed when a temperature reading is lost;
  - never leave a header in software mode with nothing driving it;
  - plan how to test without risking the hardware;
  - don't change the cooling hardware's state without asking first; no on-screen automation while the owner is gaming; no release tag without asking.

## Approaches considered

| Approach | Outcome |
|---|---|
| Curves stored in the hardware, no program running them | Ruled out. No open implementation reads or writes the hub's stored cooling settings, and no documented way to set this board's BIOS curves from another program was found. |
| The window runs the fans | Ruled out. A window crash, hang or quit would freeze the fans; this is why the helper exists. |
| FanControl keeps the board fans, DeadWare does only the hub | Ruled out. Cheapest, but against the goal of one app. |
| **B. Helper only.** The helper runs the curves. If it dies, board fans hold their last speed until the window restarts it (normally 4 s), or until a reboot if it cannot be restarted | Not chosen. It does not meet the owner's crash rule. It is how FanControl behaves today. |
| **A. Helper plus a guard.** As B, plus a second tiny process that hands the fans back within seconds when the helper dies | **Chosen by the owner.** More to build and one more idle process. |

Neither A nor B covers a power cut or a hard reset; there the BIOS and the hub start on their own curves anyway.

Within A, how the board's fan chip is written (Claude's call):

| Way | Outcome |
|---|---|
| LibreHardwareMonitor 0.9.6's control API | Rejected. Its hand-back is inverted on this chip, its first write commands 0%, a write can be dropped without any signal and cannot be sent again, and its bookkeeping would fight DeadWare's own hand-back. |
| A 0.9.7 prerelease of the library | Rejected. Not a clean swap (sensor ids and driver modules change), it still restores a stale manual state after a crash, and it is a prerelease. |
| A patched build of the library | Rejected. It fixes only the clean exit, and it ends "distributed without source modifications". |
| **DeadWare's own small driver for every write; the library only reads** | **Chosen.** One owner of the chip's fan state, every write checked by reading it back, and a hand-back that does not depend on anything remembered. |

## Facts from this PC (read-only, 2026-11-25)

- **Hardware.** Ryzen 7 7700X, MSI B650M board with a Nuvoton NCT6687D, Radeon RX 6800 XT, Corsair TITAN 360 pump and three RX RGB radiator fans on an iCUE LINK hub (firmware 4.1.656), three front intake fans on one board header, one rear exhaust fan on another.
- **FanControl today** (version 257, running): the intakes (`control/2`, "Main Intake Fans") fixed at 50%; the exhaust (`control/3`, "Exhaust Fan") fixed at 100%; a CPU curve on `control/0`, a header that reads 0 RPM; the pump header (`control/1`) not controlled; the graphics card not controlled. Its bundled sensor library (0.9.5) has the same hand-back fault, so headers it leaves are expected to stay in manual mode until a reboot (the first checks confirm it).
- **FanControl's calibration**, used for every RPM estimate here: intakes 20% 373 RPM, 30% 707, 40% 1,053, 50% 1,359, 100% 2,395; exhaust 20% 478, 40% 924, 50% 1,132, 100% 2,047.
- **Sensor ids** carry a chip index: `/lpc/nct6687d/0/control/2`, `/lpc/nct6687d/0/fan/2`.
- **Temperatures** from DeadWare's own history, 302 hours from 31 October:

  | Kind of use | Share | CPU typical / warm end | Graphics card typical / warm end |
  |---|---|---|---|
  | Light | 75% | 63 / 73 °C | 51 / 59 °C |
  | In between | 16% | 71 / 77 °C | 67 / 75 °C |
  | Heavy (games) | 9% | 74 / 78 °C | 80 / 87 °C |

  The graphics card is the hot part: 80 to 88 °C in games, 94 °C at most, its own fans at 2,500 to 2,900 RPM. The CPU's highest reading was 93.4 °C, for moments only.
- **The pump** ran at 2,710 to 2,800 RPM for 96% of that time whatever the load. For 11.6 hours on 31 October and 1 November it ran slower and varied (2,060 to 2,450 RPM). Reading, unproven: slower and varying is the hub on its own stored curve; the steady speed is what it does while a lighting program holds it and sends no speed.
- **The radiator fans** have no history. Two readings: about 1,330 RPM with coolant at 38.4 °C (15 November) and about 1,660 RPM at 43.7 °C (25 November). Both fit iCUE's old curve (20% at 30 °C, 80% at 42 °C). Step 5's readings will show what they really follow.
- **This PC freezes.** Windows logged eight unclean restarts since 25 October. Three happened with the CPU under 67 °C and the graphics card under 51 °C, so heat does not explain those and fan control is not a fix for them. A separate investigation on 2026-11-25 reports about one freeze a week (21 since 30 August), most likely the graphics path locking up, with the owner pressing Reset afterwards; its findings are not verified here. What matters for this design: while Windows is frozen no program runs, the helper and the guard included (see "The guard").

## What the owner sees

The approved mockups are the reference. In short:

- **Mockup 1: a page of its own, "Fan control"**, after Cooling Health in the list.
- **Mockup 2: the mode switch**, Quiet / Balanced / Performance, with one line saying what the mode does.
- **Mockup 3: the status line.** Normal text: "DeadWare is driving every fan listed here. The guard is running. On quit, sleep, log-off or update the fans go back to the BIOS and the hub."
- **Mockup 4: one card per fan group**: name, where it is plugged in, speed now in % and RPM, the temperature it follows and that reading, and a small picture of its curve with a ring where the fan is now.
- **Mockup 5: the curve editor** for the selected fan in the selected mode. Drag a point; click the line to add one; right-click a point to remove it; "Reset to default". A readout follows the pointer ("62 °C → 37% · about 950 RPM").
- **Mockup 6: "Follows"**: Coolant, CPU, a graphics card (listed by name when the PC has more than one), or the hotter of CPU and graphics card.
- **Mockup 7: a floor and a ceiling per fan**, drawn as hatched bands on the editor.
- **Mockup 8: the overheat rule**, drawn as a band at the right of the editor.
- **Mockup 9: the default curves** (see "Defaults").
- **Mockup 10: limits no curve can cross** (see "Curves, limits and rules").
- **Mockup 11: the tray menu** gains "Cooling: Quiet ▸" with the three modes. It is greyed with the reason when fan control is not driving anything.
- **Mockup 12: the status line in other states.** Exact texts:
  - "Fan control is off. The BIOS and the hub are running the fans."
  - "FanControl is running. DeadWare won't touch the fans while it is. Quit FanControl, then turn fan control on."
  - "The helper stopped unexpectedly at 18:42. The guard gave the fans back to the BIOS and the hub. DeadWare took them again 6 seconds later."
  - "The graphics card reached 97 °C. Every fan and the pump are at full speed until it is back under 92 °C."
  - "The graphics card's temperature stopped arriving. The intakes and the exhaust are at their highest allowed speed until it returns."
  - "Exhaust Fan reads 0 RPM at 40%. DeadWare is holding it at full speed. Check the fan."
- **Mockup 13: Settings → Fan control**, the switch "Let DeadWare control the fans", off after the update, with the list of what must be true before it can be turned on.
- **Mockup 14: which fans.** One row per case-fan header with a fan, each with its own switch (off at first) and a name the owner chooses. The CPU Fan and Pump Fan headers are listed greyed with "stays on the BIOS". Hub parts are listed and greyed until stage 2.

Additions to the mockups (Claude's calls):

- Each board fan's row in Settings also has **Everyday speed**, a number in %. It is filled in with the speed the fan was running at when the row was first shown, and the owner can change it. The defaults are built from it (see "Defaults").
- **What Settings shows before the switch is on.** With the switch off and no helper running, the window shows the three checks it can make itself: administrator rights, the PawnIO driver, FanControl. The chip line and the rows of fans appear once the switch is on. Turning it on starts the helper if none runs; the helper identifies the chip and lists the case-fan headers that have a fan, reading only. Nothing is written until a fan's own switch is turned on. With no helper connected the switch shows the window's own setting.
- **The readout's "about 950 RPM"** comes from what DeadWare has seen. For each fan it remembers the settled speed at each 5% step it has driven, in memory only, and reads between the two nearest. With nothing learned near a speed the readout shows the percentage alone.
- Not running as administrator, or no PawnIO driver: the page and Settings say so in the words Settings already uses, with the existing "Restart as administrator" button. Nothing is driven.
- A board whose fan chip is not supported: "This board's fan chip isn't one DeadWare can hand back, so DeadWare leaves its fans alone." Hub parts can still be driven in stage 2.
- More status lines, same style: "Another program is changing Exhaust Fan's speed. DeadWare gave it back to the BIOS."; "The guard can't run, so DeadWare gave the fans back."; "Fan settings could not be read and were set aside. Fan control is off."; "These fan settings were made by a newer DeadWare. Fan control is off."

## How it is built

### Two stages

| | Stage 1: board fans | Stage 2: hub parts |
|---|---|---|
| Delivers | the helper running without lighting, the board fan driver, the guard, modes, curves, the page, the tray line, Settings | speed commands through step 5's hub worker; radiator fans and pump join the same modes |
| Lets go of | FanControl | nothing more (iCUE is already unused) |
| Starts | after lighting step 5 has merged, because both change the same helper files | after step 5 has passed its hardware check, including the silence test |
| Version, tentative | 0.24.0 | 0.25.0 |

Each stage has its own plan, its own hardware check, and its own release decision by the owner. Stage 1's plan is written after step 5 has merged, against the code as it then is, because step 5 rewrites the files stage 1 changes. The first checks under "To verify first in the plan" touch none of those files and can be run earlier if the owner wants; their results can still change this design. This document designs stage 1 in full. Stage 2 is designed as far as the facts allow; its plan is written after step 5 is built, and "The hub" below says what must be re-checked then.

### Three processes

- **The window** does what it does today, and now starts the helper for either lighting or fan control.
- **The helper** gains a cooling part beside the lighting part. The two share the process, the pipe and the log, and nothing else: the cooling part has its own thread, its own lock and its own clock (a `TimeProvider`, so tests use fake time). Cooling work never takes the lighting loop's `gate` while it talks to hardware, and lighting's waits, conflicts and OpenRGB's state never pause or stop cooling.
- **The guard** is the same executable started with `--guard`. It loads no sensor library and no OpenRGB code. All it can do is watch the helper, clear the board's manual bits, and send the hub its "own mode" command.

### Starting the helper

Today the window starts the helper only when lighting is on and an OpenRGB path is set, and `Main` exits with code 2 without `--openrgb`. Changes:

- The window starts the helper when lighting is on **or** fan control is on.
- `--openrgb <path>` becomes optional. A new argument, `--fans`, allows a start without it; with neither, exit code 2 as today. Without `--openrgb` the lighting part reports "off" and does no work; everything else runs.
- **The cooling part is in every helper and is switched on and off over the pipe.** Turning fan control on while the helper already runs for lighting restarts nothing, and the lights do not blink. Turning it off does not stop a helper that lighting still needs.
- **A lighting change still restarts the helper, as today:** lighting switched on or off, a new OpenRGB location, and the "turn lighting off and on" remedies all stop the helper and start it with its new arguments. With fan control on, the fans are handed back at that Stop and taken again by the new helper, so for the seconds between (the 4 s launch floor plus the start) they run at the BIOS's speed. Starting and stopping lighting inside a running helper would avoid that and is not done: it would rework the lighting lifecycle for a rare event.
- Fan control needs an elevated helper with the PawnIO driver reachable. Otherwise the cooling part reports why and takes nothing. The helper still never elevates itself.
- **Supervision changes in two places** (`DeviceHostClient`):
  1. When a Stop has not ended the helper within 5 s the window ends the helper's whole process tree (`Kill(entireProcessTree: true)`). The guard is in that tree. The window must now end the helper and its other children and leave the guard running, so the guard can hand the fans back.
  2. `FindRunning` picks any `DeadWare.Devices.exe` of this session with the right path, which now also matches the guard. The window tells the two apart by the process id of the pipe's server.
- The rest of supervision is unchanged: the window relaunches a dead helper after 4, 8, 16 and 32 s and gives up at the fifth exit in ten minutes. After a give-up the fans stay on the BIOS and the hub, where the guard put them.
- When the window dies the helper keeps running, as today, and keeps driving the fans under the guard.
- One owner per machine: the cooling part holds a machine-wide named mutex while it drives anything, so a second signed-in user's helper reports "Another DeadWare session is driving the fans." and takes nothing.

### The cooling loop

Once a second, on the cooling thread:

1. Read the temperatures and fan speeds.
2. Apply the rules in "Curves, limits and rules" to get each fan's wanted speed.
3. Write the speeds that changed, within the write limits.
4. Renew the guard's lease.
5. Publish the state if anything visible changed.

A turn that overruns is not queued; the next turn starts a second after the last one ended.

### Temperatures

The helper opens its own read-only `Computer` from LibreHardwareMonitorLib (the 0.9.6 package DeadWare already ships): CPU and GPU on, everything else off, the motherboard included. One instance only. It never calls `SetSoftware` or `SetDefault`; a test that scans the source forbids both names in `src`.

**Its updates run behind a bounded runner, not on the cooling thread**, in the way the window's storage reader already works (`HardwareUpdates`): one update per piece of hardware at a time, never a second while the first is still pending, once a second. The cooling loop only looks at the last values and their times. A slow or stuck sensor read therefore becomes a lost reading (rule 4), not a stuck cooling loop. This matters on this PC: the library's AMD graphics-card update has been reported to stall for more than 10 s under load, and a stuck cooling loop would make the guard end the helper and OpenRGB with it. The instance is closed and opened again at the end of the wake wait.

| Source | Value |
|---|---|
| CPU | the CPU's package temperature, chosen by sensor name from a fixed list and never by which sensor happens to have a value, so the source cannot change between turns |
| a graphics card | that card's core temperature. The card is saved by an identity made from its type and model, not by the library's id, which follows detection order. When fan control is first turned on, the window passes the graphics card it has selected. |
| Coolant | the hub worker's last reading (stage 2) |
| Hotter of CPU and graphics card | the larger of the two |

A value is fresh for 5 s. A source with no fresh value between 0 and 120 °C for 10 s is **lost**. Curves use an average of the source over 20 s in Quiet, 10 s in Balanced and 5 s in Performance. This is the "waits for a sustained rise" of mockup 2.

Board fan speeds (RPM) come from the board fan driver itself, read under the bus lock, so the cooling loop knows each reading is fresh. The sensor library skips a board pass without any signal when the lock is busy, and finds no chip at all if the lock is busy while it opens; that is why the helper's `Computer` leaves the motherboard off. The window keeps its own `Computer` for the Sensors page, as today.

### Curves, limits and rules

**A curve** is 2 to 8 points of (temperature in whole °C, speed in whole %). Speed never falls as temperature rises; the editor and the settings check both refuse a curve that does. Below the first point and above the last the curve is flat. Between points it is a straight line.

**Limits** (mockups 7 and 10):

| | Floor | Ceiling |
|---|---|---|
| A fan | 20% unless the owner sets it higher; never under 20%; never stopped | 100% unless the owner sets it lower |
| The pump | 50%, fixed in code | 100% |

A wanted speed is the curve's value at the averaged temperature, kept between the fan's floor and ceiling.

**Calm.** A fan speeds up as soon as its wanted speed is 2 points or more above its current one. It slows down only when the curve, read 2 °C above the averaged temperature, is still 2 points or more under its current speed. A board fan gets at most one write every 2 s; the hub gets at most one speed write every 2 s.

**Rules, first match wins:**

1. **Not driving** (off, a conflict, a fault that gave the fan back): nothing is written.
2. **Overheat** (mockup 8, the same in every mode, not editable): the CPU's 30 s average at 92 °C or more, any graphics card's 10 s average at 97 °C or more, or the coolant at 55 °C or more puts every driven fan and the pump at 100%, ceilings ignored. It ends when all of them have been at least 5 °C under their figure for 60 s. These readings are watched whether or not a fan follows them. A reading that is lost cannot trip this rule; rule 4 covers the fans that follow it.
3. **Stalled fan:** a driven fan that reads 0 RPM for 10 s gets its ceiling, which is full speed unless the owner set a lower one, and the mockup 12 text ("at full speed", or "at its highest allowed speed" when the ceiling is lower). It ends when the fan has read above 0 RPM for 30 s. A fan whose speed cannot be read at all is a different case; see "The board fan driver".
4. **Lost reading:** a fan whose source is lost runs at its ceiling, with the mockup 12 text, until the source has been fresh for 10 s.
5. **The curve.**

### Defaults

Mockup 9 as numbers. "Reset to default" puts back these curves for the selected fan and mode.

| Fan | Follows | Quiet | Balanced | Performance |
|---|---|---|---|---|
| Radiator fans | Coolant | 20% up to 34 °C, 45% at 42, 70% at 47, 100% at 52 | 20% up to 30 °C, 80% at 42, 100% at 48 | 40% up to 30 °C, 100% at 42 |
| Pump | Coolant | 60% up to 40 °C, 100% at 50 | 90% | 100% |
| A board fan | Graphics card (CPU when the PC has no separate graphics card) | from *q* up to 55 °C to *E* at *t* °C | *E* | *E* up to 55 °C, the fan's ceiling at 75 °C |

*E* is the fan's Everyday speed, kept between its floor and ceiling. *q* is 10 + 0.3 × *E*, rounded, and not under the floor. *t* is 65 + *E* ÷ 10.

On this PC that gives exactly the approved mockup: the intakes (Everyday 50%, ceiling 50%) run 25% up to 55 °C and 50% from 70 °C in Quiet, and 50% in the other two; the exhaust (Everyday 100%) runs 40% up to 55 °C and 100% from 75 °C in Quiet, and 100% in the other two. Balanced is therefore what this PC does today.

The pump's Balanced figure stands for "today's speed, about 2,750 RPM". Which percentage gives 2,750 RPM is measured at stage 2's hardware check, and the default is corrected then if 90% is off.

A fresh install starts in Balanced. The mode is saved and survives restarts.

### The board fan driver

DeadWare's own code for the Nuvoton NCT6687D with the plain register layout. Any other chip is unsupported and never written; that includes its relatives NCT6683D and NCT6686D (same layout, untested) and boards the sensor library treats as the "DR" layout. The registers, the handshake, the bus lock and the ways in are in the hardware notes. This board has five fan headers while the chip has eight controls; a control with no header behind it is never written.

- **The way in** is the PawnIO driver's signed LpcIO module, used directly through a small client of DeadWare's own, because the library's I/O classes are not public. DeadWare never installs or changes a driver, as today.
- **The lock.** Every access holds `Global\Access_ISABUS.HTP.Method` and follows the chip's page-register convention. Waits are bounded: at most 500 ms for the lock per attempt, then the attempt counts as failed. An abandoned lock, one whose owner died, counts as acquired: the waiter now owns it.
- **What it can do**, and nothing else:

  | Operation | Meaning |
  |---|---|
  | identify | read the chip id and revision and confirm a supported chip before anything else |
  | read mode, read duty, read speed | for one control: its manual bit, its duty, and its fan's RPM |
  | take | put one control in manual mode at the duty it is running at right now, so nothing audible changes |
  | set duty | write a duty, then read it back; up to three tries, then a fault |
  | release | clear the control's manual bit, then read it back; up to three tries, 100 ms apart |
  | release all | release every control it was told about, even ones this process did not take; then, if the chip's request register still shows an open request because a writer died in the middle of a write, close it |

- **An allow-list in code** names the only registers the driver may write (the mode register, the duty registers, the request register). A test proves no other register can be written and that no duty under 20% can be sent.
- **Only case-fan headers are offered:** controls 2 to 7 of this chip, which the sensor library names "System Fan". The CPU Fan and Pump Fan headers (controls 0 and 1) are never offered and never taken, whatever is plugged into them, and neither is a control with no fan. A CPU cooler's fan or pump stays on the BIOS, which watches the CPU itself. If the library's board table shows a board where controls 0 and 1 are something else, that board is unsupported.

**Taking a board fan:**

1. Must hold first: fan control on; the fan's switch on; a supported chip; an elevated helper; PawnIO reachable; no conflict; the guard running with a fresh lease; and the fan's RPM read above 0 within the last 5 s.
2. Record the fan in the guard's lease. Only then:
3. Take it at its current duty.
4. Move it to its wanted speed under "Calm".

A fan that fails a step is not taken, with a status line saying why, and is tried again after 30 s, doubling to 10 minutes.

**"Another program is changing it":** when a fan's duty read back differs from what DeadWare last wrote, three times in 60 s, DeadWare releases that fan and says so (text above). It does not take it again until fan control is turned off and on, or DeadWare restarts.

**No speed reading:** when a driven fan's speed cannot be read for 10 s, DeadWare can no longer watch that fan and releases it: "DeadWare can't read Exhaust Fan's speed, so it gave it back to the BIOS." It is tried again the way a failed take is.

**A release that cannot be confirmed.** A release normally takes a few milliseconds. With three tries of at most 500 ms each for the lock it takes about 2 s at worst. If the manual bit still reads set after that, or the lock never came:

- the helper logs it, does not set the lease's clean flag, and carries on with whatever it was doing (a Stop is never held up);
- the guard, which the window's forced end spares, sees a helper gone without the flag and keeps trying once a second for 8 s, which is inside the updater's 10 s wait for helper processes. Then it records the fan as "not handed back" in its file and exits;
- the next helper shows "DeadWare could not hand Exhaust Fan back to the BIOS, because another program held the fan chip. It keeps its last speed until DeadWare takes it again or the PC restarts." With fan control on it takes the fan again as usual.

At sign-out both processes end together, so the helper's own release, started the moment Windows announces the session's end, is the only one.

### The guard

Started by the cooling part before it takes anything, as a child process, so it has the helper's rights. The window's forced end of a stuck helper leaves it running (see "Starting the helper").

- **The lease** is a small record the helper keeps current and the guard can read after the helper is dead: the helper's process id, a heartbeat renewed every second, a "stopped cleanly" flag, and the list of what is taken (chip and control numbers; "hub held"). A process without administrator rights cannot write it. How it is stored is the builder's choice within those rules.
- **The heartbeat is renewed by the cooling loop itself**, not by a timer of its own, so a loop stuck in a sensor read or a chip write shows as a stale heartbeat.
- **The guard does nothing while the lease lists nothing taken.**
- **The guard acts** when something is listed and the helper's process has ended without the flag, or the heartbeat is more than 5 s old. It then:
  1. ends the helper and the helper's other child processes if the helper is still there (a hung helper must not fight the hand-back);
  2. releases every listed board fan, each checked by reading back;
  3. if "hub held": sends the hub its own-mode command once, waiting at most 2 s for the Corsair lock, then closes;
  4. writes what it did, with the time, to a small file beside the fan settings;
  5. exits.
- **Targets:** fans handed back within 3 s of a crash or kill, and within 8 s of a hang.
- **A clean stop:** the helper hands everything back itself, sets the flag, and the guard exits without acting.
- **The guard is watched too.** The helper holds the guard's process handle. If the guard ends while anything is taken, the helper starts it again; after three failures in a row it hands everything back and reports "The guard can't run, so DeadWare gave the fans back." No guard, no control.
- **Afterwards** the window's ordinary supervision restarts the helper. The new helper reads the guard's file, shows the mockup 12 line, and takes the fans again if fan control is on. If the helper keeps dying and the window gives up, the fans stay handed back.

The guard cannot help when both processes are ended at once, at a power cut or at a hard reset. The board's firmware sets the fans up again at the next start.

**It cannot help while Windows is frozen either**, and this PC freezes about once a week. The fan chip has no timer of its own that returns a header to the BIOS, so during a freeze each board fan holds the speed it had at that moment, until Windows recovers or the PC is reset. That speed was the curve's answer to the temperature just before the freeze. FanControl behaves the same way today, with the difference that its speeds are fixed at 50% and 100% while DeadWare's Quiet speeds in light use are lower. The hub parts are better off: the hub falls back to its own curve after 10 to 20 s of silence, if step 5's silence test confirms that for this firmware. The design accepts this limit; nothing in software can remove it.

### Stop, sleep and the rest

| Event | What happens | Bound |
|---|---|---|
| Quit, Update & restart, Roll back, restart as administrator, a lighting change that restarts the helper, and fan control switched off while lighting is off too | All are the existing Stop request. The cooling part is told the moment the request arrives, before anything waits for the lighting loop or its lock, and releases the board fans on its own thread at once. (Today a Stop first waits for the host's lock, and the loop may be inside a turn that waits 5 s for OpenRGB to exit; the fans must not wait for that.) `DeviceHost.RunAsync`'s `finally` then waits for that release, bounded, and hands the hub back (step 5) before OpenRGB is stopped. The clean flag is set last. | board: milliseconds normally, 2 s at worst; hub 1.5 s (step 5's limit) |
| Fan control, or one fan's switch, turned off while the helper keeps running | The cooling part releases that fan, or all of them, checks each by reading back, and reports. Lighting is not touched. | as above |
| The helper ends itself (step 5's rule for a stuck USB call) | It releases the board fans first if the cooling thread still runs. The guard covers the rest. | one try, then the guard |
| Sign-out, shut down | The window sends Stop (step 5 adds this). The helper also releases by itself when Windows announces the session's end, in case the window is gone. | as above |
| Sleep | Board fans are released in the suspend handler, one try each. The hub goes quiet, as step 5 has it. | 500 ms |
| Wake | Nothing for the existing 10 s wait. Then everything is taken again from the start: the chip is identified again, each fan's mode and duty are read again, the hub is taken as step 5 does it and its speeds are sent. | |
| The helper crashes, is killed, or hangs | The guard. | 3 s / 8 s |
| The window crashes or is closed while the helper lives | Nothing changes. | |
| Power cut, hard reset | Nothing can run. The firmware and the hub start on their own curves. | |
| Windows freezes | Nothing can run. Board fans hold their last speed until Windows recovers or the PC is reset; the hub falls back by itself if its silence fallback exists (see "The guard"). When the cooling loop runs again after a stall it simply carries on from fresh readings. | |
| Update to a version with fan control | Fan control is off until the owner turns it on. | |
| Roll back to a version without it | The Stop before the swap already handed everything back. | |

The sleep, wake and session-end signals come from the helper's existing hidden window (`EverydayWatcher`), which must now run whenever cooling runs, also with lighting off. Handling the session-end messages needs no new Windows import, so the file's pinned import list stands. If the watcher cannot start, the cooling part takes nothing and says "DeadWare can't see sleep and sign-out on this PC, so it leaves the fans alone."

### Conflicts

- **FanControl** running blocks board fans, and makes DeadWare release them if it appears later. It is matched by its executable's file name and product name read from the process's path, not by process name alone. The check is the cooling part's own and runs on the cooling loop: when fan control is turned on and every 5 s after, also while lighting is off or OpenRGB is still starting. It is not part of the lighting conflict scan, which does not run in those states. A hit is a fan-control conflict: it shows in the Fan control status line and in Settings → Fan control, not under Settings → Lighting.
- **iCUE and the Corsair services** block hub parts exactly as step 5 has them block the hub.
- Any other fan program is caught by "Another program is changing it" above.

### The hub (stage 2)

Built on step 5's hub worker, which already owns the hub on its own thread with the lock, the transactions, the device list, the readings, the saved hub mode and the hand-back on every controlled stop. Step 5's design names what fan control must add; this is how:

- **Hub parts can be driven only in hub mode `driven`**, that is, after the owner has turned on step 5's own switch. In mode `openrgb` the rows in mockup 14 stay greyed with "Turn on 'DeadWare drives the hub' in Settings → Lighting first."
- **The speed command** (endpoint `0x18`, layout in the hardware notes) is added to the worker's allow-list. One frame carries the pump and every radiator fan. The frame builder cannot build a pump value under 50%, a fan value under 20%, or a value for a part whose type DeadWare does not know; tests prove it, in the way step 5 proves no speed can be built at all.
- **When speeds are sent:** at once after every take or re-take of the hub, on every change under "Calm", and again every 10 s unchanged. The radiator fans always get one common value.
- **A take that does not wait for lighting.** The worker holds the hub when lighting's conditions hold **or** when hub parts are switched on for fan control and no hub conflict is present. So the hub is taken at helper start without waiting for OpenRGB, and it is kept through OpenRGB's failures.
- **Lighting off, hub parts on.** The hub has one mode for lights and cooling. DeadWare holds it for cooling and sends black, re-sent as step 5's keep-alive, so the hub's lights are dark. The page says: "The hub's lights are dark because DeadWare lighting is off while DeadWare drives its fans."
- **After each speed write that lowers the pump:** at the first readings 15 s later the pump must read 1,000 RPM or more and every radiator fan above 0 RPM. Otherwise the worker sends 100% to all, lets go (the hub's own mode), and reports "The hub's fans did not follow DeadWare. The hub is back on its own curve." Step 5's stopped-pump rule stays as it is.
- **Step 5's take-over check** ("more than 10% slower gives the hub back") compares speeds with no command sent. It stays for the take itself. Speeds are first sent only after that check has passed.
- **Turning hub parts off while lighting keeps the hub:** the hub keeps the last speeds it was sent, and no command returns it to "no speed sent". So the worker lets go and takes the hub again once: the lights show the hub's own effect for a moment. This is the one deliberate mode change fan control causes.
- **A helper crash:** the guard sends the own-mode command. If it cannot (the lock is held, the hub does not answer), the hub's own fallback after 10 to 20 s of silence applies, which step 5's silence test confirms or refutes for this firmware.

**To re-check before stage 2's plan is written:** step 5 as built (the worker's calls, its conditions for holding, its texts); the silence test's result; what the fans and pump do in `driven` mode before any speed is sent (step 5's readings over a day of use); and the pump's and fans' RPM at 50, 60, 90 and 100%, measured with the owner's approval.

### Settings

- **`cooling.json`** is the helper's, in a protected folder `%ProgramData%\DeadWare\cooling` made with the existing `ProtectedFolders` maker (administrators and SYSTEM write; users read). Fan settings are applied to hardware by an administrator process, so they do not live in the user-writable `lighting.json` or share its failure modes.
- It holds: a version (1); whether fan control is on; the mode; and per fan: its identity (chip and control number, or hub part kind), name, switch, Everyday speed, floor, ceiling, what it follows, and a curve per mode where the owner changed one. Unchanged curves are not stored; they are the defaults.
- Every value is checked on read against the limits in this document. A file that fails is set aside under a dated name, fan control is off, and the status line says so. A file with a newer version is left untouched and fan control is off.
- A change is refused if it cannot be saved; the live settings stay.
- **`settings.json`** (the window's) gains only `FanControlEnabled`, default off, which tells the window to start the helper. The helper drives fans only when its own file says on. If the two disagree the window shows the helper's answer.

### The pipe

Additions only; `Version` stays 1 because window and helper ship together.

- Requests: get cooling state; turn fan control on or off; set the mode; set a fan's switch, name, Everyday speed, floor, ceiling and source; set a fan's curve for a mode; reset a fan's curve; subscribe to cooling readings.
- An event of its own for cooling readings (per fan: speed in %, RPM, the source's temperature, which rule is in force; the guard's state; any fault), sent at most once a second while a window part is subscribed, and at once when a rule or fault changes. It is not carried in `HostState`, which re-sends all lighting settings each time.
- `HostState` gains one optional trailing member with what the tray and the status line need without subscribing: on or off, the mode, and the current problem if any.

### The window

- **The page** `fans`, built in code like the others, its canvas made when shown and dropped when hidden (the existing rule for large drawings).
- **The curve editor** is the first pointer-edited curve in DeadWare. Its geometry, hit-testing and the "speed never falls" rule live in Core with tests, as `DeskGeometry` does for the desk; the view follows `DeskView`'s press, capture, drag and cancel pattern, and sends through the existing `Throttle`.
- **Texts** live in Core with tests, as `EverydayTexts` and `TrayLights` do: the status lines, the mode lines, the tray line.
- **The tray** takes its cooling line and submenu the way it takes the lights line.
- **Notifications** are tray balloons, at most one per kind per hour: the guard stepped in; the overheat rule started; a fan stalled; a reading lost for more than 60 s; another program changing a fan.
- **Wording.** User-facing "lighting helper" (30 strings, 19 places in the guide) becomes "helper", since it now does more than lighting.
- The user guide and the release notes are updated in each stage with what that stage really does.

### Monitoring and Cooling Health

- The Sensors page is unchanged. Board fan speeds and control values already appear there with Extended sensors on.
- Cooling Health is unchanged except for one line when fan control is on: "Fan control is on, so temperatures also change with the cooling mode." Its comparisons match on fan speed, so minutes recorded in another mode simply do not match a reference; that is accepted for now.

### Logging and notices

- `devices.log` gets one line for every take, release, fault, rule change, mode change and guard action. These lines are the hardware checks' evidence.
- `THIRD-PARTY-NOTICES.md`: the PawnIO LpcIO module if a copy is shipped; a courtesy note for the chip facts as the hardware notes word it. LibreHardwareMonitor stays "distributed without source modifications".

## To verify first in the plan

**On paper first.** The hardware notes name what the research did not record. Before any hardware step, settle these from the public sources the notes list and write the answers into the notes: which of the chip's three ports is page, index and data, and how its base address is found; where a fan's tachometer reading is; what the four LpcIO functions take and return; and the nct6687d driver's licence, for the attribution.

**Then on the PC:** a throwaway tool, run by the owner, each step described and approved before it runs. Steps 1 to 3 change nothing.

1. From an elevated process, DeadWare's own PawnIO client loads the LpcIO module and reads the chip's id and its mode and duty registers.
2. The same reads while FanControl runs. Expected: the intakes' and exhaust's controls manual at 50% and 100%.
3. The owner quits FanControl. Read again. Expected: still manual.
4. Take the exhaust at its current duty, set 80%, read back, watch the RPM follow, set it back.
5. Release it. Read back. Note what speed the BIOS gives it: this is what "handed back" sounds like on this PC.
6. The sensor library inside the single-file helper: it publishes cleanly, and reads CPU and graphics card temperatures and fan speeds when elevated.
7. Ten minutes of writes with the window polling at 0.5 s: count lock waits, retries and read-back mismatches.
8. Sleep and wake: read the mode bits after waking.

If step 1 fails the build stops and comes back to the owner. The fallback, the library's control API with its three-call hand-back, is what this design rejected.

## Testing

- **Core, pure:** curve reading, the "never falls" check, floors and ceilings, Calm, each rule and their order, the defaults and the Everyday formulas, settings checks, every text.
- **The board driver** against a fake chip that models the mode, duty and request registers, the page convention and a lock that can be held by "someone else": take changes nothing audible, a set duty is read back, a dropped write is retried, release clears the bit, the allow-list holds, an unknown chip is never written.
- **The cooling loop** on fake time with a fake temperature source: taking order (lease before write), lost readings, stalled fans, overheat on and off, a conflict appearing, "another program is changing it".
- **The guard** with a fake helper process and lease: a clean stop does nothing; a dead helper and a stale heartbeat each release everything listed; a hung helper is ended first; the guard's own death makes the helper hand back after three failed restarts.
- **Stop ordering:** a Stop releases the fans at once, even while the lighting loop holds its lock or waits for OpenRGB, and before OpenRGB is stopped.
- **The window:** its forced end of the helper leaves the guard running; it picks the helper, not the guard, when both are running; a lighting change with fan control on stops and restarts the helper.
- **Sensor reads:** a read that does not return makes its values lost and leaves the cooling loop turning.
- **A release that cannot be confirmed:** no clean flag, the guard's 8 s of tries, its record, and the next helper's status line.
- **Stage 2:** the speed frame builder's limits; the worker's new holding condition; the 15 s check; turning hub parts off.
- No test touches real hardware. CI has none.

## Hardware checks (owner's PC, each item approved one at a time)

**Stage 1**

1. The owner quits FanControl and switches off its start with Windows. Read the controls.
2. Turn fan control on, switch on the two fans, set Everyday speeds 50% and 100% and the intakes' ceiling 50%. In Balanced nothing audible changes.
3. Quiet in light use: the intakes and exhaust slow to about the mockup's figures; no fan stops.
4. Performance and back.
5. A game in Quiet: the intakes reach 50% and stop there; the exhaust reaches 100%.
6. End the helper in Task Manager. The guard hands both fans back within 3 s; the status line reports it; DeadWare takes them again.
7. Sleep and wake.
8. Quit DeadWare: both fans are on the BIOS.
9. Update & restart from the test build to itself.
10. Start FanControl while DeadWare drives the fans: DeadWare lets go and says so.

**Stage 2**, after step 5's own check: the pump and fan RPM at 50, 60, 90 and 100%; Balanced against today's speeds; Quiet in light use; a game; ending the helper (the guard sends the hub its own mode); sleep and wake; turning hub parts off; lighting off with hub parts on.

The overheat rule and the lost-reading rule are checked by tests only. Nothing in a hardware check heats the PC on purpose.

## Out of scope and later

- Everything under "Non-goals".
- A switch that follows what the PC is doing, once the hand switch has proved itself.
- Fan alerts on the Alerts page.
- Cooling Health comparisons that take the mode into account.
- A listening test that finds the band where the intakes' harmonic sits, offered at stage 1's hardware check if the owner wants it.

## Left to the builder

- Class, file and project layout; the names of the new pipe messages and their shapes.
- How the lease is stored and protected, within its rules.
- How the LpcIO module is obtained (the copy the sensor library carries, or a shipped copy with its notice); fail closed if it is missing.
- The exact back-off numbers within the bounds given, and how a turn of the cooling loop is split.
- Win2D drawing details of the page and the editor.
- How a graphics card's identity is hashed, following the graph cache's existing way.

## Calls made in writing this up (for the owner to check)

1. **DeadWare's own driver sets board fan speeds, not the sensor library.** Earlier I said speeds would still go through the library. Reading its code changed that: it can drop a speed change with no signal and no way to send it again, and its bookkeeping would fight DeadWare's own hand-back. The library now only reads.
2. **Everyday speed.** Each board fan has one number, and Balanced holds it. Quiet and Performance are built from it by the two small formulas under "Defaults", chosen so that this PC gets exactly the approved curves.
3. The pump's Balanced default is 90% until stage 2's check measures which percentage gives today's 2,750 RPM.
4. Fan settings live in a protected folder that only administrators can write, not beside the lighting settings.
5. Hub parts can be driven only once DeadWare drives the hub (step 5's switch). With lighting off and hub parts on, the hub's lights are dark.
6. Turning hub parts off makes the hub's lights show its own effect for a moment, once.
7. The guard's timings: fans handed back within 3 s of a crash and 8 s of a hang.
8. FanControl is the only other fan program looked for by name. Any other is caught when it overwrites a fan's speed.
9. Motherboard pump headers are never driven.
10. The averaging times (20, 10 and 5 s) and the 2-point, 2 °C calm rule.
11. The overheat rule cannot be edited or switched off.
12. A fan's floor cannot be set under 20%.
13. Stage 1 is built after step 5 merges. Versions 0.24.0 and 0.25.0 are tentative; tagging is the owner's call.
14. No fan alerts on the Alerts page yet; faults show in the status line and as tray notifications.
15. Cooling Health gets one line of text and no other change.
16. "Lighting helper" becomes "helper" in the app and the guide.
17. A fresh install starts in Balanced.
18. **A lighting change restarts the helper, as today, so the fans run at the BIOS's speed for a few seconds** when lighting is switched on or off or OpenRGB's location changes. Turning fan control itself on or off restarts nothing.
19. **Only case-fan headers can be driven.** The CPU Fan and Pump Fan headers stay on the BIOS whatever is plugged into them. On this PC that changes nothing.
20. **A stalled fan gets its ceiling, not always 100%.** Mockup 10 says "full speed"; for the intakes that would break the 50% rule, so they get 50%. Only the overheat rule passes a ceiling.
21. A fan whose speed can't be read is given back to the BIOS.
22. The readout's "about N RPM" is learned from what DeadWare has seen, and shows only the percentage until then.
23. The rows of fans in Settings appear after the main switch is on, each with its own switch off; nothing is written until a fan's own switch is on.
24. **A frozen Windows is accepted as a limit.** Neither the helper nor the guard can run then, so board fans hold the speed they had when it froze. This PC freezes about once a week, so it will happen. The alternative is to keep Quiet's board fan speeds no lower than today's, which gives up most of what Quiet is for.

## Constraints carried

- Nothing on the owner's cooling hardware changes without the owner's explicit yes for that step. Every live step is described first.
- No on-screen automation while the owner is gaming.
- Ask which way to build (the dev-mode picker) when the build starts.
- Never tag a release without asking.
- Facts from GPL and unlicensed projects may be used; their code may not be copied.
- Device ids and serial numbers never enter the repository.

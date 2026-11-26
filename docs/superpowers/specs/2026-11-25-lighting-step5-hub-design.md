# Lighting step 5: the hub

Date: 2026-11-25. Base: main `7e16054` (0.22.0, lighting steps 1 to 4). Target version: 0.23.0.
Status: approved by the owner on 2026-11-25, after an independent builder check whose answers are written in.
Parent design: `2026-11-15-lighting-design.md` (called "the merge design" below). Protocol facts: `2026-11-15-corsair-icue-link-notes.md` ("the notes").
Approved mockup: `2026-11-25-lighting-step5-mockups/hub.html` (approved by the owner 2026-11-25; it is the reference for every visible label and state in this document, except where "Additions to the mockup" says otherwise).

## Why

The Corsair iCUE LINK System Hub carries the three radiator fans and the pump of the owner's cooler. It has one mode that covers lights and cooling together, so two programs driving it fight and the lights blink. Today OpenRGB holds the hub whenever DeadWare's lighting runs, nothing hands it back on Quit, and nothing in DeadWare can read the coolant temperature. iCUE has to stay off for the same reason, and DeadLight is still installed.

Step 5 lets DeadWare become the hub's one owner: its own driver lights the hub and reads it, Quit hands the hub back, the owner can see coolant temperature and fan and pump speeds, and DeadLight can be retired. It is the last lighting step. Fan control builds on this driver and is a separate project.

Success: with the switch on, the hub's lights follow DeadWare without blinking, the coolant temperature is on a graph, quitting DeadWare returns the hub to its own stored behaviour, and no fan or pump speed was ever set by DeadWare.

## Goals

- A saved switch in Settings → Lighting, off after the update, decides who lights the hub: OpenRGB (as today) or DeadWare.
- With the switch on, DeadWare's own driver lights the hub's parts and keeps the hub in its hands through OpenRGB restarts, lights-off, idle and lock.
- DeadWare reads coolant temperature and fan and pump speeds while it drives the hub, and shows them on the hub's card, on the Sensors page and as an optional line on the CPU temperature graph.
- Taking the hub is checked: speeds before and after are compared, and a failed check gives the hub back and stops.
- While DeadWare drives the hub, Quit, lighting off, an update and a conflict hand it back to its own built-in mode.
- Settings → Lighting shows what of iCUE and DeadLight is still in the way, with plain instructions, and can retire DeadLight.

## Non-goals

- Fan and pump control: any speed command to the hub, curves, profiles. The driver cannot build a speed write (see "The allow-list").
- The front three fans. They are on a different Corsair device, the Lighting Node Pro, and stay as today: through OpenRGB, on their own Rainbow Wave.
- Lighting single LEDs inside a fan or the pump ring. Each part stays one colour, as today.
- More than one iCUE LINK hub on a PC, the hub's LCD variants, a hub with more parts than fit in one reply, and Corsair devices other than this hub.
- A third switch position that leaves the hub alone all the time.
- Cooling Health using coolant temperature. Only its wording is corrected.
- Reading the hub while OpenRGB lights it, except the one reading the take-over dialog shows.
- Changing any service, start-up entry or file of iCUE. DeadWare shows them and tells the owner what to do.
- Any change to what happens to the hub while the switch is off. With the switch off, 0.23 treats the hub exactly as 0.22 does, Quit included.
- The open points logged from steps 3a to 4.

## Anti-goals (a failure even if everything else works)

- **DeadWare sets a fan or pump speed**, or sends any command not on the allow-list.
- **The hub's mode changes without the owner's say-so.** The update must change nothing until the switch is turned on, and switching on must ask first. With the switch off, DeadWare's driver sends the hub no mode command at all.
- **The hub flaps between its own mode and DeadWare's**, visibly or not: no repeated mode switches, no blinking in normal running.
- **A silent, slow or unplugged hub stalls the other lights**, the shortcut, idle, lock or the mouse, or keeps the shared Corsair lock from other programs.
- **DeadWare and another program drive the hub at once**: OpenRGB with its hub detector on, iCUE, a Corsair service or DeadLight.
- **With the switch on, the hub is left held with nobody driving it** after a stop DeadWare controls (Quit, lighting off, an update, a conflict, a failed check).
- **Retire DeadLight stops or deletes anything that is not DeadLight's.**
- **The recorded history changes meaning**: the CPU pump line and Cooling Health must keep using the same sensor as before.

## Decisions the owner made

- **O1.** DeadWare takes the hub only "After my switch" (2026-11-25): a switch in Settings → Lighting, off after the update; while off, nothing changes and the hub's lights stay on OpenRGB.
- **O2.** The mockup is approved, with its fourteen listed choices:
  1. The switch is in Settings → Lighting and is off after the update. Switching on always asks first and shows the hub's readings. Switching off gives the hub's lights back to OpenRGB. Either way all lights blink once, because OpenRGB restarts.
  2. After a take-over, if the pump or a fan runs more than 10% slower than just before (compared about 15 seconds after), DeadWare gives the hub back to its own built-in mode, switches itself off and waits for the owner to choose: back to OpenRGB, or try again.
  3. While DeadWare drives the hub, a pump that reads 0 RPM for about 10 seconds makes it give the hub back and say so. It does not take it again by itself.
  4. Lights off, idle and lock make the hub dark while DeadWare keeps hold. Only Quit, lighting off, an update, a conflict or a failed check hand the hub back.
  5. Each fan and the pump stay one colour each, and the hub's colours are carried over once.
  6. Readings show on the hub's card, as five rows on Sensors, and as the optional coolant line. They exist only while DeadWare drives the hub.
  7. The coolant line starts unticked and is saved in the 6-hour graph history like the other lines.
  8. Cooling Health is left as it is; its wording is corrected.
  9. iCUE running, or a Corsair service running, blocks the take-over. "Starts with Windows" alone is a warning.
  10. Retire DeadLight switches off its start-up task and stops it, and deletes nothing. The button shows only when there is something to retire, and needs DeadWare running as administrator.
  11. A hub with a part DeadWare cannot identify for certain is refused: the switch stays off, with the reason.
  12. Not included: a third position, "leave the hub alone".
  13. The version is 0.23.0.
  14. The front three fans are not part of this step.
- **O3.** After this step is finished, remind the owner about the possible next step: a DeadWare driver for the Lighting Node Pro so the front fans follow the desk effects ("make sure to remind me about that next step after we finish this one").
- **O4.** Step 4's hardware check was skipped by the owner. Step 5's hardware check also covers its seven items (proposed to the owner, not objected to).
- **O5.** The owner approved one read-only, full-frame reading of the hub for test data ("Yes, read it", 2026-11-25). It was taken; see "Facts from the 2026-11-25 reading".
- Standing rules from the hand-over: nothing touches the hub without the owner's approval; the owner disables iCUE himself from DeadWare's instructions; never tag without asking.

Everything under "How it is built" is Claude's call, made because the owner handed over the how. None of it was put to the owner as a choice.

## Approaches considered

**A. The hub as a second device inside the lighting loop, like the mouse.** The driver is called from the loop's tick under the host's lock. Least new structure. Rejected: the tick paints nothing while OpenRGB restarts, reconnects (back-off up to 30 s) or settles, so the hub would get no traffic, could fall back to its own mode and be taken again, which is the mode flapping this step exists to end. Every wait on the hub would also sit under the lock that the other lights, the shortcut and the mouse need.

**B. A hub worker of its own inside the helper (chosen).** One long-lived thread owns the hub: the HID handle, the shared Corsair lock, the mode, the keep-alive, the readings. The lighting loop never waits on the hub. It hands the worker the newest colours and reads the worker's last known state. Trade-off: one more thread and a hand-off to test. In return the hub stays held and steady whatever OpenRGB does, a silent hub cannot stall anything, and fan control gets an owner it can extend.

**C. A separate process for the hub.** The strongest isolation. Rejected: another executable to start, supervise, update and keep out of the updater's way, when the helper is already the crash-isolated process and nothing in this step needs more.

**D. Leave the hub's lights on OpenRGB and read temperatures alongside.** Cheapest. Rejected: DeadWare would not own the hub, nothing would hand it back, reading beside another owner is the fragile route the earlier research set aside, and fan control would have nothing to build on.

## Facts from the 2026-11-25 reading

A read-only reading of the owner's hub (firmware, device list, temperatures, speeds; every reply kept whole) was taken on 2026-11-25 at 17:52 local time, while DeadWare 0.22's OpenRGB held the hub. The capture, `hub-capture-20261125-175236.txt`, and the script that took it are in step 5's records folder, outside the repository, because the replies contain the hub's device ids.

- Firmware 4.1.656. The interface reports usage page `FF42`, usage 1. Input and output reports are 513 bytes with the report id.
- One round trip (write, then the matching reply) took 2 to 10 ms. A colour frame of four round trips is therefore tens of milliseconds, not hundreds.
- Device list: 13 channels reported, channels 1 to 3 type `0x0F` model `0x00`, channel 13 type `0x11` model `0x02`, each with a 26-character id; empty channels take 8 bytes; the whole list used 215 of the reply's 512 bytes.
- Temperatures: 52 entries. Entry 0 (the hub itself) 33.7 °C; entries 1 to 3 unavailable; entry 13 (coolant) 43.7 °C.
- Speeds: 26 entries. Entry 0 unavailable; fans 1,660, 1,654 and 1,663 RPM; pump 2,774 RPM.
- Replies to close and open carry leftover bytes from earlier replies after the header, as the notes say.
- The shared lock `Global\CorsairLinkReadWriteGuardMutex` existed (OpenRGB creates it) and the reading ran under it without disturbing the lights, as on 2026-11-15.
- **The fans are not at one fixed speed while OpenRGB holds the hub.** On 2026-11-15 they read about 1,330 RPM with coolant at 38.4 °C; now about 1,660 RPM with coolant at 43.7 °C (pump 2,716 then, 2,774 now). Either the hub keeps following its own stored curve when no speed is commanded, or it holds whatever speed it had when it was taken. Two readings cannot tell which. The step's readings will show it within a day of use; it matters to fan control, not to this step's design.

Fixtures in the repository are these replies with every id replaced by a synthetic one of the same length.

## What the owner sees

The mockup is the reference. Summary, with the exact wording to use. "The hub" in running text is always the iCUE LINK hub; visible headings name it in full. Speeds are written with a thousands separator, temperatures with one decimal.

### Settings → Lighting

Order of the section: **Enable lighting** (as today), **OpenRGB location** (as today), then three new headings, then **Conflicts** (as today).

**Corsair iCUE LINK hub.** A switch labelled "DeadWare drives the hub", a state line under the label, and under both the fixed line: "When this is on, DeadWare lights the hub itself and reads its coolant temperature and its fan and pump speeds. DeadWare never sets fan or pump speeds."

| State | Switch | State line and anything under it |
|---|---|---|
| off | off | "Off. The hub's lights go through OpenRGB, as they do today." |
| switching | as it will be, disabled | "Switching… the lights blink once." |
| driving | on | "On. DeadWare is driving the hub.  Coolant 38.5 °C · Pump 2,709 RPM · Fans 1,331 · 1,326 · 1,340 RPM", and under it "Taken over today at 19:42. Before: pump 2,716 RPM, fans 1,337 · 1,326 · 1,337 RPM." (the second line only while a before reading exists from this helper run; "today" becomes the date on a later day) |
| let go | on | "On, but DeadWare has let go for now." and a caution line: "DeadWare has let go of the hub because <reason>. The hub is on its own built-in lights and fan settings. DeadWare takes it again <when>." (reasons and "when" under "Letting go") |
| gave back | off | "Off. The hub is on its own." and a red-edged box: "**DeadWare gave the hub back.** <what happened> The hub is now on its own built-in lights and fan settings, and stays that way until you choose." with buttons **Back to OpenRGB, as before** and **Try the take-over again** |

"What happened", by the failed check:

- slower: "After the take-over the pump ran at 2,010 RPM, down from 2,716 RPM before." For a fan, "fan 2" (on the owner's picture "radiator fan 2") replaces "the pump".
- no reading: "After the take-over DeadWare got no speed reading for the pump (2,716 RPM before)."
- stopped pump: "While DeadWare was driving the hub the pump read 0 RPM for 10 seconds."

Red lines under the switch (one at a time, cleared by the next click):

- Blocked: "DeadWare can't take the hub yet: <reasons joined with ', and '>. See below for what to do, then try again." Reasons, in this order: "iCUE is running", "the Corsair service is running", "DeadLight is running", "another OpenRGB is running", "no iCUE LINK hub was found", "the hub has a part DeadWare doesn't know (<what the hub reported>)", "the hub has more parts than DeadWare can read", "this PC has more than one iCUE LINK hub", "lighting is not running", "DeadWare can't save its lighting settings".
- Stale reading: "That reading was more than two minutes old. Click the switch again."
- Interrupted: "The take-over did not finish (<why>). The switch is off and the hub's lights stay with OpenRGB."
- Still switching: "DeadWare is still switching. Try again in a moment."

The switch is disabled, with the page's existing explanation, while lighting is disabled or the helper is not connected.

**The take-over dialog.** Title "Let DeadWare drive the Corsair hub?". Body, in order:

- "This changes which program is in charge of a cooling device, so DeadWare asks first."
- "OpenRGB restarts without the hub, so all lights blink once. DeadWare then takes the hub and lights it."
- A box headed "Read from the hub just now" with Coolant, Pump and the fans. If the reading could not be taken: "DeadWare could not read the hub just now, so it cannot compare speeds afterwards. It will still give the hub back if the pump stops."
- "After taking over, DeadWare reads the speeds again. If the pump or a fan runs more than 10% slower than above, DeadWare gives the hub back to its own built-in settings and tells you." (left out when there is no reading)
- Muted: "DeadWare never sets fan or pump speeds."

Buttons **Take over the hub** and **Cancel**. The comparison is made against exactly the numbers the dialog shows. Switching off needs no dialog.

**iCUE.** Lead line: "iCUE and DeadWare can't both drive the hub. iCUE stays installed; these three things only need to be off. DeadWare shows them and never changes them for you." Then three lines, each with a mark (green tick, red cross, yellow exclamation) and, when not green, an instruction under it:

| Fact | Good | Not good | Instruction |
|---|---|---|---|
| iCUE program | "iCUE is not running." | red: "iCUE is running." | "Quit it from its icon by the clock: right-click the icon and choose Quit." |
| Corsair services | "The Corsair service is not running and does not start with Windows." | red: "The Corsair service is running, and starts with Windows." / red: "The Corsair service is running." / yellow: "The Corsair service starts with Windows." | "Press Win + R, type services.msc and press Enter. Find <display name>, right-click it and choose Stop. Then double-click it and set Startup type to Manual." (the Stop sentence only when running, the Startup type sentence only when it starts with Windows; with more than one service not good, one instruction each, naming it) |
| iCUE at sign-in | "iCUE does not start when you sign in." | yellow: "iCUE starts when you sign in." | "Open Task Manager (Ctrl + Shift + Esc), choose Startup apps, find <entry name> and choose Disable." |

Red means it blocks the take-over, and is exactly what the helper's scan blocks on; yellow never blocks. A PC with no trace of iCUE (no such process, service or start-up entry) shows the single line "iCUE is not installed on this PC." and no instructions.

**DeadLight.** Shown only when DeadLight is found (its start-up task exists, or it is running). Lines: "DeadLight is not running." or red "DeadLight is running."; "DeadLight's start-up task is switched off." or yellow "DeadLight starts when you sign in (the Task Scheduler task "DeadLight")."; when the task does not exist the task line is left out. While either is not green: a **Retire DeadLight** button. Not elevated: the button is disabled with "Restart DeadWare as administrator to retire DeadLight." When both are green: "DeadLight is retired. Its folder and its settings are still on this PC; delete them yourself whenever you like." (preceded by "Retired just now." after a successful retire in this window).

Retire dialog: title "Retire DeadLight?", body "DeadWare will:", "• switch off DeadLight's start-up task. It is not deleted, so you can switch it back on in Task Scheduler;", "• stop DeadLight and the OpenRGB it started, if they are running.", muted "Nothing is deleted.", buttons **Retire DeadLight** and **Cancel**. A failure shows as a red line with the reason in plain words and leaves the lines as they are. When DeadLight's folder cannot be confirmed (see "Retire DeadLight"), the result line is "DeadWare switched off the start-up task but could not confirm which programs are DeadLight's, so it stopped none. If DeadLight is still running, quit it from its icon by the clock."

### Lighting page

- **Status line under the title.** Off: unchanged ("8 devices through OpenRGB · Wave is running"). Driving: "7 devices through OpenRGB · the Corsair hub is driven by DeadWare · Wave is running". Let go or gave back: "7 devices through OpenRGB · the Corsair hub is on its own · Wave is running". The device count never includes a hub that DeadWare drives or has let go of.
- **Reason line** (the caution line step 4 added), while the hub is on its own: "DeadWare has let go of the Corsair hub because <reason>. The hub shows its own built-in effect until <when>." or, gave back, "DeadWare gave the Corsair hub back because <the pump / fan 2> ran slower after the take-over. Choose what to do in Settings → Lighting." ("because it got no speed reading for <the pump / fan 2> after the take-over", "because the pump stopped"). It ranks below waking, locked and idle: one reason line shows at a time.
- **The hub in the picture.** Driven, or lit through OpenRGB: as today. On its own: the three fan bars and the pump ring are drawn as grey outlines with "own effect", the way the front fans are, and the caption gains "· own effect". The hub stays selectable.
- **The hub's card.** Sub-line starts "lit through OpenRGB", "driven by DeadWare" or "on its own · DeadWare is not lighting it". Zone rows as today, dimmed while on its own. A new **Readings** section after the zone rows: rows Coolant, Pump and the fans ("Radiator fans" on the owner's picture, otherwise "Fans"), values like "38.5 °C", "2,709 RPM", "1,331 · 1,326 · 1,340 RPM", and the muted line "Read from the hub every few seconds. DeadWare shows these speeds; it never sets them." Off: the section holds only "Readings appear here once DeadWare drives the hub. Switch that on in Settings → Lighting." On its own: "No readings while the hub is on its own." A reading that has gone stale shows "—".
- Brightness and Colour correction on the hub's card work as today on either route.

### Sensors page

While DeadWare drives the hub, one row per reading, hardware "Corsair iCUE LINK hub": "Coolant" (°C), "Pump" (RPM) and one row per fan (RPM). Fan rows are named after the picture's parts where the owner's picture applies ("Radiator fan 1" to "Radiator fan 3"), otherwise "Fan 1", "Fan 2", … in channel order. A hub with a second coolant sensor names each coolant row after its part. The hub's own temperature (entry 0) is not shown. Rows are absent, not dashed, while DeadWare is not driving the hub. The pump therefore appears twice on the owner's PC, once from the motherboard as today and once from the hub; that is intended.

### Coolant line on the CPU temperature graph

Settings → Appearance gains, directly under the pump tick box, "Show coolant temperature on CPU temperature graphs" with the help text "Adds the Corsair hub's coolant temperature on the same °C scale, dashed and teal by default. It is drawn only while DeadWare drives the hub." It starts unticked. With it ticked, every CPU temperature graph (Overview, pop-out, widget, history ranges) gains a legend line "Coolant · 38.5 °C · dashed", a colour dot that opens the same line-style editor as the others, and the line. With no reading the line has a gap and the legend says "Coolant · no reading (DeadWare is not driving the hub)", or "Coolant · no reading (the hub is on its own)" while it is let go or gave back, or plain "Coolant · no reading" when lighting is off or the helper is not connected. The line appears under Settings → Appearance → Graph lines like the pump line.

### Additions to the mockup (Claude's calls)

- The transient "Switching…" state and the "still switching", "stale reading" and "did not finish" red lines.
- **Try the take-over again** first gives the hub's lights back to OpenRGB (as the other button does), waits until OpenRGB lists the hub again, and then opens the take-over dialog by itself with a fresh reading. That way a second try can again compare speeds. If the hub is not listed within 60 s: "The hub has not come back through OpenRGB yet. Click the switch when its lights are back."
- The mockup's legend text "· the line stops where DeadWare let go" is not shown; it explained the mockup.
- The "no speed reading" and fan variants of the gave-back texts, and the extra blocked reasons.

### Guide and release notes

`docs/user-guide.md` gains the hub switch, the take-over, what Quit does with the switch on and off (its sentence at line 250 is rewritten), the iCUE and DeadLight lines, Retire DeadLight, the readings and the coolant line; its line 231 ("flow and coolant monitoring are not included yet") is rewritten to say that Cooling Health does not use coolant temperature and where the hub's coolant reading is shown. `docs/releases/v0.23.0.md` is written; the version in the tree becomes 0.23.0. The note for going back to 0.22 says what "The settings file" and "OpenRGB's detector" below say. Both say plainly which behaviours were not tried on a real PC. No tag is created by the build; the owner is asked first.

## How it is built

### Hub mode (saved) and hub state (not saved)

One saved fact, owned by the helper: the **hub mode**, one of

- `openrgb` (the default): OpenRGB's hub detector is on. DeadWare's driver opens the hub only for the dialog's one reading and sends it no mode command. Everything else about the hub is as in 0.22.
- `driven`: OpenRGB's hub detector is off; the worker takes the hub whenever the conditions below hold.
- `stopped`: a check failed. OpenRGB's detector is off and the worker does not take the hub. Only the owner leaves this mode. The saved mode carries the reason and its numbers, so the box survives a restart.

The **hub state** the helper reports is derived: `off` (mode `openrgb`), `switching`, `driving`, `letGo` with a reason (mode `driven` but not held), `gaveBack` (mode `stopped`).

**A mode is saved only when it is settled.** During a take-over the helper works in an unsaved in-between mode; `driven` is saved when the check has passed, `stopped` when it has failed. A helper that dies, is stopped, or meets a conflict before that leaves `openrgb` on disk, so the next start finds the hub exactly as before the switch was clicked.

**Conditions for holding the hub** (mode `driven`): lighting is enabled and OpenRGB has been launched and connected at least once in this helper run; OpenRGB has not failed for good (status Failed or OpenRgbMissing); no blocking conflict and none of the hub-only conflicts in "Conflicts"; the PC is not suspended or waking; exactly one hub is listed by Windows; the last attempt is not inside its retry delay. While they hold, the worker takes the hub and keeps it. When one stops holding, the worker lets go (see "Letting go").

**Claude's call, for simplicity:** the first take in a helper run waits for the lighting loop, as every other light does. The hub is then *kept* through OpenRGB crashes, restarts, reconnects and rescans, with its last frame, and given up only when lighting stops, is blocked, or OpenRGB fails for good. A hub that DeadWare lights while OpenRGB is missing altogether would need a second paint path; fan control can add an independent take when it needs one.

### The hub worker

A new class with one dedicated, long-lived thread. The shared lock belongs to a thread, so every transaction runs on that thread; no thread pool. It owns the transport, the lock and all timing, and is the only code that talks to the hub. `DeviceHost` and `LightingDeviceService` talk to it only through calls that return at once:

- post the newest frame (one colour per LED-bearing part; the worker expands it to LEDs);
- set dark (lights off, idle, lock): the worker sends black instead of the posted frame;
- allow or forbid holding, with the reason (the host evaluates the conditions above on its loop);
- go quiet at once (suspend) and resume (wake end);
- start one reading while not holding (the dialog); the result arrives in the snapshot;
- let go and tell me when done (a task the caller may wait on with its own limit);
- read the last known snapshot: state, the listed parts, the last readings with their age, the last reading result for the dialog, the last failure.

The worker raises one event, "changed", and the host turns it into `Wake()`; nothing is painted or sent on the worker's thread except to the hub.

Timing, all on the worker's own clock (a `TimeProvider`, so tests use fake time):

| What | Value |
|---|---|
| Colour frame after a change | at most one every 125 ms (about 8 a second) |
| Unchanged colours re-sent | every 2 s (keep-alive; the merge design said 5 s, which leaves one missed round inside a 10 s fallback; 2 s leaves four) |
| Readings (temperatures, then speeds) | every 3 s |
| Wait for the shared lock | at most 250 ms, then skip the round |
| Wait for one reply, and for one write | at most 250 ms each (the transport's own limits) |
| One whole transaction | at most 1.5 s |
| The dialog's reading, all of it | at most 2 s |
| Retry after a failed take or a failed transaction | 30 s, doubling to 10 min; back to 30 s after a success |
| Re-entering software mode after a "wrong mode" reply | at once, but at most 3 times in 10 minutes |

A skipped round is not a failure. A missing or malformed reply, or a HID error, is: the worker closes the handle, reports `letGo` with "the hub stopped answering", and retries on the delay above. Nothing is retried faster. A fourth "wrong mode" reply inside 10 minutes makes the worker let go with "the hub keeps falling back to its own mode" instead of entering software mode again.

**A hub call that never returns.** The Aerox driver abandons a hung call by running each exchange on another thread; the hub cannot, because the thread that makes the calls also owns the shared lock, and other programs wait on that lock without a limit. So:

1. Every read and write has the transport's own 250 ms limit, which is the ordinary way out.
2. A watchdog outside the worker's thread (the host's loop already runs every 500 ms or faster) sees a transaction older than 1.5 s and closes the hub's handle from outside, which is meant to cancel the pending call. The worker then releases the lock, counts a failure and retries on the delay.
3. If the worker's thread has still not come back 3 s after that, the helper logs "the hub's USB call is stuck; ending the lighting helper so other programs get the Corsair lock back" and ends itself with its own exit code. Windows then releases the lock. The window's ordinary supervision restarts the helper (4 s floor, five exits in ten minutes gives up).

Claude's call: losing all lights for a few seconds in a case that needs a wedged USB stack is better than holding a lock other cooling tools wait on for ever.

### The transport and the protocol

Behind an interface (`IHubTransport` or the builder's name): list the hubs Windows shows (vendor `1B1C`, product `0C3F`, usage page `FF42`, usage 1); open one, shared; write one 513-byte report within a limit; read one report within a limit; close (callable from another thread). The HidSharp adapter is the only HidSharp code for the hub, picks the interface by usage page and usage from the report descriptor and never by path alone, never opens interface 1, and strips the report-ID byte so offsets match the notes. It is not unit-tested, like `HidAeroxTransport`.

Framing, commands, endpoints, transactions, parsing and device types are exactly the notes. Additions and corrections:

- **Entry counts.** The temperature and speed replies report 52 and 26 entries on the owner's hub. The earlier research's bound "count ≤ 25" is wrong and must not be carried over. An entry is used only if its index is a channel that the device list reported as occupied.
- **One read per endpoint.** The worker sends one `08 01` and never the continuation read the notes mention. A device list whose records run past the end of the one reply before the last reported channel makes the hub unsupported ("the hub has more parts than DeadWare can read"). The owner's list uses 215 of 512 bytes.
- **Draining.** Before each command the worker reads and discards reports already waiting (other programs' replies arrive on every open handle), at most 16 and without waiting.
- **Replies are matched** by the echoed opcode, and endpoint reads also by data type; anything else inside the reply limit is skipped, at most 16.
- **The shared lock.** `Global\CorsairLinkReadWriteGuardMutex`, opened, or created so that other programs of any user can also take it; held from a transaction's first frame to its last, and around a mode change; an abandoned lock counts as acquired; released on the same thread. If it can be neither opened nor created the worker does not talk to the hub and reports why. OpenRGB 1.0rc3 takes the same lock around each of its own sequences (the earlier research, from its source; confirmed in practice by the readings of 2026-11-15 and 2026-11-25, which ran beside it).
- **Firmware.** Read and logged at every take. No firmware is refused by number; the parse rules decide.

**Parts.** A device-list entry becomes a part by its type code and model from the notes' table: its name (zone name) is the one OpenRGB gives the same part ("iCUE LINK RX RGB", "iCUE LINK TITAN 360"; the plan establishes the names for the other table rows from OpenRGB 1.0rc3's source, facts only, or leaves those types unsupported), its LED count, whether it has a coolant sensor. Parts are ordered by channel; LED-bearing parts are the hub device's zones 0, 1, 2, …, so the owner's hub lists zones 0 to 2 as the fans and 3 as the pump, exactly as OpenRGB does. A type or model not in the table, a table row whose LED count is unknown, or a device list that does not parse, makes the hub **unsupported**: no colours are ever written to it.

**The allow-list.** The worker can only send what one builder type produces, and that type can produce only: firmware read, enter software mode, enter hardware mode, close endpoint, open endpoint for `0x36`, `0x21` and `0x17`, open colour endpoint `0x22`, read, colour write and its continuation chunk. There is no general "write" or "open endpoint (byte)" call. A test enumerates everything the type can build and fails if any frame opens or writes endpoint `0x18`, uses opcode `06 01`, or is not on the list.

### Taking the hub

A normal take, all on the worker:

1. List hubs. None: `letGo` "no iCUE LINK hub was found", tried again when the host's 5 s slow work sees one. More than one: `letGo` "this PC has more than one iCUE LINK hub".
2. Open, read firmware.
3. Open the temperature endpoint once and note the reply: status `03` means the hub was in its own mode, `00` that it was still in software mode. Close it. Log which ("hub was in its own mode" / "hub was still held"). This is the trace the silence test reads.
4. Enter software mode.
5. Read the device list and build the parts. Unsupported: enter hardware mode, close, `letGo` with the reason, and no automatic retry until the hub's device list could have changed (next helper start, wake, or the hub re-appearing).
6. Read temperatures and speeds once.
7. Report `driving` with the parts. The host's next rescan lists the hub device, and the first frame follows on the next tick. Until that frame arrives the worker sends nothing but readings; it never invents colours.

### The dialog's reading

The request `readHub` is allowed only in mode `openrgb` and only when no blocker is present. It replies at once (accepted, or refused with the blockers); the reading itself runs on the worker and its result appears in the state. The worker opens the hub, reads firmware, the device list, temperatures and speeds as ordinary read transactions under the lock, and closes. It sends no mode command. If the first endpoint open answers `03` (nobody holds the hub), or anything fails, or 2 s pass, the result is "could not read". The result carries the readings, whether the hub is supported (and why not), and the time it was taken.

The window sends it when the switch is clicked, waits for the result (at most 3 s, then treats it as "could not read"), and shows the dialog. An unsupported hub is shown as the blocked red line instead of the dialog. The request is never sent on a timer.

### The take-over (the switch being turned on)

The request `setHubMode(driven)` replies at once: accepted, or refused with the red line's reasons. It is refused when a blocker is present (the scan is run fresh for this request), when the settings cannot be saved (the store's existing "no saves" condition), when another mode change is still running, and when the last reading is more than two minutes old ("stale reading"). A "could not read" result is accepted and means there is no before reading. After accepting, the helper reports `switching` and runs these steps on its loop, never holding the pipe:

1. **Before reading** = the dialog's reading, as the owner saw it.
2. Stop OpenRGB with a deliberate stop (see "OpenRGB's restart"), drop the session and forget everything remembered about every device, as after any OpenRGB restart.
3. The in-between mode is now "taking over": the next OpenRGB launch has the hub detector off. The worker takes the hub (the normal take). If the take fails or the hub is unsupported: the worker has already let go; the mode goes back to `openrgb`; OpenRGB is started again (detector on); the red line reports why; state `off`.
4. Start OpenRGB again. The helper connects as after any restart, lists the hub from the worker and paints it.
5. **The check**, at the first readings taken 15 s or more after software mode was entered:
   - With a before reading: if the pump's or any fan's speed is below 90% of its before value (2,716 RPM before: 2,444 fails, 2,445 passes), or its reading is unavailable, the check fails: the worker lets go (hardware mode, close), mode `stopped` is saved with the part, the numbers and the kind ("slower" / "no reading"). OpenRGB keeps running without the hub.
   - Otherwise the check passes: mode `driven` is saved; the before reading and the time stay in memory for the state line.
   - Without a before reading there is no comparison; `driven` is saved at the same 15 s mark unless the stopped-pump rule has fired.
6. **Interrupted before step 5 completes** (a Stop request, a blocking or hub-only conflict, suspend, the hub vanishing or going silent): the worker lets go, nothing is saved, the in-between mode is dropped (so `openrgb`), and unless the helper is stopping it brings OpenRGB back with the detector on through the same deliberate restart. Red line "The take-over did not finish (<why>). …". A Stop always wins and is never delayed by more than the hand-back's limit.

`setHubMode(openrgb)` (switching off, or **Back to OpenRGB, as before**): refused while a mode change is running or when the settings cannot be saved. Otherwise: `switching`; the worker lets go if it holds the hub; mode `openrgb` is saved; OpenRGB is restarted deliberately with the detector on; state `off` once the restart has been started.

**Try the take-over again** is the window's doing: it sends `setHubMode(openrgb)`, waits for state `off` and for the hub to be listed through OpenRGB (at most 60 s), then runs the ordinary switch-on flow from `readHub`.

### OpenRGB's restart

A mode change needs OpenRGB stopped and started once. Neither of the engine's two existing stops fits: the ordinary stop (`StopAsync()`, `OpenRgbEngine.cs:282`) forgets the 4 s floor, so the next start would launch at once; the conflict stop (`keepHistory: true`) keeps the floor but counts the stop towards the five-in-ten-minutes give-up. The engine gains a **deliberate stop**: it arms the 4 s floor for the next start, as a conflict stop does, and counts nothing towards the give-up. The host, which today does not drop its session on a plain stop (`DeviceHost.cs:619-645`), drops it explicitly here.

The same deliberate restart is used once when the worker finds "OpenRGB still has the hub" (see "OpenRGB's detector").

### The stopped-pump rule

While `driving`, four consecutive readings (9 s) in which a pump part's speed is 0 RPM or unavailable make the worker let go; mode `stopped` is saved with "the pump read 0 RPM". Fans are not part of this rule. A hub with no pump part has no such rule. If the save fails, the helper logs it and stays in `stopped` for this run; the design accepts that a restart then returns to `driven`.

### Letting go

Letting go keeps the saved mode. The worker sends enter hardware mode once, under the lock and inside the transaction limit, then closes the handle; if that send fails it closes anyway and logs it. Reasons, and the two phrases the texts use:

| Reason (after "because") | Settings: "DeadWare takes it again …" | Lighting page: "until …" |
|---|---|---|
| iCUE is running | once iCUE has quit | iCUE has quit |
| the Corsair service is running | once the service has stopped | the service has stopped |
| DeadLight is running | once DeadLight has quit | DeadLight has quit |
| another OpenRGB is running | once it has quit | it has quit |
| lighting has stopped | when lighting runs again | lighting runs again |
| no iCUE LINK hub was found | when it is plugged in again | it is plugged in again |
| the hub stopped answering | when DeadWare tries again in a moment | DeadWare tries again in a moment |
| the hub keeps falling back to its own mode | when DeadWare tries again in a moment | DeadWare tries again in a moment |
| the hub has a part DeadWare doesn't know (…) | when DeadWare is restarted | DeadWare is restarted |
| the hub has more parts than DeadWare can read | when DeadWare is restarted | DeadWare is restarted |
| OpenRGB still has the hub | once OpenRGB has restarted without it | OpenRGB has restarted without it |

While the PC is waking the state is `letGo` with the reason "waking", which shows no hub line: the page's waking line covers it.

### Dark, sleep and wake

- **Lights off, idle, lock:** the host sets dark; the worker sends black, re-sent every 2 s like any frame. The hub stays held and readings continue. Dark is set from the everyday work that already runs while OpenRGB is away, so a shortcut pressed while OpenRGB restarts still darkens the hub.
- **Suspend:** the worker goes quiet at once. It sends nothing, not even hardware mode: the handler must return quickly, and the hub loses power or falls back by itself.
- **Wake:** nothing is sent during step 4's 10 s wait. At its end the host tells the worker to resume, which is a normal take from step 1 (the device list is read again; the hub's parts may have changed), then the repaint. During the wait the hub shows its own effect.
- **OpenRGB restarting or reconnecting:** the worker keeps the last frame and the readings going. No new frames arrive until the loop paints again.

### Quit, stop and update

With the hub held, it is handed back in `DeviceHost.RunAsync`'s `finally`, **before** `engine.StopAsync()` (`DeviceHost.cs:353-354`): the worker is told to let go and the host waits for it, at most 1.5 s. This one place covers Quit, Update & restart, Roll back, lighting switched off, an OpenRGB path change and the elevation restart, because all are a `StopRequest`, and it must come first because the window ends the helper 5 s after sending Stop and OpenRGB's own exit wait can take 5 s. The conflict stop (`CheckConflictsAsync`'s stop branch) forbids holding before it stops OpenRGB.

Also at that point, after the hand-back and in every mode: the helper sets OpenRGB's hub detector **on** in `OpenRGB.json` (see "OpenRGB's detector"), so a start by 0.22 finds the hub.

The Windows session ending (sign-out, shut down) does not stop the helper today (`App.xaml.cs:197-207`). Claude's call: step 5 makes the window send the helper its ordinary Stop there too, without waiting more than 2 s, so a sign-out hands the hub back; on a shut down it makes no difference.

Nothing runs when the helper is killed or hangs. Then the hub's own fallback after about 10 s of silence applies, which the hardware check's silence test confirms or refutes for firmware 4.1.656. If it does not fall back, the hub keeps its last colours and whatever its speeds do until the helper, restarted by the window after 4 s, takes it again; the design accepts that.

In mode `openrgb` none of this section applies except the detector line: Quit ends OpenRGB as 0.22 does and DeadWare does not touch the hub.

### OpenRGB's detector

`OpenRgbConfig.Prepare` (run before every OpenRGB launch) sets `Detectors.detectors["Corsair iCUE Link System Hub"]` to `false` when the mode is `driven`, `stopped` or the take-over's in-between mode, and to `true` when it is `openrgb`, beside the Aerox line it already writes. The name is OpenRGB 1.0rc3's (present, and `true`, in the owner's `C:\ProgramData\DeadWare\openrgb\OpenRGB.json` on 2026-11-25).

Because `Prepare` keeps every other key, a `false` would outlive a roll-back to 0.22, which never writes that key, and the hub would stay unlit. So the helper writes it back to `true` on every controlled stop (above). A helper that was killed leaves `false` behind; the next 0.23 start corrects it either way, and the release note says that after going back to 0.22 with an unlit hub the owner should start and quit 0.23 once, and recommends switching the hub off before going back.

Two programs must never hold the hub: OpenRGB is always stopped before the worker takes the hub and the worker always lets go before OpenRGB is started with the detector on. As a guard against a stale detector setting, the worker never holds the hub while the helper's own OpenRGB lists a device named "Corsair iCUE Link System Hub" in mode `driven`: it lets go with "OpenRGB still has the hub" and the helper restarts OpenRGB once, deliberately; if OpenRGB lists it again after that, the hub stays let go for this helper run.

### One identity

As for the mouse, the hub's saved colours, tuning and place live under one fixed device key, `linkhub`, whichever route lights it:

- OpenRGB's device named exactly "Corsair iCUE Link System Hub" is presented under `linkhub` instead of its serial-and-location key, when exactly one is listed. With two or more, none is re-keyed and the switch is refused.
- The worker's hub is presented under `linkhub`, with the same device name, type, zone names and zone order as OpenRGB's, and the single mode Direct. The desk picture's signature and parts (`DeskPicture.cs:17-26`) therefore match on both routes without change.
- **The one copy (never a move).** When `linkhub` has no zone colours, they are copied from the old entry: the listed OpenRGB hub's old key if it is listed, otherwise any other key whose saved zone names are only "iCUE LINK …" names. Tuning is copied the same way, separately. The old entries stay, so 0.22 still finds its colours. The copy runs where the mouse's does, before the layout is arranged. Layout is not copied: picture mode rebuilds it, plain mode arranges the hub afresh.
- The hub cannot be resized or given a hardware mode on the worker's route; such requests are refused in plain words.
- While the hub is on its own (`letGo`, `gaveBack`), it stays in the device list as absent, like a disconnected mouse: never painted, drawn as "own effect", settings still editable.

### The lighting loop

`LightingDeviceService` today treats "a device without an OpenRGB index" as the mouse. The builder generalises that seam to a second own device: the hub is listed from the worker's snapshot, its frame is computed by the same paint code as every other device (static colour or effect, calibration, brightness; one colour per zone, since each zone is drawn as one light) and posted to the worker instead of being pushed to OpenRGB, and the 33 ms cap and 400 ms refresh that OpenRGB devices get do not apply to it (the worker has its own). Every place that special-cases the mouse (`Shape`, `Unlit`, the views, the preview, the status-line count) must be checked for the hub; the survey lists them (`survey-deadware.md` §1, §13 in step 5's records).

Nothing on the loop waits for the hub. The only wait is the 1.5 s hand-back on stop.

### The pipe

`Version` stays 1. The pipe serves one request at a time and the window gives up on a reply after 5 s (`HostPipeServer.cs:116-139`, `DeviceHostClient.cs:36`), so no hub request waits for the hub. Additions:

- `HostState` gains one optional trailing member `Hub`: the state, the reason code and its text parameters, the before readings and the take-over time when present, the failed check's part, kind and numbers when `gaveBack`, the last dialog reading's result with its time, and the last refusal or interruption for the red line. It changes rarely, so it travels in the state.
- The hub's `DeviceView` gains what the window needs to tell the routes apart (the builder's shape; an optional trailing member or a value of an existing one).
- A new event `hubReadings` (the merge design's `HubReadings`): the readings and their age, sent when a reading round completes while a window is connected. **Not** in the state: a state carries the whole settings, up to 1 MiB, and readings arrive every 3 s. A new message type is safe only because window and helper ship from the same build, as step 4 noted.
- Requests `readHub` and `setHubMode(openrgb | driven)`, both replying at once; refusals are plain words through `HostReplyCodes`.

### The settings file

`lighting.json` gains an optional `Hub` member: the mode, and for `stopped` the reason with its numbers. `CurrentVersion` becomes 5. Written version: 5 only while the mode is not `openrgb`; otherwise the existing 4 / 3 / 2 / 1 rule; a default `Hub` is not written. So a PC that never turns the switch on keeps a file 0.22 can save. Existing tests that use version 5 as "a newer file" move to 6 (the same four files step 4 listed). The `linkhub` key needs no version bump.

With the switch on, a roll-back to 0.22 reads the version 5 file, runs the lights and will not save lighting changes over it, as 0.21 did with version 4. The release note says so and recommends switching the hub off before going back.

`settings.json` (the window's file) gains only `ShowCoolantTemperature` for the tick box.

### Settings → Lighting: what the window reads

All read-only, read by the window (not the helper), only while the Lighting section is on screen, on the section's existing refresh:

- **iCUE program:** the conflict scan's result (as today).
- **Corsair services:** exactly the three services whose programs the scan blocks on: `CorsairService` (`Corsair.Service.exe`), `CorsairDeviceControlService` and `CorsairDeviceListerService`. For each one installed: display name, whether its process is running (the same test the helper uses), start type. Red when running; yellow when not running but set to start with Windows (Automatic, delayed or not). Other Corsair services, `CorsairCpuIdService` included, are not shown and do not block.
- **iCUE at sign-in:** a value named `Corsair iCUE5 Software` (or any value whose command runs `iCUE Launcher.exe` or `iCUE.exe`) under the Run keys of HKLM and HKCU, 32- and 64-bit views; and whether Windows has it disabled (the StartupApproved keys Task Manager writes). Disabled counts as "does not start".
- **DeadLight:** the Task Scheduler task `\DeadLight` (exists, enabled, its action and working directory) and DeadLight's running processes, by the folder rule below.

A fact that cannot be read shows as a yellow line "DeadWare could not check <what>: <reason>." and never as green.

### Retire DeadLight

**DeadLight's folder** is confirmed, not guessed: a folder is DeadLight's only if it holds a `package.json` whose `name` is `deadlight`. Candidates are found by walking up from every path in the task's action, arguments and working directory, and from every running `electron.exe` whose path ends in `\node_modules\electron\dist\electron.exe` (three levels up). The first candidate that confirms is the folder; a drive root or any folder without that file never is.

**DeadLight's processes** are then exactly: `electron.exe` at `<folder>\node_modules\electron\dist\electron.exe`, and `OpenRGB.exe` at `<folder>\resources\openrgb\OpenRGB.exe`. "DeadLight is running" in Settings uses the helper's existing conflict rule (unchanged), so the line and the block agree.

The action, run by the elevated window after the dialog: disable the task (not delete); end the task's running instance; if the folder is confirmed, end DeadLight's processes by process id, with the executable path checked again immediately before each, `electron.exe` first and then its `OpenRGB.exe`; wait up to 5 s for them to exit; read the facts again and show the result. With no confirmed folder it ends nothing and says so. It never ends a process by name alone, deletes nothing, and does not touch `%APPDATA%\deadlight`. The builder uses the Task Scheduler interface or `schtasks.exe` from System32 by full path. Nothing is sent to the hub by Retire: once DeadLight is gone the helper's conflict scan clears and ordinary lighting, with whatever hub mode is saved, takes over.

### Monitoring

- **A second sensor source.** The window's session passes the last `hubReadings` to the collector, which adds them to each poll's sensors as it already adds storage health and Windows thermal zones. A reading older than 10 s is not added. Sensor ids are stable and their own (`/corsairlink/…`, builder's format).
- **They must not be picked as the CPU pump.** `CpuPumpSpeed.Select` (`Collector.cs:57-64`) takes the first RPM sensor of hardware type SuperIO, Cooler or EmbeddedController whose name contains "Pump". The hub's rows carry a hardware type outside those three, and a test pins that with the hub's rows present `CpuPumpSpeed.Select` and `ThermalProjection`'s device identity return exactly what they return without them.
- **The coolant value** for graphs is the first coolant reading, under a new graph key (`cpu/coolant`), with a new entry in `GraphHistoryProjection.Mappings` (the 64-key cap is shared; one key is added). No database schema changes. The chart option, the legend, the dot, the line-style entry (dashed, teal) and the places that read the pump option (`HistoryChartView`, `OverviewPage`, `PopoutWindow`, `WidgetWindow`, `LineStyleEditor`) follow the pump line's pattern; the coolant line uses the existing °C scale. The legend's "no reading" wording needs the hub state, which the session already has.
- **Cooling Health wording.** `ThermalAnalysis.cs:242-243` ends two messages with "coolant and ambient temperatures are not measured." Both become "coolant and ambient temperatures are not part of this comparison." (the test at `ThermalAnalysisTests.cs:395` follows). `CoolingPage.cs:199` ("Pump, coolant and flow diagnosis requires confirmed sensors.") stays. Its analysis is untouched.

### Conflicts

- The existing scan and its texts stay. Matching stays by process name, with path only where step 1 reads it. **Claude's call, against the merge design's "by path, never by name alone":** for a cooling device, blocking too often is the safe direction, and name matching "can over-block but never misses one".
- **Hub-only conflict.** The legacy `Corsair.Service` (`ConflictKind.LegacyCorsairService`) keeps not blocking lighting, but blocks the take-over and makes the worker let go, as "the Corsair service is running". The merge design already treats it so (line 176). The two Corsair device services already block all lighting; for the hub they carry the same words.
- The scan that guards the hub is the helper's existing 5 s scan; a take, and both hub requests, run a fresh scan first ("launchers that run briefly at sign-in are rechecked before the hub driver takes the hub").

### Logging (also the hardware check's evidence)

One line each, in the helper's log, never per frame or per reading: a mode change requested, refused (why), settled (from, to); the dialog's reading (ok with values, or why not); OpenRGB stopped and started for a mode change; take started; firmware; "hub was in its own mode" / "hub was still held"; software mode entered; parts (channel, type, model, LEDs); unsupported and why; first frame sent; the check (each part's before and after, pass or fail); the take-over interrupted (why); stopped pump; a "wrong mode" re-entry (with the count); let go and the reason; hardware mode sent, or failed to send; taken again; the lock busy (once per spell, and once when it clears); a call stuck and the handle closed; the helper ending itself; hub appeared / disappeared; the detector written on or off. Readings are logged only when a part's availability changes. Device ids are not logged.

### Third-party notice

`THIRD-PARTY-NOTICES.md` gains the notes' attribution paragraph (identical to the research's §4) under Lighting, and says that `DeadWare.Devices.exe` uses HidSharp directly. No code from OpenLinkHub, OpenRGB, liquidctl or FanControl.CorsairLink is copied, and none of their tests.

### Where the code goes

`DeviceHost.cs` only wires. New work goes in new classes: the transport and its HidSharp adapter; the frame builder with the allow-list; the parser and the parts table; the worker; the take-over's steps as a small state machine of its own that the host drives; the check rules (pure, in Core); the hub identity rule (Core, beside the mouse's); `HubTexts` (Core, every string in "What the owner sees", with tests); the window's Windows-fact readers and the retire action (window project, behind interfaces so their rules are tested without Windows); the coolant graph members beside the pump's.

`KeyPressRuleTests` scans every file in `DeadWare.Devices` and `DeadWare.Core`, comments included. No new file may contain a forbidden name even inside another word (a helper called `IdToAscii` fails). `NativeLibrary`, `GetProcAddress` and function pointers stay forbidden; a new `DllImport` is allowed where a managed API will not do.

## To verify first in the plan

1. **HidSharp and the hub's handle.** What sharing mode `Open()` / `TryOpen` uses on Windows (the two readings opened the hub beside OpenRGB with `TryOpen`, so it is shared in practice; the plan confirms it from HidSharp 2.6.4's source or documentation), how the read and write limits behave on a 513-byte report, and whether closing the stream from another thread cancels a call in progress. If it does not, step 2 of "A hub call that never returns" is dropped and step 3 follows the 1.5 s limit directly.
2. **The shared lock from .NET.** How to open-or-create `Global\CorsairLinkReadWriteGuardMutex` with rights for every user from an elevated and from a non-elevated helper (`MutexAcl` if the framework has it, otherwise a pinned `DllImport`), how an abandoned lock surfaces, and, from OpenRGB 1.0rc3's source (facts only), that its iCUE LINK controller takes it as the research says.
3. **The zone names and LED counts OpenRGB 1.0rc3 gives each device type** in the notes' table, from OpenRGB's source (facts only), so that parts built by the worker match what OpenRGB listed. Types that cannot be established are unsupported.
4. **Fixtures** are built from the 2026-11-25 capture with synthetic ids; the plan states each fixture's literal bytes and the values the parser must produce (33.7 and 43.7 °C; 1,660, 1,654, 1,663 and 2,774 RPM; channels 1, 2, 3 and 13).
5. **Windows facts.** Where Task Manager records a disabled start-up entry, how a service's start type and executable are read without administrator rights, and how the `\DeadLight` task is read and disabled.
6. **The engine's deliberate stop** against `OpenRgbEngine`'s floor and give-up code, and the host's session handling around it.

## Testing

- **Core:** the check rules with the literal values above (2,444 fails and 2,445 passes against 2,716; unavailable fails; four zero readings stop, three do not; fans do not stop it); the hub identity and the copy (never a move; nothing copied twice; two hubs not re-keyed); the settings codec for `Hub` with the written-version rule (5 only while the mode is not `openrgb`; a 0.22-shaped file round-trips); `HubTexts` for every state, reason and red line; the status-line count with the hub driven, let go and off; the coolant graph key, option and legend.
- **Devices:** the frame builder (every frame byte-exact against the notes; the allow-list enumeration); the parser against the fixtures (52 and 26 entries; empty channels; an unknown type; a list that runs past the reply; a short reply; a wrong data type); the worker against a fake transport and fake time (take sequence and its log lines; the 125 ms cap; the 2 s keep-alive; readings every 3 s; dark; quiet on suspend and take on resume; a busy lock skips; no reply lets go and retries at 30 s then 60 s; "wrong mode" re-enters three times and lets go on the fourth; hand-back within the limit; a stuck call: handle closed at 1.5 s, the helper's exit asked for 3 s later); the take-over state machine with a fake worker and a fake engine (OpenRGB stopped before the take; the detector off for the restart; a pass saves `driven` only at the 15 s mark; a failure saves `stopped`; an interruption at each step saves nothing and ends in `openrgb`; a Stop is never held up; a stale reading is refused; unsaveable settings are refused); the host (hold conditions, the conflict letting go, hardware mode before the engine stops, the detector written `true` on stop); the engine's deliberate stop (the next start waits 4 s; the give-up count is unchanged); `OpenRgbConfigTests` for the hub key; `KeyPressRuleTests` unchanged and green.
- **Window (pure classes):** the iCUE and DeadLight line rules from fake facts, including "could not check"; the folder rule (a drive root, a folder without `package.json`, a wrong name: not DeadLight's); the retire action against a fake task and fake processes (only DeadLight's two paths, electron first, nothing ended without a confirmed folder, nothing when not elevated); the collector with hub readings (rows present, stale ones absent, the pump selection unchanged).
- Tests state literal expected values, never a constant shared with the code under test, and use fake time, never a real-time deadline.
- The build and the full suite pass before every commit; the measurement tests keep to their own job.

## Hardware check (owner's PC, after the build, each item approved by the owner one at a time)

Before it: the owner stops the Corsair service and sets it to Manual, and disables iCUE's start-up entry, following Settings → Lighting in the test build. The scripts follow step 4's pattern (launch with backups of `settings.json` and `lighting.json` and of `OpenRGB.json`, snapshot of the log, restore). Report per item what the log confirms and what it does not.

**The hub (first sitting).**

1. Settings shows the iCUE and DeadLight lines truthfully, before and after the owner's changes. The owner reads them out; nothing is logged for this.
2. **The take-over** (approval (b)). Switch on, read the dialog's numbers, take over. Owner looks at: all lights blink once, the hub lit in its colours. Log: the reading, OpenRGB stopped, firmware, "hub was still held" or "in its own mode", parts, the check's numbers and "pass", mode `driven` settled. If the check fails, stop here.
3. Readings: the hub's card, the five Sensors rows, the coolant line after ticking the box. Log: parts listed; no reading lines unless availability changed.
4. Lights off and on with the shortcut: the hub goes dark and comes back with no blink elsewhere. Log: no mode change, no let go.
5. Quit DeadWare. Owner looks at: the hub changes to its own effect. Log: "hardware mode sent", the detector written on. Start the test build again: log shows a take with "hub was in its own mode".
6. **The silence test** (approval (c)), once: an owner-run script ends the test build and its helper, the owner watches the hub for 20 s, then the build is started again. Owner's eye: whether the lights changed to the hub's own effect, and after how long. Log after restart: "hub was in its own mode" confirms the fallback; "hub was still held" refutes it. This is one of the session's two kill-and-relaunch tests.
7. Sleep the PC for a minute and wake it: the hub comes back by itself. Log: quiet, resume, take, wake repaint done.
8. Switch the hub off again: the hub's lights return through OpenRGB with the same colours. Log: hardware mode sent, mode `openrgb`, detector on.

**Step 4's seven items (second sitting, or the same one if the owner prefers):** the appendix below, on this test build.

Not tried on hardware unless the owner adds it: iCUE starting while DeadWare drives the hub, a failed speed check, the stopped-pump rule, a stuck USB call, Retire DeadLight with DeadLight actually running, and a hub with unknown parts. Tests cover them; the guide and the release note say so.

Approvals asked separately, each when its turn comes: (a) the read-only reading: given and done (O5); **(b)** the take-over; **(c)** the silence test.

## What fan control gets from this step

For the fan-control design (begun 2026-11-25 in its own session): one owner of the hub in the helper, on its own thread, with the shared lock, the transactions, the device list, the readings, the mode, the conflict rules, the hand-back on every controlled stop and a saved mode the owner controls. What it must add itself: the speed endpoint in the allow-list and its commands, a take that does not wait for the lighting loop, behaviour when lighting is disabled, and what a crash of the helper means once speeds are set.

This step sets no speeds. While DeadWare holds the hub, the fans and pump do whatever the hub does with no speed command, exactly as under OpenRGB today. That is not one fixed speed (see "Facts from the 2026-11-25 reading"): once this step is in use, the Sensors history of fan speed against coolant temperature will show whether the hub follows its own curve or holds the speed it was taken at.

## Out of scope and later

- **Reminder owed to the owner (O3):** when step 5 is finished, raise the Lighting Node Pro driver for the front three fans, and ask whether it comes before or after fan control.
- Fan control (its own design).
- The open points from step 4 (its hand-over, "Open from step 4").
- Path matching for conflicts; reading the hub while OpenRGB lights it; a third "leave alone" position; per-LED effects on the hub; hubs with more parts than one reply holds.

## Left to the builder

- Names of new types and files, within "Where the code goes".
- The exact shape of the second-own-device seam in `LightingDeviceService`, and of the hub's marker on `DeviceView`.
- The shape of the `Hub` state member, the `hubReadings` event and the two requests.
- The shape of the engine's deliberate stop.
- The sensor id format and the hardware type of the hub's rows, within "They must not be picked as the CPU pump".
- How the window reads the Windows facts, within "To verify first in the plan" item 5.
- Layout details in WinUI, following the mockup and the pages' existing styles.
- The wording of plain-word refusals not listed here, in the style of `HostReplyCodes`.

## Deviations from the merge design (Claude's calls unless marked)

- The take-over is gated by a saved switch and a dialog (owner, O1), not taken whenever lighting runs. With the switch off, Quit does not hand the hub back (owner, O1: nothing changes).
- Unchanged colours are re-sent every 2 s, not 5 s.
- The hub's status travels in the state; only readings are an event.
- Wake does not ask OpenRGB for a hardware rescan (step 4's ruling); the hub is taken again after the 10 s wait.
- Conflicts stay matched by name.
- Settings' DeadLight lines appear only when DeadLight is found; the iCUE lines collapse to one when iCUE is absent.
- No continuation read; a hub whose device list needs one is unsupported.

## Amendments after plan research (2026-11-25, Claude's calls; where they differ from the text above, these win)

Sources, all in step 5's records folder (`.superpowers/sdd/2026-11-25-lighting-step5/`): `plan-map.md` (the code as it is, with lines), `research-platform.md` (HidSharp, the shared lock, Windows facts), `research-openrgb-hub.md` (OpenRGB 1.0rc3 at tag `release_candidate_1.0rc3`).

1. **The worker is a core and a shell.** The hub's logic is a single-threaded core that is stepped with the current time and answers when it next wants to run. The dedicated thread is a thin shell around it. Every worker test steps the core with fake time on the test's own thread; no test depends on a real thread or on real time.
2. **The watchdog is the shell's own real-time timer, not the host's loop.** The loop waits inline for engine stops (up to 5 s) and skips its work while an OpenRGB start is pending (up to 30 s). The timer's period is 250 ms; it is never 500 ms or 33 ms and never made from the host's clock, because the host's test harness recognises its own ticks by those periods. At 1.5 s it closes the transport: HidSharp returns a blocked call at once when its stream is disposed from another thread. 3 s later it calls an injected "end the helper" action; the helper's exit code for it is 5.
3. **The deliberate stop.** Today every launched OpenRGB that ends is counted towards the give-up, whichever stop ended it (`OpenRgbEngine.cs:171-175`). The new stop sets a flag that `Supervise` reads there: no failure is recorded, and the next start is armed for no sooner than 4 s after the exit, without shortening a later time already armed. The host then drops its session the way `HardwareRescanLocked` does (`DeviceHost.cs:538`) and starts OpenRGB itself, off the lock, the way `CheckConflictsAsync` does.
4. **Hub requests never scan.** Requests run under the host's lock, so `readHub` and `setHubMode` judge blockers from the last scan (at most 5 s old). The take-over's first step, on the loop and outside the lock, is a fresh scan; a blocker found there ends it as "did not finish".
5. **The readings event gets its own send slot.** `HostPipeServer` keeps two waiting broadcasts; a third, latest-wins slot carries `hubReadings`, so it never replaces a waiting state or preview.
6. **Settings.** `DeviceHost.Clone` copies members by hand and the codec's newer-file handling names them one by one: `Hub` is added to both, and is reset with `Everyday` and `Mouse`. The mode is written as text (`"openrgb"`, `"driven"`, `"stopped"`), never as a number. A change "succeeds" in memory while saves are off, so the hub requests test the save condition themselves. Nineteen tests use version 5 as "newer" and move to 6.
7. **`OpenRGB.json`.** The hub key is written on every launch, `true` or `false`, so the two tests that compare the whole file to a minimal object gain that key. With no config folder (`HostOptions.ConfigDir` empty) nothing is written.
8. **Two owner-rig tests pin the hub's old key** (`DeviceHostTests.cs:1469,1516-1555`); they move to `linkhub`.
9. **Where the window's logic lives.** There is no WinUI test project. The line rules, the folder rule, the retire steps and every text are pure classes in `DeadWare.Core`; the Windows readers sit behind interfaces in `DeadWare.Services`; both are tested there. The WinUI files get a hand trace with file and line, as in steps 3b and 4.
10. **The hub on its own is drawn the way the page already draws an own-effect part** (`DeskPictureView`), with the "own effect" caption. The mockup's grey outlines imitated that drawing; no new drawing style is added.
11. **The coolant line touches more places than the pump list above**: `GraphColorSettings.Kinds` and the others in `plan-map.md` §12, or its saved colour and style are dropped. `docs/user-guide.md:225` also says coolant is not measured and is rewritten with line 231.
12. **Platform facts the build relies on:** HidSharp's default open is shared (exclusive is off) and its default limits are 3 s, so the adapter sets 250 ms; the lock is made with `MutexAcl.Create` and a rule for Everyone, falling back to `Mutex.OpenExisting`, and never with .NET 10's named-handle options (their default is current user only); the thread that catches `AbandonedMutexException` owns the lock; a disabled start-up entry is an odd first byte in the 12-byte `StartupApproved` value; a service's start type, delayed flag and program come from its registry key, with no extra package; the `\DeadLight` task is read through `schtasks /Query /XML` or the Task Scheduler interface; a process is ended through one handle on which its path was checked first.
13. **OpenRGB facts the build relies on:** zone names and LED counts come from OpenRGB's device table at that tag; type `0x1A` has no row there and stays unsupported; a part with no LEDs gets no zone but keeps its speed row; OpenRGB hands the hub back on a clean exit and on every hardware rescan, so with the switch off step 4's cable rescan still blinks the hub as it does today.

## Amendments after the plan check (2026-11-25, Claude's calls; where they differ from the text above, these win)

Sources, in step 5's records folder: `plan-check-A.md` (the plan's Tasks 1 to 8) and `plan-check-B.md` (Tasks 9 to 15), with the rulings made on them. The numbering continues from the section above. The plan's "Rulings from the plan check" (P1 to P30) carries the same decisions for the build.

14. **A hub on its own stays in the device list, also after a restart.** While the hub mode is not `openrgb`, the settings' `Hub` member also holds the hub's last known parts: the channel, type and model of each occupied channel. They are never written in mode `openrgb`, so a file whose switch was never turned on, or was turned off again, is still written byte for byte as 0.22 writes it. They are validated on read: none in mode `openrgb`, at most 64, channels 1 to 255 ascending and distinct, type and model 0 to 255. The device list shows the hub from them whenever the worker has no parts of its own in this helper run, so the last point of "One identity" also holds straight after a restart in mode `stopped`, and in mode `driven` while a conflict keeps the worker off the hub. Saved parts of which one is no longer a known part are not used. The parts are saved with the mode at the take-over's verdict, and again when a later take in mode `driven` reads different ones.
15. **The allow-list is closed at the port too.** A frame from the builder type is the only thing the transport's port can send: its write takes a frame, not bytes. The builder's close and open calls refuse every endpoint except `0x36`, `0x21` and `0x17`. A frame's bytes cannot be changed after it is built.
16. **The shared lock.** Every exchange with the hub happens while the lock is held, and the lock is released on every way out of a transaction, an unexpected error included. Any error inside a transaction counts as a failure: the handle is closed, the state is `letGo` with "the hub stopped answering", and the retry delay applies. The take, from the firmware read to the first speed reading, is one transaction under the lock, and so is the dialog's reading; a busy lock at the start of either skips it, as it skips any round.
17. **Retries.** Looking again for the hub, and allowing holding again, never shorten a retry delay. Only the wake's end, a take-over the owner starts and a successful take end it. A take-over the owner starts therefore tries the hub at once, whatever delay an earlier failure left.
18. **Replies.** Parsing is bounded by the length of the reply actually received, and a part whose channel lies beyond the entries a reply holds reads as unavailable. Speeds are read as unsigned numbers.
19. **OpenRGB's first launch honours the saved mode.** The hub detector is set from the saved mode before the first OpenRGB launch of a helper run, so in mode `driven` or `stopped` OpenRGB is never launched with the hub detector on. The helper records what each launch wrote. The worker never holds the hub beside an OpenRGB that was launched with the detector on, whatever that OpenRGB has listed so far: the reason is "OpenRGB still has the hub", and the one deliberate restart follows.
20. **The deliberate stop says whether OpenRGB ended.** If OpenRGB has not exited when the engine's wait of 5 s runs out, the worker is not allowed to take the hub and the take-over ends as "did not finish" (lighting has stopped).
21. **Switching off and backing out wait for the worker.** Switching off, and backing out of an interrupted take-over, do not set the detector on, save `openrgb` or start OpenRGB until the worker has let go. There is no time limit after which they go ahead; after 1.5 s the helper only logs that the hand-back is taking long. The watchdog bounds the wait: it closes the handle 1.5 s into a transaction, which makes the worker let go, and ends the helper 3 s after that. For these restarts an OpenRGB that has been launched but whose port is not open yet counts as running and is stopped first. A back-out's restart is not started while the PC is suspended or inside the 10 s wake wait; the wake's end starts it.
22. **A failed save keeps the mode in memory.** When saving a settled mode fails (`driven` after a passed check, `stopped` after a failed check or a stopped pump), the helper keeps that mode for this run and logs the failure once; a restart then returns to the mode on disk. The stopped-pump rule acts once per stop, not on every turn of the loop.
23. **The check.** It has an upper limit: with no reading round completed 60 s after software mode was entered (the shared lock busy throughout, say), the take-over ends as "did not finish" with "the hub stopped answering". A pump that stops during the check is the check's verdict, not an interruption: mode `stopped` is saved with "the pump read 0 RPM" and OpenRGB keeps running without the hub. The worker's stopped-pump mark is cleared when holding is next forbidden and allowed again, so a later take-over starts clean. During a take-over, "OpenRGB still has the hub" is an interruption like any other, and the one restart for a stale detector is neither run nor spent.
24. **A changed hub state always reaches the window.** The helper sends a state whenever the hub's state member differs from the last one it sent, so a second dialog reading with unchanged parts, a changed let-go reason and a new interruption each arrive. Readings are sent as events only while DeadWare drives the hub.
25. **Conflicts while OpenRGB is starting.** The scan the helper makes for the hub while an OpenRGB start is pending discards a result during which its own OpenRGB was launched, as the ordinary scan does.
26. **The detector at a stop.** The write at a controlled stop only changes an existing `OpenRGB.json` whose hub key is not already `true`. It never creates the folder or the file, and when it wrote it logs "openrgb hub detector: on (at stop)".
27. **Gave back, and the gap after switching off.** Turning the switch on in the gave-back state does what **Try the take-over again** does. Between switching off and OpenRGB listing the hub again, the hub is shown as on its own and is not painted.
28. **The Windows session ending.** The window starts the helper's Stop first, then saves its own layout and settings as before, and only then waits for the stop, for what is left of 2 s.
29. **Guide and release note.** The release note says that nothing in the release has been tried on a real PC yet, and names the six behaviours that are outside the hardware check and covered by tests only; it and the guide's matching sentence are corrected after the hardware check. The guide no longer says that the hub's fans and pump run at fixed speeds in software mode. It says that the Corsair service makes DeadWare let go of the hub and blocks the take-over, that DeadWare writes OpenRGB's hub detector before every start and back on when it stops, and that a hub with a part DeadWare doesn't know is refused.
30. **Smaller points.** Both hub requests are refused as "still switching" while the PC is suspended or waking. No colour is written to a hub whose parts have no LEDs. The worker goes quiet from the suspend signal itself, without waiting for the loop. The DeadLight section also shows when its task could not be read, and "iCUE is not installed on this PC." shows only when both the services and the start-up entries could be read. iCUE's start-up entry is recognised by its program's file name. Settings reads the iCUE and DeadLight facts at most every 5 s. The hub appearing, disappearing and "more than one hub listed" are each logged once per change.

Three more, ruled by the design session on the plan check's open minors:

- **Starting is not "let go".** Until lighting has connected once in a helper run and the hub has not yet been held, the reason is "starting". Like "waking" it shows no caution line in Settings and no hub line on the Lighting page, and for both the Settings state line reads "On. DeadWare takes the hub in a moment."
- **"OpenRGB still has the hub", once the one restart is spent,** reads "when DeadWare is restarted" and "until DeadWare is restarted".
- **Retire never ends DeadWare's own OpenRGB.** When the chosen OpenRGB location is the one inside DeadLight's folder, Retire ends no `OpenRGB.exe` and the result line adds "DeadWare's own OpenRGB runs from DeadLight's folder, so it was left running."

## Constraints carried from the hand-over

- Never launch DeadWare, its helper, OpenRGB, DeadLight or iCUE except through an owner-approved elevated script. Never run git or dotnet in `I:\DeadWare` itself (work in the `lighting-step5` worktree). Never tag without asking.
- Nothing touches the hub without the owner's approval. The owner changes iCUE's service and start-up entry himself.
- At most two kill-and-relaunch tests per session; no on-screen automation while the owner is gaming.
- `HostState` and `DeviceView` grow only by optional trailing members; pipe `Version` stays 1.
- Claude shells see shadow copies of AppData; evidence is copied out by an owner-run script.
- Corsair protocol facts may inform the code; no code or tests are copied from OpenLinkHub, OpenRGB, liquidctl or FanControl.CorsairLink.

## Appendix: step 4's seven hardware-check items (from its hand-over, unchanged)

After each item the owner runs the snapshot script and the helper's log is read back.

| # | Owner does | Owner looks at | Log lines that confirm it |
|---|---|---|---|
| 1 Shortcut | Presses Ctrl+Alt+L twice, a few seconds apart, with another program in front | Lights go off, then on; the page's switch and the tray line follow | `Shortcut set: ...` at start; two `shortcut pressed: ...` lines (off, then on) |
| 2 Idle | Sets "after 1 minute" on the Everyday card, hands off for 90 s, then moves the mouse | Lights off with the reason line, back on at the movement | `idle spell started (1 min)`, `idle spell ended` |
| 3 Lock | Switches the lock rule on, Win+L, signs in again | Lights off while locked (as far as they can see), on after | `locked`, `unlocked` |
| 4 Mouse wireless | Unplugs the mouse cable (receiver stays in), picks a colour for the mouse | Mouse takes the colour; card says wireless and shows a battery reading | `receiver opened`, a `battery: ...` line |
| 5 Mouse sleep | Wave running, mouse sleep time 1 minute, leaves the mouse still for 2 minutes, then moves it | Whether the mouse went dark, and whether the effect came back on moving | `mouse sleep time sent: 1 min`, `mouse sleep time saved`; the log cannot prove the mouse slept: only the owner's eye does |
| 6 Sleep and wake | Sleeps the PC for about a minute, wakes it, waits 20 s | Lights come back by themselves, no blink-storm | `suspend`, `resume`, `wake repaint done: N devices` |
| 7 Cable plug-in | With the mouse on wireless and DeadWare running, plugs the mouse cable in | Lights may blink for a few seconds; mouse lit on its cable; card says on its cable | `mouse cable seen: asking OpenRGB to look for hardware`, then `mouse cable listed`; no `hardware rescan request failed` |

Also worth one look, because none of step 4's window was ever run: the tray menu's lights line, the reason line, the Everyday card's layout and recording a new shortcut, and the battery bar going grey with "last known" when the mouse is out of reach.

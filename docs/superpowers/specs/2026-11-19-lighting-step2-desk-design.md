# Lighting step 2: desk layout and effects

Status: draft for the owner's approval, 2026-11-19. This is build-order step 2 of `2026-11-15-lighting-design.md` (the main spec). Step 1 (host, pipe, OpenRGB, static colours) is merged to main as `0e0a6fb` and is not tagged. Where this document and the main spec disagree, this document governs for step 2; the main spec gets a one-line pointer to it as the first task of the plan.

## Intent

Step 1 gave DeadWare a lighting helper that paints saved colours. Step 2 makes the lights move: the three desk effects from DeadLight (Wave, Breathing, Spectrum), a picture of the desk that says where each light is so Wave can sweep across it, and a live preview of the colours on that picture.

The owner wants DeadLight's lighting inside DeadWare so that one program owns the hardware. Effects and the layout are the largest part of DeadLight still missing.

## Decisions taken in brainstorming (2026-11-19)

- **Generic only (the owner's decision).** The desk card draws no furniture and no rig-specific inside view. Every PC, the owner's included, gets the same drawing rules: a desk outline, a box per device, and a PC tower box that opens to show the devices inside it. This replaces the main spec's "plan A" picture, its "inside view drawn from the side-window photo" and its "Default for this PC" layout. The approved mockups in `2026-11-15-lighting-mockups/` no longer describe the desk card; the effect tiles and the overall page arrangement in `lighting-page.png` still do.
- **Order.** The owner chose to go straight to step 2 and not to read the temperature history from before the three unexplained hard resets first. That offer is made again when fan-control work starts.
- **Approach.** DeadLight's layout model is ported as it is (see Approaches). The owner left the how to the builder; this was Claude's pick.

## Goals, non-goals and anti-goals

**Goals**

- Wave, Breathing and Spectrum run in the lighting helper and keep running when the window is closed, hidden or crashed.
- A desk card on the Lighting page where the user sees every lit device, selects one, and in Edit layout drags devices and zones to where they really are.
- New devices are placed automatically, so effects work before the user has arranged anything.
- The desk card shows the current colours while the page is visible.

**Non-goals** (later steps or dropped)

- Per-device brightness and calibration, keyboard modes, per-key painting, a wave that travels across the keyboard's keys, layers and key reactions (step 3).
- The Aerox wireless driver, Everyday controls, the tray item, sleep and resume (step 4).
- The iCUE LINK hub driver (step 5).
- Furniture, monitors, a "you" marker, or any drawing specific to one PC.
- Free drawing of the desk outline: the outline comes from three presets only. DeadLight's drag and resize of desk rectangles, and its Rotate and Mirror buttons, are not ported.
- DeadLight's "Preview wave" button. The effect tiles sit above the desk card, so selecting Wave is the check.
- Importing DeadLight's `layout.json`.

**Anti-goals.** The main spec's anti-goals all hold. For this step in particular:

- Switching effect, or dragging a device, makes a light blink, flash white or go dark for a moment.
- The helper or OpenRGB uses noticeable CPU while an effect runs, or the helper computes effect frames while the lights are off.
- Colour frames cross the pipe when no window is showing the Lighting page.
- A log gets a line per frame or per tick.
- A device running its own hardware effect (the Lighting Node Pro) is written to by an effect.

## Constraints

Everything in the main spec's Constraints section applies. Those that shape this step:

- No file, class, member or log may be called "helper". User-facing text may say "lighting helper".
- `lighting.json` is written only by the helper, validated on every read and write, capped at 1 MiB, and a change that can't be saved is refused with the live settings unchanged.
- The window does no per-frame work while the page or window is hidden (`Lifetime.WhileShowing`).
- `src/DeadWare.WinUI` has no test project. Logic that needs tests lives in `DeadWare.Core` or `DeadWare.Services`.
- DeadLight is the behavioural reference at commit `0497cd3` (its current head, `440c7e5`, changes none of the files named below). Its code is the owner's own and may be ported directly.
- Hardware checks: each live step is approved by the owner one at a time; no on-screen automation while the owner is gaming.
- Never tag. 0.19.0 stays the owner's call, and whether step 2 ships inside 0.19.0 or after it is too.

## Approaches considered

1. **Port DeadLight's layout model as it is (chosen).** Every zone is a point in the unit square. Devices in the PC tower form a group that moves together. New zones are placed automatically on a grid. It is proven on the owner's rig, its tests port over, and zone-level points are needed anyway: the iCUE LINK hub is one device whose three fans and pump sit in four different places.
2. **One point per device, zones spread automatically.** Fewer things to drag, but it cannot put the pump and the radiator fans in different places, so Wave would be wrong inside the tower.
3. **Effects computed in the window.** Ruled out by the main spec: the lights would stop when the window crashes, and every frame would cross the pipe.

## Proposed experience

### Effect tiles

Four tiles under the header, one selected: **Static** (your saved colours), **Wave** (sweeps across the desk), **Breathing** (everything pulses together), **Spectrum** (everything cycles through colours together). The selected effect's settings sit in a row under the tiles:

| Effect | Settings |
|---|---|
| Static | **Set all colours** with Undo, as today |
| Wave | **Colours** (0 to 8 swatches; none means the full rainbow), **Speed**, **Direction**, **Spread** |
| Breathing | **Speed**. Each zone pulses its own saved colour, from black to that colour and back. |
| Spectrum | **Speed**. Every zone shows the same colour, moving round the full colour circle. |

- **Speed** is a slider, 0 to 100, default 40.
- **Direction** is a list of eight: Left to right, Right to left, Back to front, Front to back, and the four diagonals. Default: Left to right.
- **Spread** is a slider, 0 to 100, default 50. Higher means narrower bands.
- Each effect remembers its own settings when another is selected.
- Selecting an effect applies it at once. No Apply button.

### Desk card

```
┌ Desk ───────────────────────────────────────────  [ Edit layout ] ┐
│                         BACK OF DESK                              │
│  ┌──────┐┌───────────────────────────────────────────────┐        │
│  │      ││                                               │        │
│  │ ┌PC tower┐           ┌ Apex Pro ───┐   ┌ Aerox 9 ┐    │        │
│  │ │ 6 devices│         │ 1 zone      │   │ 3 zones │    │        │
│  │ └────────┘           └─────────────┘   └─────────┘    │        │
│  │      │└───────────────────────────────────────────────┘        │
│  └──────┘                                                         │
│                       FRONT · YOUR SEAT                           │
├───────────────────────────────────────────────────────────────────┤
│ Not placed: (none)                                                │
└───────────────────────────────────────────────────────────────────┘
```

- **The desk outline** is drawn from the chosen shape (Straight, L left, L right) as a visual guide only. Effects never read it. Default: L left.
- **A device box** per device that has LEDs and is on the desk, drawn at its first placed zone's point, labelled with the device's name and zone count, and filled or edged with its current colour. A device running its own hardware effect is drawn grey with the tag "own effect".
- **The PC tower** is one box labelled "PC tower" with the number of devices inside. Tapping it opens it in place: the box grows to enclose the devices inside, which are then drawn as their own boxes. Tapping its label again closes it. This is the generic "inside view".
- **Tapping a device** selects it. Its panel appears on the right (below on a narrow window). Selecting a device that is inside the tower opens the tower.
- **A device list** under the desk names every device with where it is (On desk, Inside PC, Not placed, Own effect, or Not on desk for a device DeadWare can't light or one with no lit zones, which the desk never draws). It is the keyboard and screen-reader way to select a device, since a drawn canvas is not accessible.
- **Not placed** lists devices the user has taken off the desk. They keep their static colour during every effect. Each has **Place on desk**.

### Device panel

For the selected device: its name, then what step 1's device card shows today (its zones with their colours, each with its colour flyout and "Use the shared colour"; the notes for a device on its own hardware effect and for zero-zone devices). Step 1's list of device cards is removed; the panel replaces it. With nothing selected the panel says to select a device.

In Edit layout the panel also shows:

- **Inside PC tower** (checkbox).
- **Move zones separately** (toggle). When on, each of the device's zones is drawn as its own labelled dot that can be dragged.
- **Take off the desk** (moves the device to Not placed).

### Edit layout

A toggle button on the desk card; it reads "Done" while on. While on:

- Drag a device box to move all its zones together. Drag a zone dot to move one zone. Drag the PC tower's label, or anywhere on its box while it is closed, to move the tower and everything inside it.
- Dropping a device on the PC tower puts it inside; dragging it out takes it out, and it stays out.
- Positions snap to a 48 by 48 grid.
- With a device or the tower selected, the arrow keys move it one grid step, while the desk has keyboard focus. Arrow-key moves are saved at most five times a second.
- **Desk shape** list: Straight, L left, L right. It changes only the outline.
- **Reset positions** asks for confirmation, then arranges every device from scratch and keeps the desk shape.
- The lights take the new positions when a drag is dropped, not during the drag.
- Leaving the page ends Edit layout.

### Live colours

While the Lighting page is visible and an effect other than Static is running, device boxes and zone dots show the colours the helper is sending, updated at most 10 times a second. The picture shows colours before the Brightness slider is applied, so it stays readable at low brightness. With **Lights on** off, the boxes are drawn unlit. With Static, the picture uses the colours already in the helper's state and no frames flow.

### What sits out

- A device on its own hardware effect is never written to by an effect.
- A Not placed device shows its static colour.
- A device whose modes don't include Direct, or that has no LEDs, has no box.

## Architecture

### Effects (new, `DeadWare.Core`)

Built from DeadLight's `shared/effects.ts`, with the HSV and blend helpers it needs from `shared/color.ts`. The speed, wavelength, projection, phase and palette maths, and Wave, are ported unchanged. Breathing and Spectrum are deliberately different from `effects.ts`, which times them by position and lets them take a palette; here they follow the rules below, so their cases in `effects.test.ts` are rewritten, not ported. This also replaces the main spec's line that Spectrum cycles "through the palette".

- Speed 0 to 100 maps to 0.05 to 2 cycles per second.
- Spread 0 to 100 maps to a wavelength of 3.0 down to 0.2 desk widths (inverted on purpose).
- A zone's distance along the wave is `x·cos(angle) + y·sin(angle)`, with 0° left to right and 90° back to front (y grows towards the user).
- Phase is the fractional part of `distance / wavelength − seconds × cycles per second`.
- **Wave:** the palette sampled at the zone's phase; N colours blend across N segments and wrap, and an empty palette is the full hue circle.
- **Breathing:** the zone's static colour, meaning what `LightingPaint.ZoneColor` gives today (the zone's own colour, or the shared colour when it has none; never black as in DeadLight's painter), scaled by `0.5 − 0.5·cos(2π·phase)`, with the phase taken at the origin for every zone, so everything pulses in step.
- **Spectrum:** the full hue circle at the origin's phase, for every zone.
- The functions are pure: time is an argument.

The eight directions are stored as their angle in degrees. Front to back is 270°. The helper and the file accept any finite angle from 0 up to, not including, 360; the page shows the nearest of the eight.

### Layout (new, `DeadWare.Core`)

A port of DeadLight's `shared/layout.ts` without rotate and mirror:

- `zones`: zone key to a point in the unit square.
- `tower`: an anchor point, `members` (device keys inside), `excluded` (device keys the user dragged out, so auto-arrange doesn't put them back).
- Opened, the tower's box is the bounds of its members' points with a margin of 0.06 on every side, so it always has area.
- `desk`: at most two rectangles, set only from the three presets (DeadLight's numbers).
- `unplaced` (new): device keys the user took off the desk.
- Validation as in DeadLight's `validateLayout`, plus `unplaced` as an array of strings.
- **Auto-arrange** as in DeadLight's `autoArrange`: a device whose name doesn't match the peripheral pattern (keyboard, mouse, headset and the like) joins the tower unless excluded; each unplaced-so-far zone of a device with LEDs takes the first free cell of a 12 by 8 grid, in the tower's region or the peripheral region, spilling to the nearest free cell; everything is sorted by key so the result doesn't depend on enumeration order; nothing changes when nothing is new. Devices in `unplaced` are skipped.
- **One difference from DeadLight:** only a device that gets a box (it has LEDs, and it is painted in Direct mode or runs its own hardware effect) joins the tower's `members` or is given points. DeadLight decides membership before its LED filter, which would make the tower's device count include devices that are never drawn. A device that gains LEDs later, as the Lighting Node Pro does once its channel is sized, is arranged at the rescan that shows them.
- **Entries for absent devices are kept**, so an unplugged device returns to its place. Nothing prunes them in this step; **Reset positions** clears them, and the 1 MiB rule bounds the file.
- **Reset positions** is computed in the window with the same Core function: an empty layout (no zones, members, exclusions or `unplaced` entries, the tower back at its default anchor of 0.14, 0.3) that keeps the current desk shape, auto-arranged over the live devices, then sent as one `SetLayout`. Devices that were Not placed are back on the desk. There is no separate request.

Geometry the window needs and that must be tested also goes in Core: where each box and dot is drawn, the tower's bounds open and closed, hit-testing a point, applying a drag with snapping, the drop-on-tower rule, and the arrow-key nudge.

### Settings and the file

`LightingSettings` gains:

- `Effect`: the selected kind (Static, Wave, Breathing, Spectrum) and one saved parameter set per kind (speed, angle, spread, colours).
- `Layout`: the layout above.

`lighting.json` stays at version 1 and the pipe protocol stays at version 1. Both fields are optional when read, and a file without them gets Static and an empty layout that auto-arrange fills. Step 1 was never released, so no installed copy writes or reads a version 1 without them.

`LightingKeys.Remap`, which moves saved colours when a device's key changes because a USB serial drops or comes back, also moves the layout's zone points, tower members, exclusions and `unplaced` entries, under the same refusal rules. Two things make that work:

- A device key that appears only in the layout counts as stored, on both sides of the rules. Today the stored set is built from colours and hardware modes only, and **Set all colours** clears the colours, so most devices would otherwise have nothing to remap from.
- At every rescan the helper remaps first and auto-arranges second. Auto-arrange therefore never gives a returning device's new key fresh points before its old points have been moved to it, which the "target already stored" rule would then refuse for good.

Reading the file:

- An effect kind this build doesn't know is read as Static, and the file is kept.
- Speed and spread outside 0 to 100, and an angle outside 0 to 360, are clamped or wrapped on read.
- A wrong type, a bad colour string, more than 8 colours, or a layout that fails validation makes the file invalid, and step 1's rule for an invalid file applies.

### Painting

`LightingPaint.Paint` takes the effect time and returns, for a device in Direct mode with lights on:

- Static selected, the device in `unplaced`, or a zone with no point: the zone's static colour, as today.
- Otherwise the effect's colour for the zone's point. Every LED in a zone gets the zone's colour.
- A zone with no point is painted its static colour, where DeadLight's painter reports it and leaves it black; that painter test is not ported.
- Then the global brightness, as today.

Hardware-mode and unsupported devices are handled exactly as in step 1, whatever effect is selected. With lights off the paint is the black refresh step 1 sends, and no effect is evaluated.

### The helper's loop

- The paint tick is 500 ms when Static is selected or the lights are off, and 33 ms while an effect runs with the lights on (the main spec's pacing). The per-device caps (33 ms, and 200 ms for RAM) and the 400 ms refresh already in `LightingDeviceService` are unchanged.
- The conflict scan and the device rescan keep their 5 s cadence, whatever the paint tick is.
- Effect time is the monotonic time since the helper started, so every device is in step and a clock change doesn't jump the pattern.
- After each rescan that changes the device list, the helper runs auto-arrange. If the layout changed, it saves and raises a state event.
- Changing effect or layout never restarts OpenRGB, never asks OpenRGB to rescan its hardware and never switches a device's mode. If OpenRGB has announced a device-list change the helper has not read yet, the change reads it first, so nothing is painted at a stale device index, as in step 1.

### Pipe

New requests:

- `SetEffect(kind, parameters)`: validates, saves, replies, then the next tick paints it. A request is refused, not clamped, when a number is out of range or not finite, a colour is malformed, there are more than 8 colours, or the kind is unknown; the live settings stay as they were.
- `SetLayout(layout)`: replaces the whole layout. The helper validates it, runs auto-arrange over it against the live devices, saves the result and replies; the state event that follows carries the layout as saved. So a device that was placed automatically while the window held a draft, and is missing from the draft, is placed again and never left without points. The layout may name devices that aren't connected. The window sends it once per completed edit (a drop, a nudge, a checkbox, a shape, a reset), never during a drag.
- `SubscribePreview(on)`.

New event:

- `PreviewFrame`: zone key to colour for every painted zone, before brightness. Sent only to a connection that subscribed, only while an effect runs with lights on, at most 10 a second, and only when the colours differ from the last frame sent.

The state event already carries the whole settings, so it carries the effect and the layout with no change. A subscription ends with its connection.

### Window

- A `DeskView` drawn with Win2D, following `ConnectionMapView`. It redraws only on a state change, a preview frame, or pointer input.
- `LightingPage` gains the tiles, the settings row, the desk card, the device list and Not placed, and the panel that replaces the device cards. The step 1 behaviour of zone flyouts, throttled sends, Undo and the disconnected states is kept, moved into the panel.
- The page subscribes to preview frames when it is shown and unsubscribes when it is hidden, through `WhileShowing`.
- During a drag the window draws from a local draft; it sends `SetLayout` on drop and keeps the draft until the reply, so nothing snaps back.

## Testing

- **Core:** port DeadLight's `effects.test.ts` and `layout.test.ts`. Rewrite, not port, its cases that time Breathing and Spectrum by position. Add tests for the eight directions, the per-kind saved parameters, `unplaced`, the remap of layout keys, and the geometry (hit-testing, drag with snap, tower drop, nudge).
- **Paint:** from DeadLight's `painter.test.ts`, the cases that apply: an effect colour per zone, a zone without a point, a hardware-mode device untouched, lights off gives black with no effect evaluated, brightness applied last.
- **Helper** (fake OpenRGB session and fake time, with the determinism rules step 1 earned): the tick is 33 ms only while an effect runs with lights on; frames change over time; the 5 s work keeps its cadence at the fast tick; auto-arrange runs after a device-list change and saves once; `SetEffect` and `SetLayout` refuse bad input and a failed save; preview frames go only to a subscriber, at most 10 a second, and stop when the lights go off or the connection ends.
- **Codec:** a file without the new fields loads; invalid effect or layout values are handled as step 1 handles invalid colours; the 1 MiB rule holds with a large layout.
- **Window:** no automated tests (no WinUI test project). Covered by the hardware check.

## Hardware check (with the owner, each step approved first)

1. Before the check, a full power-off, to see whether the graphics card's RGB is detected again (it has not been since the hard reset on 2026-11-19).
2. Wave on every device: the colour passes across the desk in the direction chosen; no blinking.
3. The Lighting Node Pro's front fans keep their own Rainbow Wave under every effect.
4. Breathing and Spectrum are in step on every device.
5. Switching between all four effects, and changing speed, direction, spread and colours: no flash, no dark moment.
6. Edit layout: move the keyboard to the other side of the mouse and see the wave reach them in the new order; drag a device out of the tower and back.
7. Lights off during Wave, then on again.
8. CPU use of `DeadWare.Devices.exe` and `OpenRGB.exe` while Wave runs, read from Task Manager, against DeadLight running its own Wave.
9. Close the window during Wave: the lights keep moving. Reopen: the desk shows the live colours again.
10. Quit: no helper or OpenRGB left.

## Left to the builder

- Class and file layout, message shapes, and how the Core geometry is split.
- The drawing details of boxes, dots, the tower and the outline, and the page's exact arrangement at narrow widths.
- How the colour swatches for Wave are edited (reusing the page's colour picker flyout is expected).
- How the 33 ms tick and the 5 s work share the loop.

## Calls made in writing this up (for the owner to check)

- The step 1 device cards are removed; their content moves into the panel for the selected device.
- The generic "inside view" is the PC tower box opening in place, as in DeadLight's editor.
- Not placed is a list the user fills with **Take off the desk**. Nothing starts there: every device with LEDs is placed automatically, the motherboard's zones included (inside the tower).
- Direction is a list of eight named directions, not a 0 to 359 dial.
- Breathing pulses each zone's own saved colour and has no colour setting. Spectrum always uses the full colour circle. Only Wave has a palette.
- The lights follow a drag when it is dropped, not while it moves. DeadLight followed during the drag; dropping is simpler and keeps "a change that can't be saved is refused".
- Desk outlines come from three presets only. Rotate, Mirror, free resize and "Preview wave" are not ported.
- The live picture shows colours before the Brightness slider.
- A device list under the desk is the keyboard and screen-reader path.
- `lighting.json` and the pipe protocol stay at version 1, because step 1 was never released.
- Breathing uses the shared colour for a zone with no colour of its own, so the desk never goes dark after **Set all colours**.
- A change the lighting helper is asked to make with out-of-range values is refused; out-of-range numbers found in the file are clamped, and an unknown effect in the file is read as Static, so a small fault never sets the whole file aside.
- Positions of unplugged devices are kept until **Reset positions**.
- Work happens on branch `claude/lighting-step2`, in the worktree `.claude/worktrees/lighting-step2`.

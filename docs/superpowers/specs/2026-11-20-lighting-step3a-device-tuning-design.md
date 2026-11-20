# Lighting step 3a: device tuning and keyboard modes

Status: draft for the owner's approval, 2026-11-20. Step 3 of the build order in `2026-11-15-lighting-design.md` ("Device controls") is split in two by agreement with the owner on 2026-11-20: **3a** (this document) is per-device brightness, colour correction and the keyboard's lighting modes; **3b** (its own document later) is key painting and layers with key reactions. Where this document and the 2026-11-15 design differ, this one governs for 3a.

Mockup, approved by the owner on 2026-11-20: `2026-11-20-lighting-step3a-mockups/device-cards.html`.

## Intent

DeadLight lets the owner tune each device and give the keyboard its own lighting mode. DeadWare 0.19.0 has neither: one brightness for everything, one colour per zone, and the keyboard's 112 keys always share a colour. Until DeadWare has these controls, DeadLight can't be retired. 3a brings over the ones that need no knowledge of key presses.

## Goals

- Every device DeadWare paints gets its own **Brightness** and **Colour correction** (red, green, blue), set from its card on the Lighting page.
- The keyboard gets a **Lighting mode**: Follow the desk effect, Static, Breathing, Colour cycle, Rainbow wave, with one Speed setting.
- The keyboard in the desk picture is drawn key by key and every key shows its own live colour, so Rainbow wave is visible on the page as it is on the desk. (Owner, on the first mockup: "Keyboard doesn't show keys, just lighting around the edge".)
- The helper paints each light of a device separately where a mode needs it, on one path that 3b's key paint and layers will reuse.
- Behaviour matches DeadLight's where DeadLight has the feature, so nothing changes under the owner's hands when DeadLight is retired.

## Non-goals

- Key painting, layers, key ripple and key fade, and the keyboard hook they need: 3b.
- Anything specific to the mouse (battery, sleep timer, wireless): step 4.
- Brightness or correction per zone or per part. They are per device, as in DeadLight: the hub's three fans and pump share one setting.
- Lighting modes for devices other than keyboards.
- A larger view of the keyboard. 3b decides whether painting needs one.
- Importing DeadLight's saved tuning. Nothing is imported from DeadLight (2026-11-15 design).

## Anti-goals

- A device in its own hardware effect must never appear to be tuned: its controls are disabled and say why.
- A tuning change must never make lights blink or reset an effect's phase.
- An older DeadWare that reads the new settings file (after a rollback) must not strip the new settings from it.
- No new permanent load: the helper's fast tick runs only while something is actually animating.

## Constraints

- `lighting.json` is written only by the helper, validated on every read and write, 1 MiB cap on its written form (2026-11-15 design, Persistence).
- The pipe contract's rule stands: a reply is written before the state its request caused, and the helper raises a state after every accepted change.
- No code is copied from OpenRGB, OpenLinkHub or liquidctl. DeadLight is the owner's own code and is the behavioural reference: `shared/device-lighting.ts`, `shared/device-effects.ts`, `shared/effects.ts`, `shared/keyboard-layout.ts`, `electron/painter.ts` in `I:\DeadLight` (working tree on branch `claude/live-bound-diag` at the time of writing).
- The lights can't be checked on the owner's PC while the "no lighting software" freeze test runs (started 2026-11-20 15:15). Design, build and review go ahead; the hardware check waits for that test's verdict or the owner's word.

## Approaches

1. **Per-light colours for every device, one path (chosen).** The painter already returns one colour per light (`DevicePaint.Colors`); today every light of a zone gets the zone's colour. 3a lets a device's base frame differ per light (the keyboard's Rainbow wave), then applies correction and brightness to the whole frame. 3b adds key paint and layers between those two stages. This is DeadLight's `compose` shape.
2. **A separate painter for the keyboard.** Smaller now, but 3b would need the general path anyway and two paths would have to agree on brightness, correction and lights-off.
3. **Dropped: frames computed in the window.** The lights must keep running with the window closed.

The owner left the choice to the builder; the first was chosen for the reasons above.

## Experience

The device card beside the desk picture (and the device panel of the plain desk on other PCs) is built from sections, top to bottom, as in the mockup:

1. **Colour**: as today.
2. **Lighting mode** (keyboards only): a drop-down with
   - *Follow the desk effect*: what the effect tiles choose (the default),
   - *Static*: its saved colour,
   - *Breathing*: its saved colour, pulsing,
   - *Colour cycle*: all keys change colour together,
   - *Rainbow wave*: a rainbow sweeps across the keys, left to right,
   
   and under it a **Speed** slider, 0 to 100, shown for Breathing, Colour cycle and Rainbow wave. *Rainbow wave* is offered only when the keyboard reports where its keys are (see Key map); otherwise it is listed disabled with "This keyboard doesn't report its key positions."
3. **Brightness**: a "This device" slider, 0 to 100 %, default 100, with the line "Works with the page's Brightness: N% of M% here." when either is below 100.
4. **Colour correction**: Red, Green and Blue sliders, 0 to 200, default 100, a tick at 100, a **Reset** button that puts all three back to 100, and the line "For a device whose white looks tinted next to the others. 100 leaves a colour as it is."

A device running its own hardware effect shows a note first, "This device runs its own effect, **<mode>**. DeadWare's colours, brightness and colour correction don't reach it while it does.", and its sections are disabled (greyed, not hidden). A device DeadWare can't light at all has no card sections beyond what it has today.

In picture mode a device with several parts (the hub) has one Brightness and one Colour correction, with the note "Brightness and colour correction apply to the whole device" naming its parts when it has more than one.

Sliders behave as the page's Brightness slider does today: the lights follow while dragging, the value is saved, and a refused change puts the slider back and shows the helper's reason.

**The keyboard in the desk picture** is drawn as its keys: one rounded rectangle per key at the position and size the key map gives, inside the keyboard part's box, each filled with that key's live colour (preview frames while the page shows, else the saved colour), unlit when lights are off, dimmed with the rest of the picture when the helper isn't running. While the key map isn't known (keyboard not found yet, or it reports none) the part is drawn as today. The mockup's caption text beside the keyboard is not part of the page (owner, 2026-11-20).

## Behaviour

### Order of a device's colours

For a device DeadWare paints, per light:

1. **Base**: the device's lighting mode if it has one other than Follow; otherwise the desk effect or static colour, exactly as today.
2. *(3b: key paint, then layers.)*
3. **Colour correction**: each channel `v * gain / 100`, clamped to 0..255.
4. **Brightness**: multiplied by `device / 100 * page / 100`.

One rounding, at the end (away from zero, as `Rgb.Scale` rounds today). With correction 100/100/100 and device brightness 100 the result must equal today's output for every input: existing tests stay unchanged. DeadLight pins the order in `tests/painter-lighting.test.ts`, but its case also uses key paint and two layers, which are 3b. 3a ports the part without them: base `{101,51,21}`, correction 200/100/50, device brightness 50, page brightness 50 gives `{51,13,3}`. The full case (`[{64,15,8},{50,19,8}]`) is ported in 3b.

Lights off still means black for a painted device, whatever its tuning or mode.

### Keyboard modes

Maths as DeadLight (`shared/effects.ts`, `electron/painter.ts`):

- Speed 0..100, default 40; cycles per second `0.05 + speed/100 * 1.95`.
- **Static**: the saved zone colours.
- **Breathing**: the zone's colour as Static would show it (its own saved colour, else the default colour, by DeadWare's existing rule) times `0.5 - 0.5*cos(2π·phase)`, the same phase for every key. DeadLight paints black when a zone has no saved colour; DeadWare always has a colour for a zone, so it uses that.
- **Colour cycle**: one hue for the whole keyboard, moving round the full hue circle.
- **Rainbow wave**: per key, `phase = frac(x / wavelength - t * cps)` with wavelength 1.6 (DeadLight's spread 50) and direction left to right, where `x` is the key's horizontal position 0..1 from the key map; a light with no position uses 0.5. Full hue circle.

A keyboard mode replaces the desk effect for that device only. The keyboard's place on the desk plays no part in its own mode. The mode's time base is the helper's effect clock, so changing Speed or tuning never restarts it from zero more than the desk effect's own settings do today.

The helper's fast tick (33 ms) runs while lights are on and either a desk effect is running or any found, painted device has Breathing, Colour cycle or Rainbow wave. Otherwise it stays at the slow tick. The status line's "<effect> is running" keeps describing the desk effect only.

A device counts as a keyboard by OpenRGB's device type (keyboard). The owner's Apex Pro is one zone of 112 lights.

**A saved mode that can't run** (Rainbow wave saved and the keyboard now reports no positions; a mode saved for a device that is no longer a keyboard): the device is painted as Static, nothing is deleted, the card shows the saved mode with the reason beside it, and the helper logs it once per change, not per frame. It runs again when the device can do it.

**Preview frames follow the same condition as the fast tick.** Today they are produced only while a desk effect runs. They must also be produced while a device mode animates, or Rainbow wave under a static desk effect would never reach the picture.

### Key map

The helper reads, for each device, the name of every light and, for a zone that reports a matrix, each light's position: the mean column and row of its matrix cells, divided by `(width - 1)` and `(height - 1)`, or 0.5 on an axis where the matrix is one cell wide or tall (DeadLight's `keyboardLedPositions`; an empty matrix cell arrives as `0xFFFFFFFF`). DeadWare's OpenRGB layer doesn't read light names or matrices today (`OpenRgbSession.cs` carries a zone's name and light count only); OpenRGB.NET 3.1.1 provides both (`Device.Leds`, `Zone.MatrixMap`), so this is new work in that layer and in the test fixture, along with the device's total light count. A device offers Rainbow wave when it is a keyboard with exactly one zone whose light count equals the device's and at least one light has a position (`supportsPerKeyWave`).

The window gets the key map with the device (names, positions, and a key size derived from the smallest horizontal spacing as DeadLight's editor does) so the picture can draw it. 3b will use the same map for painting.

### Hardware modes and unsupported devices

Unchanged: a device in a hardware mode gets no frame, so tuning and modes don't apply; its saved tuning is kept and takes effect when it returns to DeadWare's colours.

## Data

`lighting.json` gains a per-device table keyed by device key:

- `Brightness`: 0..100, absent = 100.
- `Correction`: `R`, `G`, `B`, each 0..200, absent = 100/100/100. Reset removes it.
- `Mode`: `Kind` (`static`, `breathing`, `cycle`, `wave`; absent = follow the desk) and `Speed` 0..100.

An entry with nothing set is removed. Entries for devices that aren't connected are kept (a device may be unplugged), up to a fixed cap that the builder sets and validation enforces. The table's keys are re-keyed with everything else when a device's key changes (`LightingKeys`), so a keyboard whose USB serial drops keeps its tuning.

**Version.** The file is written as `Version` 2 **only while the per-device table has entries**, and as 1 otherwise. So a PC where nothing was ever tuned stays fully usable by 0.19.0 after a rollback. The bump must be explicit at save: today the loaded version is carried through and written back as it was, and a table saved under version 1 would be accepted by 0.19.0 as its own and dropped at its next save, which is the third anti-goal.

Checked against 0.19.0's code (builder check, 2026-11-20): it ignores JSON members it doesn't know, marks a file with a higher version as newer, uses what it understands, never quarantines or rewrites it, turns saving off and shows "Your lighting settings were saved by a newer DeadWare, so this version won't save changes to them." So after a rollback from a tuned 3a, lighting changes made in 0.19.0 last only until the helper restarts, and the tuning is intact when a 3a build runs again. Removing every tuning entry in 3a returns the file to version 1.

## Pipe

Two new requests, each validated by the helper and answered with a reply, then a state:

- set a device's tuning (brightness and/or correction, or reset correction),
- set a device's mode (kind and speed, or follow).

An unknown device key is refused in plain words. A mode the device can't do (Rainbow wave without positions, any mode on a non-keyboard) is refused in plain words.

The device as the window sees it gains its brightness, correction, mode, which modes it offers, and its key map. Preview frames gain per-light colours for devices that have a key map, alongside today's per-zone colours, still at most 10 a second and only while the page shows. As today, preview colours are the colours **before** correction and brightness (stage 1 of the order above): the picture shows what colour each light is set to, not how bright or how corrected, exactly as it ignores the page's Brightness today. Correction exists so the real light matches the intended colour, so the picture shows the intended one.

## Testing

- Core: the compose order and single rounding (the reduced DeadLight case above); identity when tuning is default, over the existing paint tests; clamping at 0..255; each keyboard mode's maths at fixed times; key positions from a matrix including empty cells and the per-key-wave eligibility rules (port `keyboard-layout.test.ts`); validation bounds; re-keying of the per-device table; version 2 round trip and a version 1 file loading with defaults.
- Helper: each new request accepted, refused and saved; the reply-before-state rule; fast tick on for an animated keyboard mode with a static desk effect and off again when it stops; per-light preview frames; a hardware-mode device unaffected.
- Page: no automated tests exist for WinUI; hand traces in each task report, then the owner's look.
- Every new test is seen failing for its stated reason first, or carries mutation evidence.

## Hardware check (owner, after the freeze test's verdict)

Each keyboard mode on the real Apex Pro, with the picture's keys matching it; Speed; brightness and correction on the keyboard, the RAM and the hub; a correction that visibly changes white; lights off and on with a mode running; the front fans' card disabled with its note; quit and restart keeping every setting.

## Left to the builder

- Names and shapes of the new classes, messages and JSON members; the cap on stored device entries; how the key map is packed for the pipe and for preview frames.
- Slider send rate and the draft or commit pattern, following the page's existing Brightness slider (`Throttle`, its sending count, drag and flush in `LightingPage.cs`), which is written for one slider and needs generalising.
- How the picture lays keys into the keyboard part's box (aspect, gaps, minimum key size) so it reads as a keyboard at the picture's smallest size.
- Whether the plain desk's keyboard box also shows keys. Default: no change to the plain desk in 3a beyond the card sections.

## Calls made in writing this up (for the owner to check)

1. The section is named **Colour correction**; DeadLight calls it Calibration.
2. **Colour cycle** is spelt the British way, as the page spells "Colours".
3. Lighting modes are for keyboards only, as in DeadLight.
4. Rainbow wave always runs left to right across the keys at a fixed width; only Speed is adjustable, as in DeadLight.
5. The keyboard drawn key by key is part of 3a, not 3b.
6. After a rollback to 0.19.0 from a PC with tuning saved, 0.19.0 runs the lights but won't save lighting changes (it says so on the page); the tuning is back when the newer build runs again. A PC with no tuning saved is unaffected.
7. The picture shows each light's intended colour, before colour correction and brightness, as it already ignores the page's Brightness. A keyboard set to 0 % still looks lit in the picture; the slider is where its brightness shows.
8. A saved mode the device can't run at the moment falls back to Static and says why on the card; nothing is deleted.
9. Breathing on a zone with no colour of its own pulses the default colour (DeadLight would show black).

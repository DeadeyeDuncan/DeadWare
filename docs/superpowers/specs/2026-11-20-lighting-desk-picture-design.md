# Lighting step 2, redesign: the desk as a picture

Status: draft for the owner's approval, 2026-11-20. It changes one part of `2026-11-19-lighting-step2-desk-design.md` (the step 2 spec): what the desk card draws and how devices get their positions on this PC. Everything else in the step 2 spec stands. Where the two disagree about the desk card, this document governs.

Mockups (approved by the owner on 2026-11-20, "These look good"): `2026-11-20-lighting-desk-redesign-mockups/lighting-page-desk.png` and `lighting-page-inside.png`, rendered from `page.html` in the same folder. They are the target for look and arrangement.

## Intent

Step 2 was built with a generic desk card: grey rectangles for the desk and the tower, and a labelled box per device. The owner saw it running for the first time on 2026-11-20 and called it "a GUI nightmare": "I expected the desk layout to look more like the picture provided. The current desk setup makes 0 sense, I expected the visual you showed me in the beginning."

The picture they meant is the pair of mockups approved before step 1 (`2026-11-15-lighting-mockups/`): the owner's real desk from above, and the inside of their PC. The owner wants to look at their own setup, see what is lit and in what colour, tap a thing to change it, and watch Wave cross it.

**How step 2 went wrong, so this document doesn't repeat it.** During the step 2 brainstorm the owner was asked "yours with a fallback, or generic only" in words, with no picture of what "generic" would look like, and answered "generic only". The controller took that as "plain boxes for everyone". The owner was expecting the approved mockups throughout. The lesson is applied here: the look was shown as mockups and approved before this document was written.

## Goals, non-goals and anti-goals

**Goals**

- On the owner's PC the desk card looks like the approved mockups: the L-shaped desk from above with its furniture in grey, the keyboard and mouse drawn as themselves, the PC tower on the floor, and an inside view of the tower with each lit part where it sits.
- Every lit part shows its live colour, and Wave is seen moving across the picture in the direction chosen.
- Tapping a part selects it and shows its colours in the panel. Tapping the tower shows the inside view; **Back to desk** returns.
- On the owner's PC nothing needs arranging: every device is in its place the first time the page opens.
- A PC that isn't the owner's still works, with the plain desk and its editor as built in step 2.

**Non-goals**

- Improving the look of the plain desk for other PCs. It stays as step 2 built it; its known faults are listed under Left for later.
- A way for a user to draw their own desk or tower.
- Dragging things on the picture. On the owner's PC the picture fixes every position, so there is no **Edit layout**.
- Per-key or per-LED drawing: the keyboard is one colour, a fan is one colour (step 3 brings per-key work).
- Anything from later steps that the old page mockup showed: Paint keys, Layers, calibration, the Everyday card.
- The PS5 controller.

**Anti-goals**

- The page shows something that isn't true: a part glowing in a colour its lights don't have, a part drawn lit when its device wasn't found, or Wave drawn moving one way while the lights move another.
- The owner has to drag, place or fix anything on their own PC.
- Another round where the look is discovered only when the app is first run. Every visible element in this document is in the approved mockups; anything the builder adds beyond them is shown to the owner as a picture first.
- Step 2's working parts (effects, the helper's loop, live colours, saving) are disturbed.

## Constraints

- All constraints of the step 2 spec and of the main spec apply (naming, versions staying at 1, the hidden-window rule, hardware checks approved step by step, never tag).
- `src/DeadWare.WinUI` has no test project. Everything that can be decided without drawing goes in `DeadWare.Core` with tests: which picture applies, which device and zone lights which part, where each part is, what a tap hits, and each part's position for Wave.
- The work lands on the same branch, `claude/lighting-step2`, and in the same draft pull request (#138), before that is merged.
- The owner looks at the page before it is called done. A mockup approved is not the page approved.

## Approaches considered

1. **A picture described as data (chosen).** A picture is a list of parts in Core: each part has a kind, a place in the desk view or the inside view, and the device and zone that light it. The page draws whatever the list says. The owner's rig is one built-in picture. Matching devices to parts, positions and hit-testing are all testable, and another rig would be another list.
2. **Draw the owner's rig directly in the page.** Quicker, but it puts the matching and the positions in the one project with no tests, which is where step 2's problems hid.
3. **An image file with clickable areas.** Win2D's SVG support can't reliably recolour and glow individual parts, and every lit part changes colour live.

The owner left the how to the builder; this was Claude's pick.

## The experience

As the mockups show. In words:

- **Header, effect tiles and the effect's settings row:** unchanged from step 2. The status line under the title reads `N devices through OpenRGB · <effect> is running` (or the existing reasons lighting is unavailable).
- **Desk card.** Title "Desk", hint "tap a device to change its colours · tap the PC to see inside". The drawing:
  - the L-shaped desk in a wood tone, long part along the back, short part down the left, labelled BACK OF DESK;
  - in grey, for orientation only and never tappable: main monitor, two speakers, second monitor, headphones and amp, and a "you" circle;
  - the keyboard as a keyboard outline with rows of keys, and the mouse on its mat, each glowing in its current colour, each labelled underneath;
  - the PC tower on the floor at the lower left, labelled "PC tower" with "tap to see inside ↗", showing a small glow from what's inside;
  - while Wave runs, a soft band and an arrow caption ("wave moving left to right") showing its direction. The band is a direction marker, not a live position: it lies across the middle of the drawing at right angles to the chosen direction, the arrow and the caption name that direction (the eight names of the Direction list, in lower case), and none of it moves. The lit parts' own colours show where the wave is. No band for Breathing, Spectrum or Static, and none in the inside view.
- **Inside the PC.** Tapping the tower replaces the desk drawing, in the same card, with the side-window view: title "Inside the PC", hint "side window, front on the right · tap a part to change its colours", and a **← Back to desk** button. The drawing:
  - three radiator fans across the top, rear to front;
  - the pump as a ring; two RAM sticks; the graphics card with its lit name;
  - three front fans down the front edge, drawn grey and labelled "own effect" while the Lighting Node Pro runs its own effect;
  - the rear fan and the power supply in grey, never tappable.
- **Selection.** Tapping a lit part selects it (an accent outline on the desk; a white outline on a fan) and the panel shows that device, with the tapped zone's row highlighted. Tapping empty space clears the selection. Selecting a part inside the PC while the desk is showing can't happen; the tower itself is not a selectable device.
- **The panel** (right of the card; below it on a narrow window): the device's name, which part is selected, and what step 2's panel shows today (each zone with its colour, its flyout, "Use the shared colour", the notes for own-effect devices). Nothing from later steps.
- **In the mockups but not part of the page:** the "On other PCs" card and the two dashed notes in the panel ("Under Wave the whole keyboard shows one colour…", "Each part of the hub is placed where it sits…"). They explain the mockup to its reader and are not built.
- **Under the drawing:** "Not in the picture: …" names lit devices or zones the picture has no part for, and says they keep their saved colour during effects. On the owner's PC that is the motherboard's two headers.
- **A part whose device wasn't found** is drawn unlit with "not found" under its label, and can't be tapped. (On 2026-11-19 OpenRGB did not detect the graphics card after a hard reset; the picture must say so honestly when that happens.)
- **Lights off:** every part is drawn unlit. **Helper not running:** the drawing is dimmed and taps do nothing, as in step 2.
- **Other PCs:** the plain desk card and its **Edit layout**, the Devices list and Not placed, exactly as step 2 built them.

## Which picture applies

- Core holds a list of built-in pictures. There is one: the owner's rig.
- A picture applies when its **signature devices** are all present in the device list. The owner's picture's signature is the motherboard `MSI MAG B650M MORTAR WIFI (MS-7D76)` and the `Corsair iCUE Link System Hub`. Both are always found on this PC, and no other PC is likely to have the pair.
- When a picture applies, it applies whether or not its other devices were found; a missing one is drawn "not found".
- **Once in use, a picture is kept while at least one of its signature devices is present.** OpenRGB has already failed to detect a device on this PC after a hard reset; one missed hub must not flip the owner's page to the plain desk and throw the layout away. So: a picture applies when all its signature devices are present, or when the saved layout already names it and at least one is present.
- When no picture applies, the plain desk is used.
- The helper decides, from the device list and the saved layout's `Picture` name, and the layout it publishes carries that name. The window draws the picture the published layout names and never decides for itself, so the two cannot disagree.

## The owner's picture: parts and what lights them

Device names and zone names are as OpenRGB 1.0rc3 reports them on this PC (the sanitised fixture `tests/DeadWare.Devices.Tests/Fixtures/controllers.json`).

| Part | View | Device (by name) | Zone |
|---|---|---|---|
| Keyboard | desk | `SteelSeries Apex Pro` | `Keyboard` (index 0) |
| Mouse | desk | a device whose name starts with `SteelSeries Aerox 9` | all four zones (`Front`, `Middle`, `Rear`, `Reactive`); the part shows the `Middle` zone's colour |
| Radiator fan 1, 2, 3 | inside, top row, rear to front | `Corsair iCUE Link System Hub` | zones 0, 1, 2 (`iCUE LINK RX RGB`) |
| Pump | inside | `Corsair iCUE Link System Hub` | zone 3 (`iCUE LINK TITAN 360`) |
| RAM stick 1, 2 | inside | the two `Corsair Vengeance RGB DDR5` devices, in order of device key | `Corsair DRAM` (index 0) |
| Graphics card | inside | a device whose name starts with `Gigabyte Radeon RX 6800 XT` | all five zones (`GPU zone 1` to `5`); the part shows zone 1's colour |
| Front fans ×3 | inside, front edge | `Corsair Lighting Node Pro` | zone 0 (`Corsair Channel 1`); one zone lights all three fans |
| not in the picture | | `MSI MAG B650M MORTAR WIFI (MS-7D76)` | `JRGB1`, `JRGB2` |

- The two RAM sticks have the same name; they are told apart by device key, sorted, as step 2 already does when it numbers duplicates.
- The mouse matches by prefix because its name differs between wired and wireless.
- Tapping a part selects the device and the zone whose colour the part shows: the zone given above, `Middle` for the mouse and `GPU zone 1` for the graphics card.

## Positions for Wave

Every zone a part is lit by gets a point in the desk's unit square, the same square step 2 uses (x left to right, y from the back of the desk to the front). The helper uses these points for Wave exactly as it uses arranged points today.

- **Desk parts** take the centre of their drawn shape in the desk view.
- **Tower parts follow the picture, not the room** (the owner's decision, 2026-11-20: "follow the picture"). Inside the PC the wave moves the way the inside view is drawn, so "left to right" runs from the rear of the case to the front, across the radiator fans in turn, as the approved inside-view mockup shows. This is a drawing convention: seen from above the tower is one spot on the floor, and a wave crossing the room would reach everything in it together. The owner chose the convention knowingly.
- To do that, the inside view's case box is mapped onto a fixed region of the unit square: x from 0.03 to 0.30 and y from 0.60 to 0.98. A tower part's point is the centre of its shape in the inside view, carried into that region. Rear to front becomes left to right; top to bottom of the case becomes back to front.
- So a left-to-right Wave starts at the rear of the PC, runs through it, then crosses the desk to the keyboard and the mouse. A part lit by several zones gives all of them the same point.
- Zones in "not in the picture" get no point, and step 2's rule gives them their saved colour.

**The layout while a picture applies.** At each device rescan, and when the helper first connects, the helper replaces the whole layout with the picture's: the zone points above and nothing else, no tower members or exclusions, every lit device the picture has no part for marked not placed, and the picture's name recorded in a new optional layout field, `Picture` (absent for the plain desk; `lighting.json` stays at version 1). Points left from an earlier arrangement, or from a device that is no longer found, do not survive. It saves only when that changes the saved layout, so a second rescan with the same devices saves nothing. `SetLayout` from a window is refused while a picture applies ("This PC's layout comes from its built-in picture."), and the window offers no way to send one.

**Leaving a picture.** When no picture applies but the saved layout names one, the helper resets the layout as **Reset positions** does and auto-arranges, and clears the name. A layout arranged by hand on the plain desk is not kept across a switch to a picture and back.

## Architecture

- **Core, new:** the picture model (parts with kind, view, shape box, label, the device and zone rule, rear-to-front fraction for tower parts; furniture with kind and shape box), the owner's built-in picture, and functions for: which picture applies to a device list; resolving each part to its device and zones or to "not found"; the layout points; what a tap at a point hits in a given view; the "not in the picture" list.
- **Core, changed:** nothing in the effect maths, the layout model or the plain desk's geometry.
- **Helper:** at a rescan, picture first, else remap and auto-arrange as today. `SetLayout` refused while a picture applies.
- **Window:** `DeskView` gains a picture mode beside its plain mode: it draws the parts and furniture of the current view from Core's data, with the shapes and glow of the mockups, and reports taps. `LightingPage` shows the picture card or the plain card according to which applies, hides the Devices list, Not placed and Edit layout in picture mode, and highlights the selected zone's row in the panel.
- Live colours, the panel's zone rows and flyouts, the effect bar, preview frames and saving are reused unchanged.

## Testing

- **Core:** the picture applies only with both signature devices; it still applies with any other device missing, and that part resolves to "not found"; each row of the table above resolves to the stated device and zone, using the fixture's device list; the two RAM sticks resolve in key order, and a single stick resolves to stick 1 with stick 2 "not found"; the mouse resolves under both its names; the picture is kept with one signature device missing when the saved layout names it, and not adopted in that case when it doesn't; layout points: desk parts at their shape centres, tower parts inside x 0.03 to 0.30 and y 0.60 to 0.98, radiator fans 1 to 3 at increasing x and all left of the keyboard; "not in the picture" lists exactly the motherboard's lit zones; a tap inside a part's shape hits it and a tap on furniture or empty space hits nothing; the tower is hit only in the desk view.
- **Helper:** with the fixture's devices, the saved layout has exactly the picture's points, names the picture and marks the motherboard not placed, and a second rescan saves nothing; points saved earlier for other keys are gone; `SetLayout` is refused; the hub disappearing keeps the picture and its layout; both signature devices disappearing resets and auto-arranges as in step 2 and clears the name; a Wave left to right gives radiator fan 1, fan 3 and the keyboard three different colours at one instant, in that order along the palette.
- **Window:** no automated tests. The owner looks at it running.

## Checking it with the owner

1. The page is opened on the owner's PC and compared with the two mockups side by side. Differences are fixed or agreed before anything else.
2. Then the step 2 hardware check's functional steps (effects on every device, no blinking, the Node Pro keeping its own effect, lights off and on, closing the window, quitting), which were postponed when the owner chose to redesign first.

## Left for later

- The plain desk for other PCs keeps the faults the owner saw on 2026-11-20: the desk and tower drawn as look-alike grey rectangles, boxes with cut-off names, a device list cut off by the window.
- Whether the plain desk's auto-arrange misplaced the RAM, motherboard and hub on 2026-11-20, or the owner had dragged them. Not known; to be checked before the plain desk is next worked on.
- The seven minor findings parked from step 2's final review, and the half-second effect start.

## Left to the builder

- Type and file names, and the exact numbers for each shape, taken from `page.html` in the mockups folder (its two SVG drawings use a 720 by 450 box).
- How glow is drawn in Win2D so it matches the mockups closely.
- The arrangement at narrow widths.

## Calls made in writing this up (for the owner to check)

- The inside view replaces the desk drawing in the same card, with **Back to desk**.
- No **Edit layout**, Devices list or Not placed on the owner's PC; other PCs keep all three.
- The picture is chosen automatically when the MSI board and the Corsair hub are both present; there is no setting.
- A part whose device wasn't found is drawn unlit and marked "not found".
- The motherboard's two headers are "not in the picture" and keep their saved colour during effects.
- The mouse and the graphics card are each one part in one colour, although OpenRGB gives them four and five zones.
- Radiator fans 1 to 3 are the hub's zones in order, drawn rear to front. If the real order is the other way round, it is one line to swap, found during the check.
- Inside the PC, Wave follows the picture (the owner's decision): the case is given the region x 0.03 to 0.30, y 0.60 to 0.98 of the wave's square. Those four numbers are the builder's, chosen so a left-to-right wave runs through the PC before it reaches the keyboard.
- The Wave band is a direction marker that doesn't move.
- The mockups' "On other PCs" card and dashed notes are not built.
- A picture, once in use, is kept while either the MSI board or the Corsair hub is still found, and the layout file records the picture's name.
- While the picture applies, any layout arranged earlier on the plain desk is replaced.
- The plain desk for other PCs is left as it is in this round.

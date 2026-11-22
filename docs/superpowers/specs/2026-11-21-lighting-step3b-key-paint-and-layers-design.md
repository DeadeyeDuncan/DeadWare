# Lighting step 3b: key painting, layers and saved keyboard looks

Status: draft for the owner's approval, 2026-11-21. Second half of step 3 ("Device controls") of `2026-11-15-lighting-design.md`; the first half is `2026-11-20-lighting-step3a-device-tuning-design.md` (3a, branch `claude/lighting-step3a`, PR #141, not merged when this was written). 3b is built on top of 3a. Where this document and the two earlier ones differ, this one governs for 3b.

Mockups, approved by the owner on 2026-11-21, in `2026-11-21-lighting-step3b-mockups/`: `key-painting.html` (the owner chose **Way 1**, pick keys then choose a colour) and `layers.html`. Both are clickable; they are the reference for layout and wording.

## Intent

DeadLight lets the owner give single keys their own colour and stack layers on the keyboard, two of which react to typing. DeadWare can do neither, so DeadLight can't be retired. 3b brings both over. The owner asked for two things DeadLight does not do well or at all: picking keys one at a time, and saving named keyboard setups to switch between.

## Goals

- **Key painting**: any key of the keyboard can have its own colour, set in a large keyboard view by picking keys (one by one, by dragging, or by a shortcut group) and then choosing a colour. Undo while the view is open.
- **Layers**: up to 8 per keyboard, applied in order on top of the lighting mode and painted keys. Kinds: a colour, an effect, key fade, key ripple. Each has a strength, a way of mixing, and "which keys".
- **Saved looks**: named setups of the keyboard (painted keys, lighting mode, layers) that the owner saves and switches between.
- **The keyboard is drawn with real key shapes** (wide space bar, wide Shift and so on) and key labels in the large view, and with the same shapes in the desk picture.
- Behaviour matches DeadLight's where DeadLight has the feature (paint, layer maths, reaction maths, the name guard), so nothing changes under the owner's hands when DeadLight is retired.

## Non-goals

- Layers or key painting on devices other than keyboards.
- DeadLight's Morrowind layer source (out of the merge by the 2026-11-15 design).
- Profiles for the whole desk. Saved looks are for one keyboard only.
- Importing DeadLight's painted keys or layers. Nothing is imported (2026-11-15 design).
- A brush tool, an eraser, dragging layers to reorder (buttons do it), an on/off switch per layer.
- The other open items from 3a (Breathing dark at the low end in the picture, Follow forgetting Speed, a matrix that positions no light, the plain desk rework). They stay parked.
- Anything for the mouse: step 4.

## Anti-goals

- **The helper must never become a key logger.** See "The key-press rule". This is the part of 3b a review must attack hardest.
- A painted key or a layer's key must never light the wrong key after the keyboard reports its lights in a different order.
- An older DeadWare (0.19.0 or 0.20.0) that reads the new settings file after a rollback must not strip the new settings from it.
- No new permanent load: the fast tick runs only while something is actually moving; a keyboard with a key-ripple layer and nobody typing stays on the slow tick.
- Switching looks must never silently throw away a setup the owner has not saved.

## Constraints

- All of 3a's: `lighting.json` is written only by the helper, validated on every read and write, 1 MiB cap on the written form; a reply is written before the state its request caused; no code copied from OpenRGB, OpenLinkHub or liquidctl.
- DeadLight is the owner's own code and the behavioural reference (`I:\DeadLight`, working tree): `shared/device-lighting.ts`, `shared/keyboard-input.ts`, `shared/keyboard-layout.ts`, `shared/effects.ts`, `shared/lighting-needs.ts`, `electron/painter.ts` (`compose`, lines 316-371; reactions 290-312), `tests/painter-lighting.test.ts`, `tests/keyboard-input.test.ts`.
- The helper (`DeadWare.Devices`) has no Win32 message loop, window or hook today. Its main loop is `DeviceHost.RunAsync` on `Task.Delay`. The hook needs a thread of its own with a message loop.
- The helper usually runs as administrator. A system-wide keyboard hook in an elevated process is the mechanism key loggers use; the design treats it accordingly.
- Never launched by an agent: DeadWare, its helper, OpenRGB, DeadLight. Hardware checks are the owner's, through approved scripts.

## Approaches

1. **Paint and layers in the helper, as a new stage of 3a's one paint path (chosen).** `LightingPaint.Paint` today computes a base frame (`Lights`) and then tunes it (`Tune`). 3b inserts key paint and layers between the two, which is DeadLight's `compose` order and what 3a's design reserved. Lights keep running with the window closed.
2. **Colours computed in the window.** Dropped: they would stop when the window closes.
3. **A separate keyboard-only painter.** Dropped in 3a for the same reason as there: two paths that must agree on correction, brightness and lights-off.

For key presses: **a low-level keyboard hook in the helper (chosen)**, as the 2026-11-15 design names it. Considered and dropped: Raw Input (needs a window and delivers the same information, no safer) and a hook in the window process (stops when the window is closed, and the window is the less protected process).

The owner left the how to the builder.

## Experience

### The keyboard's card

In order (new parts marked):

1. **Saved looks** (new): a drop-down of the saved looks with the current one shown, "(changed)" after its name when the keyboard no longer matches it, and buttons **Save changes**, **Save as a new look…**, **Rename**, **Delete**. With no looks saved it shows only "Save as a new look…".
2. **Colour**: as today.
3. **Key colours** (new): "N keys painted" and a **Paint keys…** button.
4. **Lighting mode**: as 3a.
5. **Layers** (new): "N layers" (or "No layers") and an **Edit layers…** button.
6. **Brightness**, **Colour correction**: as 3a.

"Paint keys…" and "Edit layers…" need the keyboard to report its key positions and its keys' names; otherwise they are disabled with "This keyboard doesn't report its key positions." or "This keyboard doesn't report its keys' names." On a PC without the desk picture (the Lighting page shows the plain desk) both are disabled, with "Painting keys and layers need the desk picture, which this PC doesn't have yet." under each. A keyboard running its own hardware effect has the new sections disabled with 3a's note.

### The large keyboard view

"Paint keys…" or "Edit layers…" swaps the desk picture for a large drawing of the keyboard, the way the inside-the-PC view takes over today. A two-part switch at the top, **Key colours | Layers**, moves between the two; **Done** returns to the desk. The card beside it shows the tools for the chosen part. Every change is applied to the real keyboard and saved as it is made. Leaving the Lighting page or selecting another device closes the view as Done does.

Keys are drawn at their real shapes with their labels (see Key shapes). A key shows its live colour, as the desk picture does.

**Key colours** (mockup `key-painting.html`, Way 1):

- Click a key to pick it, click again to drop it. Press and drag across keys to pick several (or to drop several, when the drag starts on a picked key). Picked keys have a bright outline.
- Shortcut groups: **All**, **None**, **WASD**, **Arrows**, **Number row**, **F keys**, **Number pad**. A group that matches no key of this keyboard is not shown.
- Choosing a colour paints the picked keys. The colour control is the picker the Colour section uses today, with a row of recent or preset swatches beside it (builder's choice of swatches).
- **Remove paint**: "From the picked keys", "From every key".
- **Undo** steps back through the paint changes made since the view was opened (at least 20 steps). It is forgotten on Done and when a look is chosen.
- The **Saved looks** control of the normal card is also shown on the card here, as in the mockup, and behaves the same.
- The line "N keys painted · M picked".
- An unpainted key shows whatever the lighting mode gives it.

**Layers** (mockup `layers.html`):

- The list, numbered, each row "kind · which keys" with **↑**, **↓** and **✕**; **+ Add a layer** (disabled at 8); "Lower in the list is on top. Up to 8."
- The selected layer's settings:
  - **What it does**: *A colour* (a fixed colour over the keys you choose), *An effect* (a moving effect over the keys you choose), *Key fade* (a key lights up when you press it, then fades out), *Key ripple* (a ring of light spreads out from the key you press).
  - **Colour** (not for the effects Wave and Spectrum).
  - **Effect** and **Speed** (effect layers only): Wave, Breathing, Spectrum, as the page's effect tiles name them; Speed 0 to 100.
  - **How long it lasts** (fade and ripple): 0.1 s to 3.0 s, default 0.6 s.
  - **Strength**: 0 to 100 %.
  - **How it mixes with what's under it**: *Covers it* / *Adds light to it*.
  - **Which keys**: *All keys* / *Only the keys I pick*, the latter with **Pick keys…** which turns the large keyboard into the same key picker as Key colours (same clicks, drag and groups) until **Finished picking**.
- A new layer is, as in the mockup: Key fade, light blue, 0.6 s, strength 100 %, adds light, all keys. (A full-strength colour that covers every key would blank the keyboard the moment it was added.)
- While any layer is a key fade or key ripple, the card shows this note (wording to keep):
  > **Key fade and key ripple need DeadWare to notice key presses.** It learns only which light to flash. It never stores what you type and sends it nowhere outside DeadWare, and it stops noticing as soon as no layer needs it. While this page is open, the keyboard drawn here lights up with your presses too.
  > A press on any keyboard plugged into this PC lights this one: Windows doesn't say which keyboard a key came from.

### Saved looks

A look is a named copy of the keyboard's painted keys, lighting mode (with its Speed) and layers. It does not hold brightness or colour correction (they tune the hardware, not the look) or the keyboard's Colour section.

- **Save as a new look…** asks for a name and saves the keyboard as it is now. A name already in use is refused with "There is already a look called X."
- Choosing a look in the drop-down switches the keyboard to it. If the keyboard has changes that are in no look (it is "(changed)", or it has paint, a mode or layers and no look was ever chosen), the page first asks "The keyboard's current setup isn't saved in a look. Switch and lose it?" with **Switch** and **Cancel**.
- **Save changes** overwrites the current look with the keyboard as it is now. Shown only when "(changed)".
- **Rename**, **Delete** (Delete asks first; deleting a look does not change the keyboard's lights).
- Up to 12 looks per keyboard; names 1 to 40 characters, compared without regard to case.

## Behaviour

### Order of a keyboard's colours

Per light, for a device DeadWare paints:

1. **Base**: as 3a (the lighting mode, else the desk effect or static colour).
2. **Key paint**: a painted key's colour replaces the base.
3. **Layers**, in list order.
4. **Colour correction**, 5. **Brightness**: as 3a.

One rounding, at the end. Between stages 2 and 5 a colour is carried as fractions and is **not** clamped or rounded between layers (DeadLight `painter.ts:352-364`); correction clamps to 0..255 as in 3a. 3a's `Tune` is whole-number maths on bytes and must stay exactly as it is for a device with no layers: every existing test stays unchanged. For a device with layers the same result must come out whenever the layer stage happens to produce whole numbers.

Pinned case (DeadLight `tests/painter-lighting.test.ts:23-26`), to port as a test: two lights "Key: A" at (0,0) and "Key: B" at (1,0); base (101,51,21); page brightness 50; device brightness 50; correction 200/100/50; key 0 painted (200,0,0); layer 1 a colour (100,100,100), strength 50, covers, all keys; layer 2 a colour (20,20,20), strength 50, adds, only key 0. Result `[(64,15,8), (50,19,8)]`. The second light is the one that fails if a fraction is rounded early (100.5 × 2 = 201).

Lights off still means black, whatever is painted or layered.

### Layer maths

For each layer, for each light it covers, the layer gives a colour `p` and an amount `a0` (0..1); `a = a0 × strength/100`; then per channel, *covers*: `c × (1 − a) + p × a`; *adds*: `c + p × a`. A light with `a0 = 0` is left untouched. Each layer reads the frame as the layers before it left it.

- **A colour**: `p` = the layer's colour, `a0 = 1`.
- **An effect**: `p` = `LightingEffects.Evaluate(kind, position, t, params, layerColour)` with `position` the key's own position on the keyboard (not the keyboard's place on the desk), a light with no position using the centre (0.5, 0.5); `params` = the layer's Speed, angle 0, DeadWare's default Spread, no colour list (rainbow) ; `a0 = 1`. `t` is the helper's effect clock, the one 3a's keyboard modes use.
- **Key fade**: for a light whose key was pressed, `a0 = max over its live presses of 1 − elapsed/duration`; `p` = the layer's colour.
- **Key ripple**: for a light with a position, `a0 = max over live presses and their origin lights of ring × (1 − e)`, where `e = elapsed/duration` (presses with `e ≥ 1` are over), `ring = max(0, 1 − |dist − e × √2| / 0.18)`, and `dist` is the straight-line distance between the light and the origin in the key map's 0..1 positions. A light with no position is untouched. Origins are the lights of the pressed key that have a position.

Several presses combine by the largest, not by adding. A press is a key going down. A key held down counts once: the repeats Windows sends while it is held are ignored (as the approved mockup behaves), which means the hook class also sees the key coming up, and for that purpose only.

"Which keys": absent = every light of the keyboard; a list = only those lights; an empty list = none (the layer does nothing and does not count as animating).

### The name guard

A painted key and each key in a layer's "which keys" list is saved as the light's number **with the light's name**. When the keyboard's light at that number has a different name, or the number is beyond the keyboard's lights, that entry is skipped, not deleted. The card says "N painted keys don't match this keyboard's keys any more and are skipped." (and the same for a layer's keys, on the layer), and the helper logs it once per change of the situation, not per frame. A keyboard that reports no names can't be painted or given layers at all (see the card), as in DeadLight, where unnamed lights are always skipped (`painter.ts:319-322`, `:348`): without names the guard would have nothing to check. Names are 1 to 128 characters.

### Key presses

- The helper learns of key presses from one low-level keyboard hook on a dedicated thread with its own message loop.
- A press is turned into "which lights" by a fixed table from the key's scan code and extended flag to the names OpenRGB gives lights ("Key: A", "Key: Number Pad 1", "Key: Right Control" and so on), matched without regard to case and with or without the "Key: " prefix. The table is DeadLight's (`shared/keyboard-input.ts:3-34`, about 105 keys under about 168 names), re-expressed for Windows scan codes. Its special cases are kept: the ISO backslash, number-pad Enter apart from Enter, right-hand Ctrl and Alt, the arrows, number-pad digits apart from the number row; the Windows keys are mapped, and so is `#` on an ISO board (DeadLight leaves it out); Fn, macro keys and brand-logo lights are deliberately unmapped. A key with no light does nothing.
- Live presses are kept for at most 3 s, at most 32 at a time.
- **Tick**: while lights are on and a found, painted keyboard has an effect layer that covers at least one key with strength above 0, the fast tick runs (extend `LightingPaint.Animating`). For fade and ripple layers the fast tick runs only while a press is still live for some layer's duration; a press wakes the loop at once rather than waiting out the slow tick, so the first frame of a reaction is not late by up to half a second.
- Presses are dropped, and the list cleared, when lights go off and when the hook is removed. The helper has no sleep or lock notifications today and 3b adds none: a press is gone after 3 s in any case.
- If the helper is not running as administrator, Windows does not give it keys typed into windows that are; reactions pause while such a window is in front. The guide says so.

### The key-press rule (for the builder and for every review)

1. **One small class owns the hook**, in one source file, and its only output is a "light pressed" record: a device key, light numbers, a time. The record type has no member that holds a scan code, virtual-key code, character or text; a test pins the shape of that type. (A test can't stop a number from being misused; that is for review, every time this file changes.)
2. **Inside the hook callback** the only things read are: the scan code, the extended flag, the "sent by software" flag, and whether the key went down or came up (Alt-combinations count the same as plain keys). The virtual-key code is never read. **A key event sent by software is ignored outright**, before anything else is looked at: that is how programs type text, and DeadWare has no business with it. The conversion to light numbers happens in the callback, against a table prepared beforehand for the keyboards that currently have a fade or ripple layer; a key that maps to no such light is dropped there and nothing about it leaves the callback.
3. **The one thing the class remembers besides live presses** is which keys that have a light are held down right now, each with the time it was last seen, so that a held key counts once and a key whose release was missed works again two seconds later. A key with no light is never remembered. It is cleared when the hook is removed or refreshed.
4. **Nothing about a key press is ever written** to a log, a file or the settings, and no pipe message describes a press. Log lines may say the hook was installed or removed and why, never which or how many keys. **One exception, decided by the owner on 2026-11-21:** the picture shows fades and ripples, so preview frames carry the colours a press caused. With a reaction on every key those colours follow what was pressed. They go only to a window that has subscribed to preview frames (the Lighting page while it shows), over the helper's pipe, which an elevated helper opens only to elevated processes (`HostPipeServer.cs:39-43`); they are never stored by either side. No other message may carry reaction colours.
5. **An allow-list, not a deny-list.** The hook file may call exactly the Windows functions needed to install and remove the hook, pass the event on, and run and stop its message loop (the builder lists them in the file; expected: `SetWindowsHookEx`, `UnhookWindowsHookEx`, `CallNextHookEx`, `GetMessage`, `PostThreadMessage`, `GetModuleHandle`, `GetCurrentThreadId`). A test reads the sources of `DeadWare.Devices` **and** `DeadWare.Core` and fails if the hook file imports any other function, if `SetWindowsHookEx` appears in any other file, or if any file mentions a function that reads keys or turns them into characters (`ToUnicode`, `ToUnicodeEx`, `ToAscii`, `MapVirtualKey`, `VkKeyScan`, `GetKeyState`, `GetAsyncKeyState`, `GetKeyboardState`, `GetKeyNameText`, `GetKeyboardLayout`, `GetRawInputData`, `RegisterRawInputDevices`, `Console.ReadKey`). This is the hub driver's command allow-list applied to the hook.
6. **The hook always passes the key on** and never blocks, changes or injects keys. The callback does only a table lookup and a hand-off, because Windows silently removes a slow hook.
7. **The hook exists only while needed**: lights are on, and a found, painted keyboard has a key fade or key ripple layer with strength above 0 and at least one key. It is removed as soon as that stops being true, and when the helper stops. Whether the helper has it installed is part of the helper's state, so the page can show the note truthfully and a test can assert it. Windows can remove a hook without saying so; while the hook is needed the helper removes and installs it again every five minutes, whatever was or wasn't typed, silently, so the refresh itself remembers and reveals nothing about typing.
8. Known limit, to state in the guide: for up to three seconds the helper's memory holds which lights were just pressed, and which keys are held; that is the feature. It is never more than that.

### Key shapes

OpenRGB reports where a key is, not how wide it is, and 3a draws equal keys. 3b adds a built-in table of the standard full-size layout: for each standard light name, its row, its left edge and its width in key units (ANSI and ISO variants where they differ). When a keyboard's named lights are found in that table, the large view and the desk picture draw those keys at the table's shapes; named lights the table doesn't know are drawn in a row of their own beneath, labelled by name. When the keyboard reports no names, or fewer than half its lights are in the table, 3a's equal-key drawing from the key map is used for everything, with labels where there are names. The label is the light's name without "Key: ", shortened to fit.

The table is for drawing and picking only. Ripple distance and the Wave effect keep using the key map's positions, as in DeadLight and 3a.

### The picture and preview frames

The colours the window shows (desk picture and large view) are the colours after stage 3, before correction and brightness: painted keys and layers show, as lit as they are set. Preview frames follow the extended fast-tick rule. The window must also show a static change (a key painted while nothing animates) at once: the helper sends one frame of per-light colours after every accepted change and on subscribing, while the page shows.

## Data

The per-device entry of `lighting.json` (3a's table) gains:

- `Keys`: light number → `Name`, `Color`. At most 4096 per device.
- `Layers`: list, at most 8, each: `Kind` (`color`, `effect`, `fade`, `ripple`), `Color`, `Effect` (`wave`, `breathing`, `spectrum`), `Speed` 0..100, `DurationMs` 100..3000, `Strength` 0..100, `Mix` (`cover`, `add`), `Lights` (absent = all; a list of number + name, at most 4096).
- `Looks`: list, at most 12, each: `Name`, and its own copy of `Mode`, `Keys`, `Layers`.
- `Look`: the name of the look last chosen or saved, if any.

Unknown kinds in a file are kept and skipped when painting, and refused in new writes, as DeadLight does. An entry with nothing set is removed, as in 3a. A change that would take the file past its size cap is refused in plain words ("There isn't room to save that. Delete a saved look first.") and nothing is lost. Re-keying needs nothing new for the table itself (the new members live inside the per-device entry), but must be covered by a test.

**Version.** The file is written as `Version` 3 **only while some device has `Keys`, `Layers`, `Looks` or `Look`**; otherwise by 3a's rule (2 while the table has entries, else 1). Reason, as in 3a: 0.20.0 ignores members it doesn't know and would drop them at its next save if the file still said 2. Checked against the 3a code: a file with a higher version is used for what is understood, never rewritten, saving is turned off and the page says "Your lighting settings were saved by a newer DeadWare, so this version won't save changes to them." (`LightingSettingsCodec.Finish`, `DeviceHost.cs:183-187`). 3b only adds members, so 0.20.0 and 0.19.0 read what they know from a version 3 file.

## Pipe

New requests, each validated by the helper, answered with a reply and then a state:

- set a keyboard's painted keys (the whole set, replacing the old one: it is small, and it makes Undo a plain "send the earlier set");
- set a keyboard's layers (the whole list);
- save a look (new, or over an existing name), choose a look, rename a look, delete a look.

Refused in plain words: an unknown device; a device that is not a keyboard or reports no key positions; a light number beyond the keyboard's lights; more than 8 layers or 12 looks; a look name that is empty, too long or taken; a look that doesn't exist.

The device as the window sees it gains: its painted keys, its layers, its looks' names, the current look and whether the keyboard has changed from it, how many painted keys and layer keys are skipped by the name guard, and the key shapes. The helper's state gains whether the key-press hook is installed. Slider-driven layer settings use the existing `Throttle`.

## Testing

- Core: the pinned compose case; identity with no paint and no layers over the existing paint tests; cover and add maths, no clamp between layers; fade at half-life (DeadLight expects (151,26,11), `painter-lighting.test.ts:42`); ripple ring, origins and expiry (`:58`); absent, empty and listed "which keys"; the name guard for paint and for layer keys, including a reordered keyboard (`:28`, `:53`); the scan-code table against DeadLight's cases (`keyboard-input.test.ts`); press list bounds (3 s, 32); key shapes table (no overlaps, every standard key present, fallback rule); validation bounds; version 3 round trip, a version 2 file loading unchanged, a file returning to 2 or 1 when the last paint, layer and look are removed; re-keying carries paint, layers and looks.
- Helper: each request accepted, refused and saved; reply before state; the hook installed and removed by rule 7 of the key-press rule, with a fake hook; software-sent keys ignored; a held key counted once; fast tick only while a press is live; a frame after a static change; lights-off clears presses and removes the hook.
- The key-press rule: the record-shape test and the allow-list source scan over `DeadWare.Devices` and `DeadWare.Core`.
- Page: no automated tests exist for WinUI; hand traces in each task report, then the owner's look.
- Every new test is seen failing for its stated reason first, or carries mutation evidence.

## Hardware check (owner; short, each item leaves a trace that can be read back)

1. Paint WASD red and the arrows yellow; save as look "Check A". Trace: the look in `lighting.json` with 8 keys.
2. Add a key ripple layer, type, watch the ring; save as "Check B". Trace: the look with one ripple layer.
3. Switch from "Check B" to "Check A" and back. Trace: `Look` in the file.
4. Remove the ripple layer; the helper's log shows the hook removed. Trace: the log line.
5. Quit and restart; the keyboard comes back as left. Trace: none needed beyond the lights.

## Left to the builder

- Names and shapes of classes, messages and JSON members; how fractions are carried through the layer stage; how the press list is shared between the hook thread and the paint tick; how a press wakes the loop.
- How the hook thread is started and stopped inside the helper's lifetime, and what happens if installing the hook fails (the layer simply doesn't react; the card says "DeadWare couldn't watch for key presses, so this layer won't react." and the helper logs why).
- The swatches beside the colour picker; drag-picking details; minimum sizes in the large view.
- The exact standard-layout table, and how ANSI and ISO are told apart (by which names the keyboard reports).

## To confirm when planning

- Confirmed by the builder check: the helper runs in the owner's own desktop session, where a low-level hook receives keys. The window starts it as a child process (`DeviceHostClient.cs:412-418`), and at logon a task with an interactive token starts DeadWare (`StartupLayout.cs:54-57`).
- Builder traps found by the same check: `DeviceTunings.Clone` (`DeviceTuning.cs:12-17`) and `LightingPaint.View` (`LightingPaint.cs:43-45`) copy member by member and would silently drop new members; a test must catch that. The preview's per-zone colour is the zone's first light (`LightingDeviceService.cs:211`): it must stay the base colour, or painting key 0 would colour the whole keyboard wherever keys aren't drawn. 0.19.0's behaviour with a newer file was checked for 3a (that design, lines 129-131), not again here.
- The real light names of the owner's Apex Pro, from a 3a hardware-check log or the owner's PC, since the test fixture uses "Key 0".."Key 111". The scan-code table and the shapes table both depend on real names; the fallbacks above cover a mismatch.

## Calls made in writing this up (for the owner to check)

1. A saved look keeps painted keys, lighting mode and layers; not brightness, colour correction or the Colour section. To give a look its own all-over colour, pick **All** and paint.
2. Looks are snapshots: changing the keyboard does not change the saved look until you press **Save changes**; the name shows "(changed)" meanwhile, and switching away asks first.
3. Up to 12 looks per keyboard.
4. The card gets a **Layers** line with **Edit layers…**, next to the mockup's **Paint keys…**; both open the same large view.
5. No on/off switch per layer (DeadLight has one): remove the layer or set its strength to 0.
6. An effect layer offers the effect and its Speed only; it always runs left to right in rainbow colours (Breathing uses the layer's colour).
7. Undo covers key colours only, and only until Done or a change of look.
8. The keyboard is drawn with real key widths from a built-in standard layout, in the large view and the desk picture; a keyboard the table doesn't fit falls back to today's equal keys.
9. A ripple layer on a keyboard nobody is typing on costs nothing: the fast tick runs only while a ripple is on its way.
10. Decided by the owner (2026-11-21): fades and ripples show on the page's keyboard too. So while the Lighting page is open, the window is told the colours of the keys, which follow what was pressed. Nothing else ever leaves the helper about a key press, and the note on the card says so.
11. A held key gives one fade or ripple, not a stream of them (DeadLight repeats). Keys "typed" by software, not by a person, give none.
12. The ripple's width and speed are DeadLight's, which is wider and rounder than the ring in the mockup.
13. A new layer starts as a key fade (as in the mockup), so adding one never blanks the keyboard.
14. After a rollback to 0.20.0 or 0.19.0 from a PC with paint, layers or looks saved, the older version runs the lights but won't save lighting changes (it says so); everything is back when the newer build runs again.

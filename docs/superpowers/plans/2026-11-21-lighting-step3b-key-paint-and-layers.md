# Lighting Step 3b (Key Painting, Layers, Saved Looks) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the owner paint single keys, stack up to 8 layers (a colour, an effect, key fade, key ripple) on the keyboard, and save named keyboard looks, with the keyboard drawn at real key shapes.

**Architecture:** A new stage in the helper's one paint path, between 3a's base frame and its tuning: key paint, then layers, carried as fractions to a single rounding. Key presses reach the paint tick as "light pressed" records from one low-level keyboard hook that exists only while a fade or ripple layer needs it. Paint, layers and looks live inside 3a's per-device entry of `lighting.json`, written as version 3 only while present. The window edits them in a large keyboard view that takes the desk picture's place.

**Tech Stack:** .NET 10, C#, xUnit 2.9.3, WinUI 3 + Win2D, System.Text.Json (the pipe is source-generated through `DeviceHostJsonContext`; the settings codec uses reflection), Win32 `WH_KEYBOARD_LL` by P/Invoke. Projects: `src/DeadWare.Core`, `src/DeadWare.Devices` (the helper), `src/DeadWare.Services` (holds `DeviceHostClient`, which sends any `HostMessage` through its generic `SendAsync`), `src/DeadWare.WinUI`; tests in `tests/DeadWare.Tests`, `tests/DeadWare.Devices.Tests`, `tests/DeadWare.App.Tests`.

**Spec:** `docs/superpowers/specs/2026-11-21-lighting-step3b-key-paint-and-layers-design.md` (approved by the owner 2026-11-21). Mockups, the reference for layout and wording: `docs/superpowers/specs/2026-11-21-lighting-step3b-mockups/key-painting.html` (Way 1 tab) and `layers.html`. Behavioural reference: `I:\DeadLight` (`shared/device-lighting.ts`, `shared/keyboard-input.ts`, `electron/painter.ts:290-371`, `tests/painter-lighting.test.ts`, `tests/keyboard-input.test.ts`); read it, never copy from OpenRGB, OpenLinkHub or liquidctl.

**For the directing session.** This build is directed from a fresh session that starts from this file (the owner's trial; `HANDOFF-3b.md` in `I:\DeadWare\.claude\worktrees\lighting-step3a\.superpowers\sdd\2026-11-20-lighting-step3a\` has the owner's standing rules: read it first). Branch `claude/lighting-step3b`, worktree `I:\DeadWare\.claude\worktrees\lighting-step3b`, stacked on `claude/lighting-step3a` (PR #141, unmerged at planning time: check `gh pr view 141 --repo DeadeyeDuncan/DeadWare` and rebase onto main if it has merged). Keep working files in `.superpowers/sdd/2026-11-21-lighting-step3b/` (copy `gate.sh`, `implementer-rules.md`, `reviewer-rules.md` from the 3a folder and change the paths, branch and plan path). Ask the owner which build mode to use before any building. Record the start time: the arc's cost is measured at the end with `uv run python scripts/arc_cost.py --since <start> --until <end>` in claude-memory-compiler. The final review is the saved workflow `whole-branch-review`, run from a top-model session with pinned SHAs.

## Global Constraints

- Work only in the worktree `I:\DeadWare\.claude\worktrees\lighting-step3b`. Never run git or dotnet in `I:\DeadWare` itself. Never launch `DeadWare.exe`, `DeadWare.Devices.exe`, OpenRGB or DeadLight. Workers run no mutating git; the controller commits. Never tag.
- With nothing painted, no layers and no looks, every painted colour and every saved file equals 3a's byte for byte (the new members are never written as nulls), and pipe messages differ only by new members at their defaults. Existing tests stay unchanged and green, except the version literals named in Task 1.
- `LightingPaint.Tune` is not edited. A device with no paint and no layers never goes through fractions.
- Lights off is black for a painted device whatever is painted or layered. A device in a hardware mode gets no frame; its saved paint, layers and looks are kept.
- `lighting.json`: written only by the helper, validated on read and write, 1 MiB cap on the written form.
- Pipe: a reply is written before the state its request caused; never raise a state inside `Apply`, set `raise=true`. The pipe `Version` stays 1; new members on existing records are optional trailing parameters.
- The fast tick (33 ms) runs only while lights are on and something moves: 3a's cases, an effect layer that covers at least one light with strength above 0, or a key press still live for some fade or ripple layer. A keyboard with a ripple layer and nobody typing stays on the slow tick.
- **The key-press rule** (spec, "The key-press rule", all 8 points) binds every task. In short: one file owns the hook; the callback reads only scan code, extended flag, sent-by-software flag and down/up; software-sent keys are ignored first; the only output is `LightPress`; nothing about a press is logged, saved or sent, except the colours it causes in preview frames; the hook exists only while needed.
- Limits, exact: 8 layers per keyboard; 12 looks per keyboard; look name 1 to 40 characters, compared ignoring case; 4096 painted keys and 4096 lights in a layer's list; light name 1 to 128 characters; reaction duration 100 to 3000 ms, default 600; strength 0 to 100; speed 0 to 100; live presses kept 3000 ms, at most 32.
- UI text, British spelling, exactly: `Saved looks`, `Save changes`, `Save as a new look…`, `Rename`, `Delete`, `(changed)`, `Key colours`, `Paint keys…`, `Layers`, `Edit layers…`, `Done`, `Undo`, `Pick keys`, `All`, `None`, `WASD`, `Arrows`, `Number row`, `F keys`, `Number pad`, `Remove paint`, `From the picked keys`, `From every key`, `+ Add a layer`, `What it does`, `A colour`, `An effect`, `Key fade`, `Key ripple`, `Colour`, `Effect`, `Speed`, `How long it lasts`, `Strength`, `How it mixes with what's under it`, `Covers it`, `Adds light to it`, `Which keys`, `All keys`, `Only the keys I pick`, `Pick keys…`, `Finished picking`. Longer sentences are quoted in the task that uses them.
- Core/Devices code style: terse, brace-tight, no spaces after commas (as `LightingPaint.cs`). WinUI style: conventional spacing, XML doc comments.
- Every new test is seen failing for its stated reason before the code that passes it, or carries mutation evidence in the task report.
- Before every commit: `dotnet build DeadWare.slnx -c Release` with 0 errors and no new warnings, and all three test projects with `--filter "FullyQualifiedName!~MeasurementTests"`.

## Review Focus

Inputs the spec implies and a person will hit; each is pinned by a test in the named task.

1. **The keyboard comes back with its lights in another order, or with fewer lights, while keys are painted and a layer lists keys.** Mismatched entries are skipped and kept, never light another key, are counted for the card, and are logged once per change. (Tasks 2, 5)
2. **Looks at their edges:** choosing a look that was just deleted, renaming to a name that differs only by case, saving a 13th look, saving when the file would pass 1 MiB. Each is refused in plain words and nothing is lost. (Tasks 1, 5)
3. **A reaction layer that can do nothing:** an empty key list, strength 0, every listed key skipped by the name guard, lights off. No hook, no fast tick, no error. (Tasks 2, 6)
4. **A flood of key events:** a key held for seconds, more than 32 presses inside 3 s, keys sent by software, keys with no light, presses arriving while the hook is being removed. One press per held key, the list stays bounded, nothing is thrown from the callback. (Task 6)
5. **A version 3 file meets other builds and odd content:** an unknown layer kind is kept and skipped; removing the last paint, layer and look writes version 2 or 1 again; a version 4 file is read as newer and never rewritten. (Task 1)

---

### Task 1: Paint, layers and looks in the per-device table

**Files:**
- Create: `src/DeadWare.Core/DeviceLayers.cs`
- Modify: `src/DeadWare.Core/DeviceTuning.cs` (`DeviceTuning`, `Clone`, `Normalize`, `Validate`, `WrittenVersion`), `src/DeadWare.Core/LightingModels.cs` (`CurrentVersion=3`), `src/DeadWare.Core/LightingSettingsCodec.cs` (`WrittenTooLarge` becomes `internal const`, for Task 5), `src/DeadWare.Core/DeviceHostProtocol.cs` (`DeviceHostJsonContext` only if the new types need registering)
- Test: `tests/DeadWare.Tests/DeviceLayersTests.cs` (new), `DeviceTuningTests.cs`, `LightingSettingsCodecTests.cs`, `LightingKeysTests.cs`, `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `LightingStoreTests.cs`

**Interfaces:**
- Produces (all in namespace `DeadWare.Core`):
  - `public sealed class PaintedKey{public string Name{get;set;}="";public string Color{get;set;}="#000000";}` (colour as `#RRGGBB`, parsed with `Rgb.TryParse`, as zone colours are)
  - `public sealed class LayerLight{public int Index{get;set;}public string Name{get;set;}="";}`
  - `public sealed class DeviceLayer{public string Kind{get;set;}="fade";public string Color{get;set;}="#38E9FF";public string Effect{get;set;}="wave";public int Speed{get;set;}=40;public int DurationMs{get;set;}=600;public int Strength{get;set;}=100;public string Mix{get;set;}="add";public List<LayerLight>? Lights{get;set;}}` (`Lights` null = every light; empty = none). These defaults are the spec's new layer.
  - `public sealed class DeviceLook{public string Name{get;set;}="";public DeviceMode? Mode{get;set;}public Dictionary<int,PaintedKey>? Keys{get;set;}public List<DeviceLayer>? Layers{get;set;}}`
  - `DeviceTuning` gains `public Dictionary<int,PaintedKey>? Keys{get;set;}`, `public List<DeviceLayer>? Layers{get;set;}`, `public List<DeviceLook>? Looks{get;set;}`, `public string? Look{get;set;}`, each marked `[JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]` (as are `DeviceLook.Mode/Keys/Layers` and `DeviceLayer.Lights`), so an entry without them is written exactly as 3a writes it
  - `public static class DeviceLayers`: `const int MaxKeys=4096,MaxLayers=8,MaxLooks=12,MaxLookName=40,MaxLightName=128`; `const string Color="color",Effect="effect",Fade="fade",Ripple="ripple",Cover="cover",Add="add"`; `static readonly string[] Kinds=[Color,Effect,Fade,Ripple]`, `Effects=["wave","breathing","spectrum"]`, `Mixes=[Cover,Add]`; `static bool Has(DeviceTuning t)` (any of Keys, Layers, Looks non-empty or Look set); `static Dictionary<int,PaintedKey>? Clone(Dictionary<int,PaintedKey>? k)`; `static List<DeviceLayer>? Clone(List<DeviceLayer>? l)`; `static DeviceLook Clone(DeviceLook l)`; `static bool Same(DeviceTuning t,DeviceLook look)` (Mode, Keys and Layers equal by value; a null and an empty `Keys` or `Layers` are equal; **a layer's `Lights` null (all keys) and empty (no keys) are not equal**); `static DeviceLook? Find(DeviceTuning t,string name)` (ignoring case, trimmed); `static bool Changed(DeviceTuning t)` (`Look` names a saved look and `!Same`); `static string? ValidateSetup(DeviceMode? mode,Dictionary<int,PaintedKey>? keys,List<DeviceLayer>? layers)`; `static string? CheckNew(List<DeviceLayer>? layers)` (a sentence when a kind, effect or mix is not in the lists above; used for requests, not for files)
  - `LightingSettings.CurrentVersion=3`; `DeviceTunings.WrittenVersion`: `s.Version>CurrentVersion?s.Version:s.Devices.Values.Any(t=>t is not null&&DeviceLayers.Has(t))?3:s.Devices.Count>0?2:1`

- [ ] **Step 1: Write the failing tests.**
  - `DeviceLayersTests`:
    - `Same_treats_null_and_empty_alike_and_compares_by_value`.
    - `Changed_only_when_a_saved_look_is_named_and_differs`.
    - `Find_ignores_case_and_outer_spaces`.
    - `CheckNew_refuses_unknown_kind_effect_and_mix` with "That layer isn't of a kind DeadWare knows."
    - `ValidateSetup_bounds`: each of these gives a sentence, and the limits themselves give null: 9 layers ("A keyboard can have up to 8 layers."); 4097 keys ("Too many painted keys."); a key index -1 ("A painted key is invalid."); a null `PaintedKey`; a name of 0 or 129 characters ("A light's name must be 1 to 128 characters."); a colour that `Rgb.TryParse` refuses ("A colour is invalid."); strength -1 and 101 ("Layer strength must be 0 to 100."); duration 99 and 3001 ("A layer must last 0.1 to 3 seconds."); speed -1 and 101 ("Layer speed must be 0 to 100."); a `Kind`, `Effect` or `Mix` that is null or longer than 32 characters ("A layer is invalid."); a null layer; a layer `Lights` with 4097 entries or a negative index ("A layer's keys are invalid."). **An unknown but well-formed kind such as `"morrowind"` gives null** (kept in files).
  - `DeviceTuningTests`:
    - `Normalize_drops_empty_paint_layers_and_looks`: empty `Keys`, `Layers` and `Looks` become null; a `Look` naming no saved look becomes null; an entry left with nothing is removed; an entry with only `Keys` stays.
    - `Validate_looks`: 13 looks ("A keyboard can have up to 12 saved looks."); a name of 0 or 41 characters or only spaces ("A look's name must be 1 to 40 characters."); two looks "Gaming" and "gaming" ("Two looks have the same name."); a look whose setup `ValidateSetup` refuses gives that sentence.
    - `Clone_copies_paint_layers_and_looks_deeply`: change every member of the clone, including a layer's `Lights` and a look's `Keys`; the original is untouched. Build the original by setting **every** property of `DeviceTuning`, `DeviceLayer`, `DeviceLook`, `PaintedKey` to a non-default value and compare by serialising both to JSON before the change, so a member added later and forgotten in `Clone` fails here.
    - `WrittenVersion_is_3_only_with_paint_layers_or_looks`: tuning only 2; one painted key 3; one layer 3; one look 3; `Version=4` stays 4; empty table 1.
  - `LightingSettingsCodecTests`:
    - `A_painted_file_is_written_as_version_3_and_read_back` (one key, one layer of each kind with a two-light list, one look, `Look` set; `Newer` false; equal after the round trip).
    - `Removing_the_last_paint_writes_version_2_again` and `..._version_1_when_no_entry_is_left`.
    - `A_version_2_file_loads_unchanged`.
    - `A_tuned_entry_without_paint_is_written_exactly_as_3a_wrote_it`: settings with one entry `{Brightness=70,Correction=100/88/112,Mode=wave 55}` write text with none of the member names `Keys`, `Layers`, `Looks`, `Look`, and equal to this golden text for the `Devices` member (take it from 3a's `A_tuned_file_is_written_as_version_2_and_read_back` output before changing any code, and paste it into the test).
    - `An_unknown_layer_kind_is_kept` (`"Kind":"morrowind"` survives read and write).
    - `Current_version_3_is_not_newer`; `A_version_4_file_is_newer_and_not_quarantined`.
    - `A_painted_table_over_the_size_cap_is_refused_on_write` (12 looks each with 8 layers of 4096-light lists: `Write` fails as it does today for an oversized file; assert the existing failure path, no new one).
  - **Existing tests that use version 3 as "a newer file" must move to version 4.** Most are escaped inside C# strings (`\"Version\":3`), so search for the digit, not the quotes: `grep -rnE 'Version[^0-9]{0,6}3([^0-9]|$)' tests`. Known at planning time: `DeviceHostTests.cs:526,542`, `LightingStoreTests.cs:60`, `LightingSettingsCodecTests.cs:127,137`, and `DeviceTuningTests`' `Version=3 stays 3`. These literal changes are the only edits allowed to existing tests in this task; list each in the report.
  - `LightingKeysTests`: `Remap_carries_paint_layers_and_looks` (an entry with all four new members under an old device key is found, equal, under the new key).
  - `DeviceHostTests`: `Other_changes_keep_paint_layers_and_looks` (seed `lighting.json` with a painted, layered entry with a look; send `SetBrightnessRequest(40)` and a `SetDeviceTuningRequest`; the saved file and the state's settings still hold all of it).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~DeviceLayers|FullyQualifiedName~DeviceTuning|FullyQualifiedName~LightingSettingsCodec|FullyQualifiedName~LightingKeys"`. Expected: the new tests fail to compile (types missing); after stubbing the types, each fails on its assertion.
- [ ] **Step 3: Implement** the types, `DeviceLayers`, and the changes to `Clone`, `Normalize`, `Validate` (call `ValidateSetup` for the entry and for each look) and `WrittenVersion`. `Dictionary<int,PaintedKey>` serialises with number keys as JSON property names; the settings codec serialises by reflection and needs nothing more; if the pipe's source generator needs it, register the new types on `DeviceHostJsonContext`.
- [ ] **Step 4: Run** the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 3b: paint, layers and looks in the per-device table, file version 3`.

---

### Task 2: The paint stage: key paint, layers, reactions

**Files:**
- Create: `src/DeadWare.Core/LightingLayers.cs`
- Modify: `src/DeadWare.Core/LightingPaint.cs` (`Paint`, the device overload of `Animating`, new `Shown`), `src/DeadWare.Core/KeyMaps.cs` (new `CanPaint`). The `DeviceView` overload of `Animating` is Task 5's.
- Test: `tests/DeadWare.Tests/LightingLayersTests.cs` (new), `LightingPaintTests.cs`

**Interfaces:**
- Consumes: Task 1's types and `DeviceLayers`.
- Produces:
  - `public sealed record LightPress(string DeviceKey,IReadOnlyList<int> Lights,double AtMs);` in `LightingLayers.cs`. **This is the key-press rule's record: it must never gain a member.**
  - `KeyMaps.CanPaint(LightingDevice d)`: a keyboard whose `KeyMaps.Of` is not null, has at least one position, and whose `LedNames` has one non-empty name per light.
  - `public static class LightingLayers`:
    - `static bool Active(LightingSettings s,LightingDevice d)` (`CanPaint` and the entry has `Keys` or `Layers`)
    - `static double[]? Compose(LightingSettings s,LightingDevice d,Rgb[] baseFrame,double tMs,IReadOnlyList<LightPress>? presses)`: null when not `Active`; else `3*baseFrame.Length` values, r,g,b per light, after key paint and layers, unclamped and unrounded
    - `static Rgb TuneExact(double r,double g,double b,DeviceTuning? t,int pageBrightness)`
    - `static Rgb Round(double r,double g,double b)` (clamp to 0..255, round half away from zero)
    - `static int SkippedKeys(LightingDevice d,DeviceTuning? t)`; `static IReadOnlyList<int> SkippedLayerLights(LightingDevice d,DeviceTuning? t)` (one count per layer)
    - `static bool EffectLayerRuns(LightingSettings s,LightingDevice d)`; `static bool NeedsKeys(LightingSettings s,LightingDevice d)` (lights on, device painted (`Direct`, no hardware mode) and `CanPaint`, and a fade or ripple layer with strength above 0 that covers at least one light after the name guard); `static int LongestReactionMs(LightingSettings s,LightingDevice d)` (0 when `!NeedsKeys`)
  - `LightingPaint.Paint(LightingSettings settings,LightingDevice device,double tMs=0,IReadOnlyList<LightPress>? presses=null)`
  - `LightingPaint.Shown(LightingSettings s,LightingDevice d,double tMs,IReadOnlyList<LightPress>? presses)` : `Rgb[]`, the colours the window shows: `Lights` when not `Active`, else `Compose` through `Round`
  - `LightingPaint.Animating(LightingSettings s,IEnumerable<LightingDevice> devices,IReadOnlyList<LightPress>? presses=null,double nowMs=0)`: 3a's result, or a painted device for which `EffectLayerRuns`, or a press `p` for a device with `nowMs-p.AtMs<LongestReactionMs`

**Maths (spec, "Layer maths"; DeadLight `painter.ts:279-312`, `:316-371`):**
- Key paint: light `i` takes `Keys[i].Color` when `Keys[i].Name` equals the light's name, else it is skipped.
- A layer covers every light when `Lights` is null, else the listed lights whose name matches. Kinds not in `DeviceLayers.Kinds` are skipped.
- Per covered light the layer gives colour `p` and amount `a0`; `a=a0*Strength/100.0`; cover: `c*(1-a)+p*a`; add: `c+p*a`; `a0==0` leaves the light untouched. A layer reads the frame as the earlier layers left it and writes after computing all its lights.
- `color`: `p`=its colour, `a0=1`. `effect`: `p=LightingEffects.Evaluate(kind,pos,tMs,q,layerColour)` with `kind` from an explicit map (`"wave"`→`EffectKind.Wave`, `"breathing"`→`Breathing`, `"spectrum"`→`Spectrum`; **not** `LightingEffects.KindOf`, which matches `Wave` by exact case and would fall back to Static), `pos` the light's key-map position as a `DeskPoint` (centre 0.5,0.5 when it has none) and `q=new EffectParams{Speed=layer.Speed}` (the class's own defaults for angle and spread, an empty colour list); `a0=1`.
- `fade`: `a0` = the largest `1-(tMs-p.AtMs)/DurationMs` over presses for this device that list this light, counting only `0<=tMs-p.AtMs<DurationMs`.
- `ripple`: for a light with a position, `a0` = the largest `ring*(1-e)` over presses and their origin lights that have a position, `e=(tMs-p.AtMs)/DurationMs` with `0<=e<1`, `ring=Math.Max(0,1-Math.Abs(dist-e*Math.Sqrt(2))/0.18)`, `dist` the straight-line distance in key-map positions. A light with no position is untouched.
- `TuneExact`: per channel `Math.Round(Math.Min(255,Math.Max(0,v*gain/100.0))*device/100.0*page/100.0,MidpointRounding.AwayFromZero)`, with `device`, `page` and the gains read as `Tune` reads them. In `Paint`, a device with `Compose==null` keeps today's loop over `Tune`, untouched.

- [ ] **Step 1: Write the failing tests.** Build devices inline as `LightingPaintTests.cs:181` does, type 5, one zone with a matrix, `LedNames` given.
  - `The_DeadLight_compose_case` : lights "Key: A" at (0,0) and "Key: B" at (1,0) (`new ZoneMatrix(2,1,[0,1])`); zone colour (101,51,21); page brightness 50; device brightness 50; correction 200/100/50; key 0 painted (200,0,0); layer 1 color (100,100,100) strength 50 cover, all; layer 2 color (20,20,20) strength 50 add, lights `[{0,"Key: A"}]`. `Paint(...).Colors` equals `[(64,15,8),(50,19,8)]`.
  - `Layers_are_not_clamped_between_them`: add (255,255,255) at 100 then cover with black at 50 on base (200,200,200) gives 228 per channel after rounding, not 128.
  - `TuneExact_equals_Tune_for_whole_numbers`: for every byte value 0..255 and each of the tunings {none}, {brightness 37}, {correction 200/100/50}, {brightness 70, correction 88/112/100}, and page brightness 0, 33, 50, 100: equal to `Tune`.
  - `Nothing_painted_is_the_byte_path`: `Compose` returns null for a keyboard with a tuned entry but no keys or layers, for a non-keyboard with keys in its entry, and for a keyboard with no names.
  - `Fade_at_half_life`: port DeadLight `tests/painter-lighting.test.ts:42-46` with its inputs; expected (151,26,11).
  - `Ripple_origins_and_expiry`: port `:58-62`.
  - `Layer_keys_absent_empty_and_listed`, `A_reordered_keyboard_skips_paint_and_layer_keys`: port `:28-32` and `:53-56`; also assert `SkippedKeys` and `SkippedLayerLights`.
  - `Presses_combine_by_the_largest`: two presses on one light at different ages give the larger amount, not the sum.
  - `Each_effect_name_runs_its_own_effect`: wave, breathing and spectrum layers at one time and position give three results that each equal `LightingEffects.Evaluate` for that `EffectKind`, and none equals the Static result.
  - `An_effect_layer_uses_key_positions`: a wave layer gives different colours at x=0 and x=1 and the same colour for two lights at one x; a light with no position equals one at (0.5,0.5).
  - `Lights_off_is_black_with_paint_and_layers`.
  - `NeedsKeys_cases` (Review Focus 3): true for a ripple layer on all keys; false with strength 0, with `Lights=[]`, with every listed light mismatched, with lights off, with the device in a hardware mode, for a `color` layer.
  - `Animating_with_layers`: an effect layer makes it true with a static desk; a ripple layer alone false; with a press 100 ms old and duration 600 true; 700 ms old false; an effect layer with `Lights=[]` false.
  - `Shown_is_stage_three`: with correction 200/100/50 and brightness 10, `Shown` gives the painted key's own colour.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~LightingLayers|FullyQualifiedName~LightingPaint"`. Expected: new tests fail (missing members, then assertions); existing `LightingPaintTests` pass unchanged.
- [ ] **Step 3: Implement** `LightingLayers` and the `LightingPaint` changes. Build the key map once per `Compose` call.
- [ ] **Step 4: Run** all three test projects. Expected: pass.
- [ ] **Step 5: Commit** `Lighting 3b: key paint and layers in the paint path`.

---

### Task 3: From a key's scan code to its lights

**Files:**
- Create: `src/DeadWare.Core/KeyPressTable.cs`
- Test: `tests/DeadWare.Tests/KeyPressTableTests.cs` (new)

**Interfaces:**
- Produces: `public static class KeyPressTable`: `static (int Scan,bool Extended)? CodeFor(string lightName)`; `static IReadOnlyList<int> LightsFor(IReadOnlyList<string> lightNames,int scan,bool extended)` (indices of the lights whose name maps to that key; empty when none); `static IReadOnlyList<string> Unrecognised(IReadOnlyList<string> lightNames)`.

**The table.** Names are matched trimmed, ignoring case, with or without a leading `Key: `. Port every name of DeadLight `shared/keyboard-input.ts:10-34` (about 168 names). DeadLight's codes are libuiohook's; here they are Windows set-1 scan codes as `KBDLLHOOKSTRUCT` reports them. Conversion: a libuiohook code `0x0Exx` or `0xE0xx` is scan `xx` with the extended flag; any other code is the same scan, not extended. **Exceptions, decided here:**

| Key | Scan | Extended |
| --- | --- | --- |
| `\ (ISO)` | 0x56 | no |
| Pause (all its names) | 0x45 | no |
| Num Lock | 0x45 | yes |
| Right Shift | 0x36 | either (match both) |
| Print Screen | 0x37 | yes |
| Number-pad Enter | 0x1C | yes |
| Number-pad `/` | 0x35 | yes |
| Number-pad `*` | 0x37 | no |

Number-pad digits and `.` are not extended; Insert, Delete, Home, End, Page Up, Page Down and the arrows are the same scans with the extended flag. Left and Right Windows are 0x5B and 0x5C extended, Menu 0x5D extended, Right Control 0x1D extended, Right Alt 0x38 extended. `#` (the ISO key beside Enter) is scan 0x2B, not extended, the same key position as `\ (ANSI)`; DeadLight leaves it unmapped, and a keyboard has only one of the two. `Fn`, `Japanese Yen`, `OEM 102`, `_`, logo and macro lights map to nothing. Alt+Print Screen (0x54) and Ctrl+Pause (0x46 extended) are left unmapped on purpose.

- [ ] **Step 1: Write the failing tests:**
  - `Every_DeadLight_name_maps` (a `[Theory]` over the full name list with its expected scan and flag)
  - `Names_match_with_and_without_the_prefix_and_any_case`
  - `Number_pad_digits_are_apart_from_the_number_row`
  - `Number_pad_Enter_is_apart_from_Enter`
  - `The_ISO_and_ANSI_backslashes_are_apart`
  - `Pause_and_Num_Lock_are_apart`
  - `Right_Shift_matches_with_either_flag`
  - `Unmapped_names_map_to_nothing` (`Key: Fn`, `Key: Japanese Yen`, `Key: OEM 102`, `Key: _`, `Logo`, `Key: M1`)
  - `LightsFor_returns_every_light_with_that_name` (two lights named for one key both come back)
  - `Unrecognised_lists_the_names_that_map_to_nothing`
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~KeyPressTable`. Expected: fail.
- [ ] **Step 3: Implement** as a static dictionary built once.
- [ ] **Step 4: Run** the filter again. Expected: pass.
- [ ] **Step 5: Commit** `Lighting 3b: key press table from scan codes to light names`.

---

### Task 4: Real key shapes

**Files:**
- Create: `src/DeadWare.Core/KeyShapes.cs`
- Modify: `src/DeadWare.Core/DeskPicture.cs` (`KeyRects`)
- Test: `tests/DeadWare.Tests/KeyShapesTests.cs` (new), `DeskPictureTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record KeyShape(int Light,double X,double Y,double W,double H,string Label);` (key units; a standard key is 1 high)
  - `public sealed record KeyShapeSet(double Width,double Height,IReadOnlyList<KeyShape> Keys,bool Standard);`
  - `public static class KeyShapes`: `static KeyShapeSet? Of(KeyMapView map)`; `static string Label(string lightName)`; `static int? Hit(KeyShapeSet set,double x,double y)` (key units; the light under the point, else null); `static IReadOnlyList<int> Group(KeyMapView map,string group)` with groups `"all"`, `"wasd"`, `"arrows"`, `"numbers"`, `"fkeys"`, `"numpad"`.
- `DeskPictures.KeyRects(PicBox box,KeyMapView keys)` keeps its signature. When `KeyShapes.Of(keys)` is `Standard`, the rectangles are the shapes scaled to fit the inset box, aspect kept, centred, each rectangle then shrunk by 6 % of a key unit on every side so neighbours show a gap; otherwise today's code runs unchanged. In a `KeyShapeSet` itself neighbouring shapes touch; the gap exists only in what is drawn, and `Hit` works on the touching shapes.

**The standard layout** (key units; x from the left edge of Esc; rows 0 to 5 at y = 0, 1.25, 2.25, 3.25, 4.25, 5.25; three blocks: main at x 0, navigation at x 15.25, number pad at x 18.5; total width 22.5, height 6.25):
- Row 0: Escape 1 wide at x 0; F1-F4 from x 2; F5-F8 from x 6.5; F9-F12 from x 11; Print Screen, Scroll Lock, Pause in the navigation block.
- Row 1: `` ` ``, 1-0, `-`, `=` each 1; Backspace 2. Navigation: Insert, Home, Page Up. Pad: Num Lock, `/`, `*`, `-`.
- Row 2: Tab 1.5; Q-P, `[`, `]` each 1; `\ (ANSI)` 1.5. Navigation: Delete, End, Page Down. Pad: 7, 8, 9, `+` (1 wide, 2 high).
- Row 3: Caps Lock 1.75; A-L, `;`, `'` each 1; Enter 2.25 (with `#` present, an ISO board: `#` 1 wide and Enter 1.25). Pad: 4, 5, 6.
- Row 4: Left Shift 2.25 (ISO: 1.25, then `\ (ISO)` 1); Z-M, `,`, `.`, `/` each 1; Right Shift 2.75. Navigation: Up Arrow in the middle column. Pad: 1, 2, 3, Enter (1 wide, 2 high).
- Row 5: Left Control, Left Windows, Left Alt 1.25 each; Space 6.25; Right Alt, then one slot for Right Fn or Right Windows (when a keyboard has both, the first in light order takes the slot and the other goes to the extra row), Menu, Right Control 1.25 each. Navigation: Left, Down, Right Arrow. Pad: 0 2 wide, `.` 1.
- A keyboard is ISO when it has a light named `\ (ISO)` or `#`.
- Names are matched as in Task 3 (trimmed, any case, with or without `Key: `), with the same alternative names.
- `Of` returns a `Standard` set when at least half of the map's lights have a name in the layout. Lights with a name not in it go, in light order, in one extra row beneath (y = 6.75, 1 wide, from x 0, wrapping at the set's width). Lights with an empty name are left out of a standard set.
- Otherwise `Of` returns a non-standard set built from the key map's positions, the same geometry `KeyRects` uses today expressed in key units, or null when no light has a position.
- `Label`: the name without `Key: `, then shortened by a fixed table (`Escape`→`Esc`, `Backspace`→`Bksp`, `Caps Lock`→`Caps`, `Left Shift`/`Right Shift`→`Shift`, `Left Control`/`Right Control`→`Ctrl`, `Left Windows`/`Right Windows`→`Win`, `Left Alt`/`Right Alt`→`Alt`, `Print Screen`→`PrtSc`, `Scroll Lock`→`ScrLk`, `Pause/Break`→`Pause`, `Insert`→`Ins`, `Delete`→`Del`, `Page Up`→`PgUp`, `Page Down`→`PgDn`, `Up Arrow`→`↑`, `Down Arrow`→`↓`, `Left Arrow`→`←`, `Right Arrow`→`→`, `Number Pad N`→`N`, `Number Pad Enter`→`Ent`, `Num Lock`→`Num`, `\ (ANSI)` and `\ (ISO)`→`\`), else the first 5 characters.
- `Group`: `wasd` = the lights named W, A, S, D; `arrows` = the four arrows; `numbers` = 1 to 0 of the number row; `fkeys` = F1 to F12; `numpad` = every `Number Pad …` name and Num Lock; `all` = every light in the set.

- [ ] **Step 1: Write the failing tests:**
  - `A_full_ANSI_board_is_standard_and_no_keys_overlap` (104 standard names; every pair of shapes disjoint; Space is 6.25 wide; the pad's `+` and Enter are 2 high)
  - `An_ISO_board_has_the_short_shift_and_the_extra_key`
  - `Unknown_named_lights_go_in_a_row_beneath`
  - `Fewer_than_half_recognised_falls_back_to_positions`
  - `Unnamed_lights_fall_back_to_positions`
  - `No_positions_and_no_names_gives_null`
  - `Labels` (the table above, and a 9-character unknown name cut to 5)
  - `Hit_finds_the_key_and_misses_outside_the_board_and_between_blocks`
  - `Groups` (each group on the ANSI board; a board with no number pad gives an empty `numpad`)
  - In `DeskPictureTests`: `KeyRects_uses_real_shapes_for_a_named_board` (Space's rectangle is more than 5 times as wide as A's; all inside the box). The existing `KeyRects` tests, which use unnamed maps, pass unchanged.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~KeyShapes|FullyQualifiedName~DeskPicture"`. Expected: new tests fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** all of `DeadWare.Tests`. Expected: pass.
- [ ] **Step 5: Commit** `Lighting 3b: real key shapes from a built-in standard layout`.

---

### Task 5: The helper: requests, the view, frames for static changes

**Files:**
- Modify: `src/DeadWare.Core/DeviceHostProtocol.cs`, `src/DeadWare.Core/LightingPaint.cs` (`View`, the `DeviceView` overload of `Animating`, new `ShowsLights`), `src/DeadWare.Devices/DeviceHost.cs` (`Apply`, handlers, `PreviewLocked`), `src/DeadWare.Devices/LightingDeviceService.cs` (preview lights; `Shape`), `src/DeadWare.WinUI/LightingPage.cs` (the two places that drop or clear frames, `:425` and `:574`)
- Test: `tests/DeadWare.Tests/DeviceHostProtocolTests.cs`, `LightingPaintTests.cs`, `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `LightingDeviceServiceTests.cs`, `tests/DeadWare.App.Tests/DeviceHostClientTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces (records in `DeviceHostProtocol.cs`, each with its `[JsonDerivedType]` name):
  - `SetDeviceKeysRequest(string DeviceKey,Dictionary<int,PaintedKey>? Keys) : HostMessage` — `"setDeviceKeys"`; the whole set, null or empty clears
  - `SetDeviceLayersRequest(string DeviceKey,List<DeviceLayer>? Layers) : HostMessage` — `"setDeviceLayers"`
  - `SaveLookRequest(string DeviceKey,string Name,bool Overwrite=false) : HostMessage` — `"saveLook"`
  - `ChooseLookRequest(string DeviceKey,string Name) : HostMessage` — `"chooseLook"`
  - `RenameLookRequest(string DeviceKey,string Name,string NewName) : HostMessage` — `"renameLook"`
  - `DeleteLookRequest(string DeviceKey,string Name) : HostMessage` — `"deleteLook"`
  - `DeviceView` gains trailing `bool CanPaint=false,Dictionary<int,PaintedKey>? Paint=null,List<DeviceLayer>? Layers=null,IReadOnlyList<string>? Looks=null,string? Look=null,bool LookChanged=false,int SkippedKeys=0,IReadOnlyList<int>? SkippedLayerLights=null`
  - `HostState` gains trailing `bool KeyHook=false` (set in Task 6; always false here)
  - `HostReplyCodes` gains, with `Explain` text: `NotPaintable`="not paintable" → "That device isn't a keyboard DeadWare can paint key by key."; `BadKey`="bad key" → "That key isn't on this keyboard."; `BadLayer`="bad layer" → "That layer isn't of a kind DeadWare knows."; `TooManyLayers`="too many layers" → "A keyboard can have up to 8 layers."; `TooManyLooks`="too many looks" → "A keyboard can have up to 12 saved looks. Delete one first."; `LookName`="look name" → "A look's name must be 1 to 40 characters."; `LookTaken`="look taken" → "There is already a look with that name."; `NoLook`="no look" → "That look isn't saved any more."; `NoRoom`="no room" → "There isn't room to save that. Delete a saved look first."

**Behaviour:**
- Every handler follows 3a's `SetDeviceMode` handler (`DeviceHost.cs:455-472`): `Listed()` first (`UnknownDevice`), then `KeyMaps.CanPaint` (`NotPaintable`), then its own checks, then `Mutate` with `Normalize`. **The `CanPaint` gate applies only to requests that add something:** a non-empty `Keys`, a non-empty `Layers`, `SaveLook` and `ChooseLook`. Clearing paint, clearing layers, renaming and deleting a look work on any listed keyboard, so one that has lost its names or positions can still be cleaned up.
- Keys: an index at or beyond the device's light count → `BadKey`. More than 4096, a bad colour or a bad name → the `ValidateSetup` sentence through `Mutate`'s validation, as 3a's handlers surface a validation failure.
- Layers: more than 8 → `TooManyLayers`; `DeviceLayers.CheckNew` fails → `BadLayer`; a layer light index beyond the light count → `BadKey`.
- `SaveLook`: name trimmed; empty or over 40 → `LookName`; exists and `!Overwrite` → `LookTaken`; new and 12 already saved → `TooManyLooks`. It stores deep copies of the entry's `Mode`, `Keys`, `Layers` and sets `Look`.
- `ChooseLook`: unknown → `NoLook`; else the entry's `Mode`, `Keys`, `Layers` become deep copies of the look's and `Look` its name. A look whose mode the device can't run now is still chosen (3a's "saved mode that can't run" rule applies).
- `RenameLook`: unknown → `NoLook`; the new name trimmed, checked as in save; another look has it (ignoring case) → `LookTaken`; `Look` follows the rename. Renaming a look to its own name in another case is allowed.
- `DeleteLook`: unknown → `NoLook`; `Look` is cleared when it named that look; the entry's `Mode`, `Keys` and `Layers` are not touched.
- A change that would take the written file past 1 MiB is refused today inside `Mutate`, by `LightingSettingsCodec.Validate` returning the sentence `WrittenTooLarge` (`DeviceHost.cs:522`, `LightingSettingsCodec.cs:63,75`), before anything is saved or adopted. For these six requests, answer that one sentence as `NoRoom` instead; every other validation sentence is answered as today.
- `View`: fill the new members from the entry with deep copies; `Looks` = the names in saved order; `LookChanged` = `DeviceLayers.Changed`; `SkippedKeys` and `SkippedLayerLights` from `LightingLayers`. `View` builds a `DeviceView` positionally: use named arguments for the new members.
- Preview: `PreviewEvent.Lights` for a device comes from `LightingPaint.Shown` (the paint stage's result), not from `Lights`. `PreviewEvent.Colors` (per zone) stays the base colour, never a painted key's.
- **Frames for things that don't move.** Today the helper builds a frame only while `Animating` (`PreviewLocked`, `DeviceHost.cs:279`), and the window drops every frame and clears the shown one while its own `Animating` is false (`LightingPage.cs:425`, `:574`). A painted key under a static desk would never be drawn, and the window can't know a press is live. So:
  - `LightingPaint.ShowsLights(LightingSettings s,IEnumerable<DeviceView?> devices)`: lights on and some `Direct` device view has a non-empty `Paint` or `Layers`.
  - `LightingPaint.Animating(LightingSettings,IEnumerable<DeviceView?>)` also returns true for a `Direct` view with an effect layer of strength above 0 whose `Lights` is null or non-empty.
  - Helper: a `previewDue` flag is set, while the window is subscribed, by `SubscribePreviewRequest` and by every raised state. When it is set, the next `PreviewLocked` builds and returns a frame **bypassing the `Animating` check, the `PreviewGap` and `SameFrame`**, then clears the flag. The slow tick otherwise still sends none.
  - Window: at `:425` a frame is shown when the host is Running and `Animating(...)||ShowsLights(...)`; at `:574` the shown frame is cleared only when both are false. `ShowDeskColors` keeps using `Colors` for zones; per-key drawing uses `Lights`.
- `Shape` (`LightingDeviceService.cs:39-40`) names only counts, so a keyboard whose lights come back renamed or reordered raises no state today. Add a stable hash of each device's light names to it, so the skipped-key counts, the card and (Task 6) the press table follow.
- The name-guard log line, once per change of the count for a device, not per frame: `"{device name}: {n} painted or layered keys don't match this keyboard's keys any more and are skipped."`

- [ ] **Step 1: Write the failing tests.**
  - `DeviceHostProtocolTests`: add the six requests, a `DeviceView` with every new member set, and a `HostState` with `KeyHook=true` to `Messages()` (`Every_message_type_round_trips` then covers them); `Old_views_read_without_the_new_members` (3a JSON for a `DeviceView` and a `HostState` deserialises with the defaults).
  - `DeviceHostTests`, using the fixture keyboard (`FakeOpenRgbSession.FromFixture`, "SteelSeries Apex Pro", lights "Key 0".."Key 111"):
    - `Painting_keys_is_saved_replied_then_stated` (reply before state; file has the keys; version 3).
    - `Painting_replaces_the_whole_set` and `An_empty_set_clears_paint_and_returns_the_file_to_its_earlier_version`.
    - `A_key_beyond_the_keyboard_is_refused`.
    - `A_device_that_is_not_a_paintable_keyboard_is_refused` (the fixture's RAM).
    - `An_unlisted_device_is_refused`.
    - `Layers_are_saved_and_shown` (one of each kind).
    - `Nine_layers_are_refused`.
    - `An_unknown_layer_kind_is_refused_in_a_request_but_kept_from_a_file` (seed the file with `"morrowind"`, send a valid `SetDeviceLayersRequest` for another device or a `SetBrightnessRequest`: the unknown layer is still in the file).
    - `Looks_save_choose_rename_delete` (the whole cycle, asserting the entry, `Look`, `LookChanged` in the state after each step).
    - `Look_edges` (Review Focus 2): choose after delete → `NoLook`; rename to a taken name in another case → `LookTaken`; a look renamed to its own name in another case succeeds; a 13th look → `TooManyLooks`; save with an existing name and `Overwrite=false` → `LookTaken`, with `true` succeeds; a name of spaces → `LookName`.
    - `A_save_past_the_size_cap_is_refused_with_no_room_and_nothing_changes`.
    - `Deleting_the_current_look_keeps_the_keyboard_as_it_is`.
    - `A_reordered_keyboard_counts_skipped_keys_and_logs_once` (Review Focus 1: paint keys, give the fixture keyboard real names first (see Task 6's note), paint keys, swap the fake's light names for two lights, rescan; a state is raised, `SkippedKeys==2`, both still in the file, one log line after two more ticks).
    - `A_static_paint_change_sends_one_frame_while_subscribed` (inside the `PreviewGap` of an earlier frame and equal to it: still sent) and `..._none_when_not_subscribed`.
    - `A_static_painted_desk_sends_no_frames_on_later_ticks`.
    - `Subscribing_sends_a_frame`.
    - `Preview_lights_carry_painted_keys_and_zone_colours_stay_base` (paint light 0 red: `Lights[device]` starts `FF0000`; `Colors[zone]` is still the zone colour).
  - `LightingPaintTests`: `ShowsLights_cases` (paint → true; layers → true; lights off → false; a hardware-mode view → false; nothing → false; a null view skipped) and `The_view_overload_animates_for_an_effect_layer` (and not for `Lights=[]` or strength 0).
  - `DeviceHostClientTests` (in `tests/DeadWare.App.Tests`): one round trip of `SetDeviceKeysRequest` through `SendAsync`, as 3a's tuning test does.
  - `LightingPage.cs` has no automated tests: hand-trace both changed places in the report (a painted key under a static desk is drawn and survives the next state; lights off clears it; an unpainted static desk behaves as before).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~DeviceHostTests` and the protocol tests. Expected: fail.
- [ ] **Step 3: Implement.** The client needs no change: `DeviceHostClient.SendAsync` (`src/DeadWare.Services/DeviceHostClient.cs:280`) sends any `HostMessage`.
- [ ] **Step 4: Run** all three projects. Expected: pass.
- [ ] **Step 5: Commit** `Lighting 3b: helper requests for key paint, layers and looks`.

---

### Task 6: The key-press hook

**Files:**
- Create: `src/DeadWare.Devices/KeyPressGate.cs` (pure logic, no Win32), `src/DeadWare.Devices/KeyPressHook.cs` (**the only file that may import the hook functions**)
- Modify: `src/DeadWare.Devices/DeviceHost.cs` (need evaluation, presses into every paint, wake on press, state), `src/DeadWare.Devices/LightingDeviceService.cs` (`Paint`, `PaintDevice`, `Preview`, `PreviewLights` at `:118,143,201,220` take the presses), `src/DeadWare.Devices/Program.cs` (create and dispose the hook)
- Test: `tests/DeadWare.Devices.Tests/KeyPressGateTests.cs` (new), `KeyPressRuleTests.cs` (new), `DeviceHostTests.cs`

**Interfaces:**
- Consumes: `LightPress`, `LightingLayers.NeedsKeys`, `LightingLayers.LongestReactionMs`, `KeyPressTable.LightsFor`, `KeyPressTable.Unrecognised`, `LightingPaint.Paint/Shown/Animating` with presses.
- Produces, **all `internal`** (`AssemblyInfo.cs:3` already lets the test project see internals; nothing outside the helper may see a scan code):
  - `internal sealed class KeyPressGate`:
    - `KeyPressGate(Func<double> nowMs,Action? pressed=null)`
    - `void Prepare(IReadOnlyList<(string DeviceKey,IReadOnlyList<string> LightNames)> keyboards)` (replaces the table; an empty list also clears presses and held keys)
    - `void OnKey(int scan,bool extended,bool sentBySoftware,bool up)` (never throws)
    - `IReadOnlyList<LightPress> Live()` (presses younger than 3000 ms, at most the newest 32, oldest first)
    - `void Released()` (forgets held keys, keeps live presses); `void Clear()` (forgets both)
    - `const int MaxLive=32,LifeMs=3000,HeldMs=2000`
  - `internal interface IKeyPressSource : IDisposable{bool Installed{get;}bool Start(KeyPressGate gate);void Stop();}` — `Start` returns false when the hook can't be installed. **It takes the gate itself, not a delegate, so scan codes can go nowhere else.**
  - `internal sealed class KeyPressHook : IKeyPressSource` (dedicated background thread, `WH_KEYBOARD_LL`, message loop)
  - `DeviceHost` takes an optional `IKeyPressSource? keys=null` constructor parameter; with null it never watches keys.

**Behaviour:**
- `OnKey`, in this order: return if `sentBySoftware`; look the key up for each prepared keyboard with `KeyPressTable.LightsFor`; **if no keyboard has a light for it, return without remembering anything**; if `up`, forget the key as held and return; if the key is held and was last seen less than `HeldMs` ago, note the time and return (a held key's repeats; the time lets a key whose release was never seen, as when an administrator window swallowed it, work again 2 s after its last repeat); mark it held with the time; add one `LightPress(deviceKey,lights,nowMs())` per keyboard; then, outside the lock, call `pressed` once inside `try`/`catch`. Held keys are keyed by `(scan,extended)`. One private lock, held for a few instructions; **neither `OnKey` nor `pressed` ever takes the host's `gate` lock or `previewGate`.**
- `DeviceHost`: after every adopted settings change, device list change (including the names hash of Task 5), lights on or off, and change of the host's status, compute the keyboards to watch: those for which `LightingLayers.NeedsKeys` **and** at least one light name is a known key (`KeyPressTable.Unrecognised(names).Count<names.Count`). The source runs only while the host is Running with a session and that list is not empty. None: `Stop()` if started, `gate.Prepare([])`. Some: `gate.Prepare(them)`, and `Start(gate)` if not started. `Stop()` is also called in `RunAsync`'s `finally` (`DeviceHost.cs:158`). `HostState.KeyHook` = the source's `Installed`; a change raises a state. A failed start logs `"Key presses: couldn't watch for key presses ({reason})."` once and is tried again at the next evaluation.
- Every place that paints or builds a preview passes `gate.Live()`: the tick (`TickWork`) and the repaints at `DeviceHost.cs:310,425,539`, through `LightingDeviceService`. The clock value given to `Paint`, `Shown` and `Animating` is the one the gate stamps presses with (`nowMs` is the host's effect clock from its `TimeProvider`).
- **Waking the loop.** `pressed` completes a `TaskCompletionSource` created with `TaskCreationOptions.RunContinuationsAsynchronously` that the host loop awaits together with its `Task.Delay` (`Task.WhenAny`), and the loop makes a fresh one each turn. Never cancel the delay's token from `pressed`: a cancelled token runs the loop's continuation, the paint tick and its OpenRGB calls, on the hook thread, which lags all typing and makes Windows remove the hook.
- **Refresh.** While started, every 5 minutes by the host's `TimeProvider` (a fixed interval, whatever was or wasn't typed, so nothing about typing is remembered or revealed): `Stop()`, `gate.Released()`, `Start(gate)`. It writes no log line and raises no state unless `Installed` actually changes.
- Log lines allowed about keys, and no others: `"Key presses: watching, for {n} keyboard(s)."`, `"Key presses: not watching."`, the couldn't-watch line, and once per device-list change per keyboard `"{device name}: {recognised} of {total} light names are known keys; not known: {names}."` (light names are facts about the device, not about what was pressed).
- `KeyPressHook`: P/Invokes exactly `SetWindowsHookEx`, `UnhookWindowsHookEx`, `CallNextHookEx`, `GetMessage`, `PostThreadMessage`, `GetModuleHandle`, `GetCurrentThreadId` (their `W` forms count as the same names). The callback: when `nCode<0`, return `CallNextHookEx` at once; else read from `KBDLLHOOKSTRUCT` only `scanCode` and `flags` (extended = 0x01, sent by software = 0x02 or 0x10, up = 0x80); the struct declares the virtual-key field as padding (`private readonly uint _0`) so it can't be read by name. It calls `gate.OnKey` inside `try`/`catch` that swallows everything, then **always** returns `CallNextHookEx`. The thread is a background thread. `Stop` posts `WM_QUIT` to it and joins with a 2 s timeout; `Installed` is false from the moment `Stop` is called. The callback delegate is kept in a field for the hook's life.

- [ ] **Step 1: Write the failing tests.** The fixture keyboard's lights are "Key 0".."Key 111" (`LightingDeviceServiceTests.cs:392`), which are no known keys: tests that need presses first give the fake keyboard real names for the lights they use ("Key: A", "Key: W", "Key: Left Control", "Key: Right Control", and so on).
  - `KeyPressGateTests` (Review Focus 4):
    - `A_press_becomes_light_numbers_for_each_prepared_keyboard`
    - `A_key_with_no_light_leaves_nothing_and_is_not_remembered_as_held` (after it, `Live()` is empty; assert by a later prepared table that maps it: its next down counts)
    - `A_held_key_counts_once_until_released` (down, down, down, up, down gives two presses)
    - `A_key_whose_release_was_missed_counts_again_two_seconds_after_its_last_repeat`
    - `Software_sent_keys_are_ignored_before_anything_else` (a software-sent down does not mark the key held: a real down after it counts)
    - `Presses_expire_after_three_seconds`; `At_most_32_live_presses_newest_kept`
    - `Preparing_nothing_clears_presses_and_held_keys`; `Released_forgets_held_keys_and_keeps_presses`
    - `OnKey_never_throws` (unprepared gate; scan -1; a `pressed` action that throws)
    - `Left_and_right_keys_of_one_scan_are_held_apart` (0x1D plain and extended)
    - `OnKey_from_two_threads_keeps_the_list_bounded` (two threads, 10,000 calls each)
  - `KeyPressRuleTests`:
    - `The_press_record_holds_only_a_device_lights_and_a_time`: reflect `LightPress`; its public properties are exactly `DeviceKey:string`, `Lights:IReadOnlyList<int>`, `AtMs:double`, and it has no fields beyond their backing fields.
    - `The_hook_types_are_not_public`: `KeyPressGate`, `KeyPressHook`, `IKeyPressSource` are not public.
    - `Only_the_hook_file_imports_the_hook_functions`: read every `.cs` under `src/DeadWare.Devices` and `src/DeadWare.Core` (find the repository root by walking up from the test assembly to `DeadWare.slnx`). `SetWindowsHookEx` appears only in `KeyPressHook.cs`; the `DllImport`/`LibraryImport` entry points in `KeyPressHook.cs`, with a trailing `W` removed, are exactly the seven above; `.OnKey(` appears only in `KeyPressHook.cs`; `IKeyPressSource` appears only in `KeyPressHook.cs`, `KeyPressGate.cs`, `DeviceHost.cs` and `Program.cs`; no file contains any of `ToUnicode`, `ToAscii`, `MapVirtualKey`, `VkKeyScan`, `GetKeyState`, `GetAsyncKeyState`, `GetKeyboardState`, `GetKeyNameText`, `GetKeyboardLayout`, `GetRawInputData`, `RegisterRawInputDevices`, `Console.ReadKey` (so comments in those two projects must not use these words either; none does today); `KeyPressHook.cs` does not contain `vkCode`.
    - Mutation evidence for the scan: add a forbidden name in a scratch file under `src/DeadWare.Core`, see the test fail, remove it; say so in the report.
  - `DeviceHostTests`, with a fake `IKeyPressSource` that records `Start`/`Stop` and keeps the gate it was given, so the test presses keys with `gate.OnKey`:
    - `The_hook_starts_with_a_ripple_layer_and_stops_without_one` (`KeyHook` in the state follows)
    - `The_hook_stops_when_lights_go_off_and_presses_are_cleared`
    - `The_hook_stops_when_the_host_stops_running`
    - `A_layer_that_can_do_nothing_starts_no_hook` (strength 0; `Lights=[]`; a keyboard none of whose light names is a known key)
    - `A_keyboard_in_a_hardware_mode_starts_no_hook`
    - `A_press_runs_the_fast_tick_until_the_reaction_ends` (ripple 600 ms on a static desk: slow tick before, fast for 600 ms after the press, slow again)
    - `A_press_wakes_the_loop_at_once` (a frame with the pressed light changed is sent without advancing the clock by the slow tick)
    - `A_press_never_runs_the_tick_on_the_callers_thread` (call `gate.OnKey` from the test thread with a fake session whose `UpdateLeds` records its thread: it is never the test thread)
    - `A_press_reaches_the_preview_lights_and_nothing_else` (after a press, no `StateEvent` and no log line differs from before, apart from preview frames)
    - `A_renamed_keyboard_gets_a_new_press_table` (swap two light names, rescan: the old key no longer lights the old light)
    - `The_hook_is_refreshed_every_five_minutes_silently` (`Stop` then `Start` recorded; no log line, no state; held keys forgotten, live presses kept)
    - `A_hook_that_cannot_start_is_reported_and_retried` (`KeyHook` false, one log line, `Start` called again after the next settings change)
    - `Without_a_key_source_nothing_is_watched`
    - `The_log_never_names_a_pressed_key` (after presses on several keys the log's new lines are only the allowed ones)
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~KeyPress|FullyQualifiedName~DeviceHostTests"`. Expected: fail.
- [ ] **Step 3: Implement** `KeyPressGate`, then the `DeviceHost` and `LightingDeviceService` changes against the fake, then `KeyPressHook` and its creation in `Program.cs` (disposed with the host). `KeyPressHook` has no automated test beyond the source scan: it cannot be run by an agent. Trace it by hand in the report: thread start, install, `nCode<0`, callback, stop while a callback is running, the delegate's lifetime.
- [ ] **Step 4: Run** all three projects. Expected: pass.
- [ ] **Step 5: Commit** `Lighting 3b: key presses from a low-level hook that exists only while a layer needs it`.

---

### Task 7: The large keyboard view and key painting

**Files:**
- Create: `src/DeadWare.Core/KeyPick.cs`, `src/DeadWare.WinUI/KeyboardEditor.cs`
- Modify: `src/DeadWare.WinUI/DeskPictureView.cs` (a third view), `src/DeadWare.WinUI/LightingPage.cs` (entering and leaving the view, the card)
- Test: `tests/DeadWare.Tests/KeyPickTests.cs` (new)

**Interfaces:**
- Consumes: `KeyShapes`, `DeviceView.CanPaint/Paint/Keys`, `SetDeviceKeysRequest`, `PreviewEvent.Lights`.
- Produces:
  - `public sealed class KeyPick` (Core): `IReadOnlySet<int> Picked{get;}`; `void Down(int light)` (starts a drag: adds when the light was not picked, removes when it was, and remembers which); `void Over(int light)` (continues the drag the same way); `void Up()`; `void Set(IEnumerable<int> lights)`; `void Clear()`.
  - `public sealed class PaintHistory` (Core, same file): `void Push(Dictionary<int,PaintedKey>? before)` (deep copy; keeps the newest 50); `bool CanUndo{get;}`; `Dictionary<int,PaintedKey>? Pop()`; `void Clear()`.
  - `PictureView.Keys` (enum value beside `Desk` and `Inside`); on `DeskPictureView`: `string? KeysDevice{get;set;}`, `IReadOnlySet<int>? PickedKeys{get;set;}`, `bool PickingKeys{get;set;}`, events `KeyDown(int light)`, `KeyOver(int light)`, `KeyUp()`.
  - `KeyboardEditor` (WinUI): builds the card for the large view; `enum EditorPart{KeyColours,Layers}`; raises requests through the page's existing send path.

**Behaviour (mockup `key-painting.html`, Way 1; spec "The large keyboard view"):**
- Entering: `Paint keys…` or `Edit layers…` on the keyboard's card sets `picture.View=PictureView.Keys` through `ShowPictureView` (`LightingPage.cs:815-824`), with the header "Painting keys" or "Layers", the two-part switch **Key colours | Layers**, and **Done**. Leaving: Done, selecting another device, leaving the page, or the keyboard disappearing from the state. Leaving clears `PaintHistory` and `KeyPick`.
- Drawing: `KeyShapes.Of(device.Keys)` scaled to the picture area, aspect kept; each key a rounded rectangle filled with its live colour (`frame.Lights` as the desk picture does, kept by Task 5's rule while anything is painted or layered; else the zone's colour), its `Label` in white or black by the fill's brightness, a 3 px accent outline when picked. Lights off draws the keys unlit; the helper not running dims as the desk does.
- Pointer: pressed on a key → `KeyDown`; moved over another key while pressed → `KeyOver`; released or capture lost → `KeyUp`. Hit test with `KeyShapes.Hit`.
- Card, Key colours part: the line "Click a key to pick it, click again to drop it. Drag across keys to pick several."; the group buttons (a group that `KeyShapes.Group` returns empty for is not shown); a `ColorPicker` in a `Flyout` as the Colour section uses (`NewPicker()`, `LightingPage.cs:1448`) behind a "Colour for the picked keys" button, plus nine swatches (`#FF3B3B #FF8A2B #FFD23B #22E04A #38E9FF #5B9DFF #B46BFF #FF5BD0 #FFFFFF`); `Remove paint` with its two buttons; the line "{n} keys painted · {m} picked"; **Undo**, enabled by `CanUndo`.
- Painting: choosing a colour with keys picked pushes the current paint to `PaintHistory`, builds the new whole set (each picked light with its name from `device.Keys.Names`) and sends `SetDeviceKeysRequest` through a `Throttle` (the colour picker fires continuously; push to history once per picker opening, not per change). The picked keys stay picked. A refusal shows the helper's reason as the page's other refusals do and restores the paint from the state.
- Undo sends the popped set. Choosing a look clears the history.
- When `device.SkippedKeys>0`, the card shows "{n} painted keys don't match this keyboard's keys any more and are skipped."

- [ ] **Step 1: Write the failing tests** in `KeyPickTests`: `A_click_picks_and_a_second_click_drops`; `A_drag_from_an_unpicked_key_adds_every_key_it_crosses`; `A_drag_from_a_picked_key_removes`; `Over_without_Down_does_nothing`; `Set_replaces_and_Clear_empties`; `History_returns_states_newest_first_and_copies_them` (changing the dictionary after `Push` does not change what `Pop` returns); `History_keeps_the_newest_50`; `Pop_on_empty_returns_null_and_CanUndo_is_false`.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~KeyPick`. Expected: fail.
- [ ] **Step 3: Implement** `KeyPick` and `PaintHistory`; run the filter; expected pass.
- [ ] **Step 4: Implement** the view, the editor card's Key colours part, and the page wiring. Keep `LightingPage.cs` changes to wiring; the card's content lives in `KeyboardEditor.cs`.
- [ ] **Step 5: Hand trace** in the report, with file:line for each: entering and leaving by each of the four routes; a click, a drag-add and a drag-remove; a colour chosen with the picker held down (one history entry, throttled sends, final value sent on close); Undo after two paints; a refused request; the keyboard unplugged while the view is open; lights off.
- [ ] **Step 6: Build and run** all three projects. Expected: 0 errors, no new warnings, pass.
- [ ] **Step 7: Commit** `Lighting 3b: the large keyboard view and key painting`.

---

### Task 8: The layers editor

**Files:**
- Modify: `src/DeadWare.WinUI/KeyboardEditor.cs`, `src/DeadWare.WinUI/LightingPage.cs` (wiring only)
- Create: `src/DeadWare.Core/LayerEdits.cs`
- Test: `tests/DeadWare.Tests/LayerEditsTests.cs` (new)

**Interfaces:**
- Consumes: `DeviceView.Layers/SkippedLayerLights`, `HostState.KeyHook`, `SetDeviceLayersRequest`, `KeyPick`, `KeyShapes.Group`, `DeviceLayers`.
- Produces: `public static class LayerEdits` (Core; every method returns a new deep-copied list and never changes its input): `static List<DeviceLayer> Add(List<DeviceLayer>? layers)` (appends `new DeviceLayer()`; unchanged at 8); `Remove(layers,int i)`; `Move(layers,int i,int by)` (by is -1 or +1; unchanged at the ends); `With(layers,int i,Action<DeviceLayer> change)`; `static string Title(DeviceLayer l)` ("Key ripple · all keys", "A colour · 4 keys", "An effect · no keys"; an unknown kind is "A layer this version doesn't know"); `static bool Reacts(List<DeviceLayer>? layers)` (any fade or ripple).

**Behaviour (mockup `layers.html`; spec "Layers"):**
- The list with ↑, ↓, ✕ per row; `+ Add a layer` disabled at 8; "Lower in the list is on top. Up to 8." Selecting a row shows its settings; a layer of an unknown kind shows only its title and ✕.
- Settings in the mockup's order. `What it does`: four choices with the mockup's one-line description under them. `Colour`: shown for A colour, Key fade, Key ripple, and for An effect when the effect is Breathing. `Effect` (Wave, Breathing, Spectrum) and `Speed` 0..100: An effect only. `How long it lasts`: fade and ripple, 0.1 s to 3.0 s in steps of 0.1 s, shown as "0.6 s". `Strength` 0..100 %. Mixing and `Which keys` as the mockup.
- Sliders are `TuningSlider`s (3a) so they throttle, flush on release and hold against incoming states. Every change sends the whole list with `SetDeviceLayersRequest`.
- `Pick keys…` puts the large keyboard into picking for that layer: `KeyPick` is set from the layer's lights, the group buttons appear, each pick change sends the layer's new list (index and name per light), and the button reads `Finished picking`. Switching to another layer, to Key colours, or Done ends picking. Choosing `All keys` sets `Lights` to null; choosing `Only the keys I pick` on a layer with null starts with an empty list and picking on.
- The note, shown while `LayerEdits.Reacts`: the two paragraphs quoted in the spec under "Layers", exactly. When lights are on, the keyboard is painted, a reaction layer could do something, and `state.KeyHook` is false, show instead of the first paragraph: "DeadWare couldn't watch for key presses, so this layer won't react."
- When a layer's entry in `SkippedLayerLights` is above 0, its settings show "{n} of this layer's keys don't match this keyboard's keys any more and are skipped."

- [ ] **Step 1: Write the failing tests** in `LayerEditsTests`: `Add_appends_the_default_fade_and_stops_at_8`; `Remove_and_Move_keep_the_rest_in_order`; `Move_at_an_end_changes_nothing`; `Edits_never_change_their_input` (including a layer's `Lights`); `Titles` (each kind; all keys, n keys, no keys; unknown kind); `Reacts_only_for_fade_and_ripple`.
- [ ] **Step 2: Run** the filter `FullyQualifiedName~LayerEdits`. Expected: fail. **Step 3: Implement** `LayerEdits`; expected pass.
- [ ] **Step 4: Implement** the Layers part of `KeyboardEditor` and its wiring.
- [ ] **Step 5: Hand trace** in the report: add, reorder, remove; each setting for each kind; picking keys for a layer and ending it by each route; a strength slider dragged (throttle, flush, no jump back); the note appearing and going; a refused request.
- [ ] **Step 6: Build and run** all three projects. Expected: pass.
- [ ] **Step 7: Commit** `Lighting 3b: the layers editor`.

---

### Task 9: The keyboard's card and saved looks

**Files:**
- Modify: `src/DeadWare.WinUI/DeviceSections.cs`, `src/DeadWare.WinUI/KeyboardEditor.cs` (the same looks control on the editor card), `src/DeadWare.WinUI/LightingPage.cs` (wiring)
- Create: `src/DeadWare.Core/LookNames.cs`
- Test: `tests/DeadWare.Tests/LookNamesTests.cs` (new)

**Interfaces:**
- Consumes: `DeviceView.CanPaint/Keyboard/Paint/Layers/Looks/Look/LookChanged`, the four look requests.
- Produces: `public static class LookNames` (Core): `static string? Problem(string? name,IReadOnlyList<string> existing,string? renaming=null)` → null when usable, else "Give the look a name.", "A look's name can be up to 40 characters." or "There is already a look called {name}." (ignoring case; `renaming` is the look's current name, allowed to match itself); `static bool Unsaved(DeviceView d)` (`LookChanged`, or `Look` is null and the device has paint, layers or a lighting mode).

**Behaviour (spec "The keyboard's card", "Saved looks"; mockup `key-painting.html`, lower card):**
- Section order for a keyboard: Saved looks, Colour, Key colours, Lighting mode, Layers, Brightness, Colour correction. The three new sections are shown when `device.Keyboard`; gate on `Keyboard`, not on `Keys` (3a ruling).
- `Key colours`: "{n} keys painted" or "No keys painted", and `Paint keys…`. `Layers`: "{n} layers" or "No layers", and `Edit layers…`. When `!device.CanPaint` both buttons are disabled with "This keyboard doesn't report its key positions." (no `Keys`) or "This keyboard doesn't report its keys' names." A keyboard in its own hardware effect has the three sections disabled under 3a's note.
- `Saved looks`: a `ComboBox` of the looks, the current one selected and shown as "{name} (changed)" when `LookChanged`; with no looks, only `Save as a new look…`. `Save changes` is shown only when `LookChanged` and sends `SaveLookRequest(key,look,Overwrite:true)`.
- `Save as a new look…` and `Rename` open a `ContentDialog` with a text box; the primary button is disabled while `LookNames.Problem` is not null and its sentence shows under the box.
- Choosing a look when `LookNames.Unsaved(device)`: a `ContentDialog` "The keyboard's current setup isn't saved in a look. Switch and lose it?" with **Switch** and **Cancel**; Cancel puts the selection back.
- `Delete`: a `ContentDialog` "Delete the look {name}? The keyboard's lights stay as they are." with **Delete** and **Cancel**.
- A refused request shows the helper's reason and the control returns to the state.

- [ ] **Step 1: Write the failing tests** in `LookNamesTests`: `An_empty_or_blank_name_is_a_problem`; `A_41_character_name_is_a_problem_and_40_is_not`; `A_taken_name_in_another_case_is_a_problem`; `Renaming_a_look_to_its_own_name_in_another_case_is_fine`; `Unsaved_cases` (changed look; no look and paint; no look and a mode; no look and nothing → false; an unchanged look → false).
- [ ] **Step 2: Run** the filter `FullyQualifiedName~LookNames`. Expected: fail. **Step 3: Implement**; expected pass.
- [ ] **Step 4: Implement** the sections, the looks control (one builder used by `DeviceSections` and `KeyboardEditor`) and the dialogs.
- [ ] **Step 5: Hand trace** in the report: first look saved from nothing; choose with and without unsaved changes, and Cancel; Save changes; rename; delete the current look; each refusal; a keyboard that can't be painted; a keyboard in a hardware effect; a non-keyboard's card unchanged from 3a.
- [ ] **Step 6: Build and run** all three projects. Expected: pass.
- [ ] **Step 7: Commit** `Lighting 3b: the keyboard's card and saved looks`.

---

### Task 10: Guide, release note, version

**Files:**
- Modify: `docs/user-guide.md` (the Lighting chapter), the changelog and the release note files that Task 7 of the 3a plan touched for 0.20.0 (find them with `git show --stat 28d370a`), the version number where 0.20.0 is set
- Create: `docs/releases/` note for 0.21.0 in the form of the 0.20.0 one

- [ ] **Step 1:** Guide: painting keys (the large view, picking, groups, Undo); layers (the four kinds, order, strength, mixing, which keys); saved looks (what a look keeps and doesn't; "(changed)"; to give a look its own all-over colour, pick All and paint); **key presses** (what DeadWare notices and what it never does, in the note's words; a held key gives one reaction; keys sent by software give none; any keyboard's keys light this one; if DeadWare isn't running as administrator, reactions pause while an administrator window is in front; for up to three seconds the helper remembers which lights were just pressed, and nothing longer); rollback (an older DeadWare runs the lights from a file with paint, layers or looks but won't save lighting changes).
- [ ] **Step 2:** Release note and changelog for 0.21.0; version bumped to 0.21.0. No tag: the tag is the owner's.
- [ ] **Step 3:** Check every UI word in the guide against the Global Constraints list (`grep` each). Expected: identical spelling.
- [ ] **Step 4:** Build and run all three projects. Expected: pass.
- [ ] **Step 5: Commit** `docs: 0.21.0 guide and release notes for key painting, layers and saved looks`.

---

## After the tasks

1. Whole-branch review: the saved workflow `whole-branch-review` from a top-model session, with pinned `base` (the commit this branch started from, `423c271`, or main after a rebase) and `head`, this spec, and `controllerModel`. A verdict of INCOMPLETE is not a pass. After a fix wave, run it again over the fix range only. The review must attack the key-press rule point by point.
2. Push, open the pull request (base `claude/lighting-step3a` while PR #141 is open, else `main`), and report the link.
3. Hardware check by the owner, through an owner-approved elevated script (never launched by an agent). Short, each item leaves a trace:
   1. Paint WASD red and the arrows yellow; save as "Check A". Trace: the look in `lighting.json` with 8 keys.
   2. Add a key ripple layer, type, watch the ring on the keyboard and on the page; save as "Check B". Trace: the look with one ripple layer; `devices.log` has "Key presses: watching".
   3. Press Pause, Num Lock, right Shift, number-pad Enter and the ISO key if there is one, with a key fade layer on all keys: each lights its own key. Trace: the owner's word per key; `devices.log` has the "light names are known keys" line with its counts.
   4. Switch to "Check A". Trace: `Look` in the file; `devices.log` has "Key presses: not watching".
   5. Quit and restart; the keyboard comes back as left.
4. Measure the arc: `uv run python scripts/arc_cost.py --since <start> --until <end>` in claude-memory-compiler; keep the output beside the working files.

# Lighting Step 3a (Device Tuning and Keyboard Modes) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every painted device its own brightness and colour correction, give keyboards a lighting mode, and draw the keyboard in the desk picture key by key in live colours.

**Architecture:** The painter keeps one path: a per-light base frame (the device's mode, else today's zone colours), then correction, then device × page brightness with one rounding. Per-device settings live in a new table in `lighting.json`, written as version 2 only while the table has entries. The OpenRGB layer starts reading light names and zone matrices; the window gets a key map and per-light preview colours.

**Tech Stack:** .NET 10, C#, xUnit 2.9.3, WinUI 3 + Win2D, OpenRGB.NET 3.1.1, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-11-20-lighting-step3a-device-tuning-design.md` (approved 2026-11-20). Mockup: `docs/superpowers/specs/2026-11-20-lighting-step3a-mockups/device-cards.html`. Code map with file:line for everything named here: `.superpowers/sdd/2026-11-20-lighting-step3a/interface-map.md` (lines at commit 82f2709). Behavioural reference: `I:\DeadLight` (`shared/effects.ts`, `shared/keyboard-layout.ts`, `shared/device-lighting.ts`, `electron/painter.ts`, `tests/fixtures/controllers.json`); read it, never copy from OpenRGB, OpenLinkHub or liquidctl.

## Global Constraints

- Work only in the worktree `I:\DeadWare\.claude\worktrees\lighting-step3a`. Never run git or dotnet in `I:\DeadWare` itself. Never launch `DeadWare.exe`, `DeadWare.Devices.exe` or OpenRGB. Workers run no mutating git; the controller commits.
- With correction 100/100/100, device brightness 100 and no mode, every painted colour equals today's. Existing paint, effect, host and service tests stay unchanged and green.
- Lights off is black for a painted device whatever its tuning or mode.
- A device in a hardware mode gets no frame; its saved tuning is kept.
- `lighting.json`: written only by the helper, validated on read and write, 1 MiB cap on the written form.
- Pipe: a reply is written before the state its request caused; never raise a state inside `Apply`, set `raise=true`. The pipe `Version` stays 1; new members on existing records are optional trailing parameters.
- The fast tick (33 ms) and preview frames run only while lights are on and something animates (a desk effect or a device mode). Otherwise the slow tick.
- UI text, British spelling, exactly: `Lighting mode`, `Follow the desk effect`, `Static`, `Breathing`, `Colour cycle`, `Rainbow wave`, `Speed`, `Brightness`, `This device`, `Colour correction`, `Red`, `Green`, `Blue`, `Reset`. Longer sentences are quoted in the task that uses them.
- Core/Devices/Services code style: terse, brace-tight, no spaces after commas (see the map, section 6). WinUI style: conventional spacing, XML doc comments.
- Every new test is seen failing for its stated reason before the code that passes it, or carries mutation evidence in the task report.
- Before every commit: `dotnet build DeadWare.slnx -c Release` with 0 errors and no new warnings, and all three test projects with `--filter "FullyQualifiedName!~MeasurementTests"`.

## Review Focus

Inputs the spec implies and a person will hit; each is pinned by a test in the named task.

1. **A device is unplugged while its tuning is saved or being changed.** Its entry is kept, a request for it is refused in plain words, nothing else changes. (Task 4)
2. **A keyboard's matrix is odd:** a cell naming a light index beyond the light count, the same light in several cells, a one-row or one-column matrix, an all-empty matrix. No crash, no divide by zero, Rainbow wave offered only when at least one light has a position. (Task 2)
3. **Extreme tuning:** brightness 0, correction 0/0/0, correction 200 on a channel already at 255. Black or clamped, never an error, and the picture still shows the intended colour. (Tasks 2, 4)
4. **A version 2 file written by this build is read by this build after the last entry is removed,** and a version 1 file with a stray table is never produced. (Task 1)
5. **A keyboard with an animated mode switches to a hardware mode, or its mode can no longer run.** The fast tick and preview stop being driven by it, it is painted as Static or not at all, the saved mode is kept. (Tasks 2, 4)

---

### Task 1: Per-device settings table

**Files:**
- Create: `src/DeadWare.Core/DeviceTuning.cs`
- Modify: `src/DeadWare.Core/LightingModels.cs` (LightingSettings), `src/DeadWare.Core/LightingSettingsCodec.cs`, `src/DeadWare.Core/LightingKeys.cs`, `src/DeadWare.Devices/DeviceHost.cs` (`Clone` only)
- Test: `tests/DeadWare.Tests/DeviceTuningTests.cs` (new), `tests/DeadWare.Tests/LightingSettingsCodecTests.cs`, `tests/DeadWare.Tests/LightingKeysTests.cs`, `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `tests/DeadWare.Devices.Tests/LightingStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class DeviceCorrection{public int R{get;set;}=100;public int G{get;set;}=100;public int B{get;set;}=100;}`
  - `public sealed class DeviceMode{public string Kind{get;set;}="static";public int Speed{get;set;}=40;}`
  - `public sealed class DeviceTuning{public int? Brightness{get;set;}public DeviceCorrection? Correction{get;set;}public DeviceMode? Mode{get;set;}}` (null = default: 100, 100/100/100, follow the desk)
  - `LightingSettings.Devices` : `Dictionary<string,DeviceTuning>`, default empty; `LightingSettings.CurrentVersion=2`
  - `public static class DeviceTunings`: `const int MaxEntries=256`; `static readonly string[] Kinds=["static","breathing","cycle","wave"]`; `static DeviceTuning? Of(LightingSettings s,string deviceKey)`; `static Dictionary<string,DeviceTuning> Clone(Dictionary<string,DeviceTuning> t)` (deep); `static void Normalize(LightingSettings s)` (Brightness 100 becomes null, Correction 100/100/100 becomes null, an entry with all three null is removed; a null entry is left for `Validate` to report, never dereferenced); `static string? Validate(LightingSettings s)`; `static int WrittenVersion(LightingSettings s)` (`s.Version>LightingSettings.CurrentVersion?s.Version:s.Devices.Count>0?2:1`)

- [ ] **Step 1: Write the failing tests** in `DeviceTuningTests.cs`:
  - `Normalize_drops_defaults_and_empty_entries`: entry `{Brightness=100,Correction=100/100/100}` is removed; `{Brightness=70}` stays with `Correction` null.
  - `Validate_bounds`: brightness -1 and 101, a correction channel -1 and 201, speed -1 and 101, kind `"rainbow"`, a null entry, a key longer than `LightingSettingsCodec.MaxKeyLength`, and 257 entries each give a sentence; 0, 100, 200 and exactly 256 entries give null. Sentences: "Device brightness must be 0 to 100.", "Colour correction must be 0 to 200.", "Mode speed must be 0 to 100.", "Lighting mode is not known.", "Too many device entries."
  - `WrittenVersion_is_2_only_with_entries`: empty table 1; one entry 2; `Version=3` stays 3.
  - In `LightingSettingsCodecTests`: `A_tuned_file_is_written_as_version_2_and_read_back` (round trip of brightness 70, correction 100/88/112, mode wave speed 55; `Newer` false); `An_untuned_file_is_written_as_version_1` (written text contains `"Version": 1`, also after loading a version 2 file and removing its only entry); `A_version_1_file_loads_with_an_empty_table`; `A_null_table_is_defaulted`; add `Current_version_2_is_not_newer`; add `A_null_entry_is_quarantined_not_thrown` (`{"Version":2,"Devices":{"k":null}}` gives `Quarantine` true and an error, no exception); add `A_later_file_with_a_table_of_another_shape_is_still_read` (`{"Version":3,"Devices":7}` reads as Newer with an empty table, not quarantined).
  - **Existing tests that use version 2 as "a newer file" must move to version 3.** With `CurrentVersion=2` they would otherwise fail. Change the literal `"Version":2` to `"Version":3` at `LightingSettingsCodecTests.cs` :127, :137, :266, :271, :276, :281, :287, `DeviceHostTests.cs` :513, :529 and `LightingStoreTests.cs` :60. These are the only edits allowed to existing tests in this task; change nothing else in them, and list each in the report.
  - In `DeviceHostTests`: `Other_changes_keep_device_tuning` (seed `lighting.json` with one tuned entry, start the helper, send `SetBrightnessRequest(40)` and a `SetEffectRequest`: the saved file and the state still hold the entry). It fails until `DeviceHost.Clone` copies the table.
  - In `LightingKeysTests`: `Remap_moves_a_devices_tuning_with_its_key` (serial drops at the same location: the entry moves) and `Remap_counts_a_tuning_entry_as_stored` (a target key that only has a tuning entry blocks the rename, as the other tables do).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~DeviceTuningTests|FullyQualifiedName~LightingSettingsCodecTests|FullyQualifiedName~LightingKeysTests"` and `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~DeviceHostTests|FullyQualifiedName~LightingStoreTests"`. Expected: compile errors for the missing types, then failures.
- [ ] **Step 3: Implement.** `DeviceTuning.cs` as above. In the codec: `Finish` defaults a null `Devices` and calls `DeviceTunings.Normalize`; `Values` calls `DeviceTunings.Validate`; `NewerShape` also removes `Devices`; `Write` sets `settings.Version=DeviceTunings.WrittenVersion(settings)` after its newer-version refusal and before serialising (`Validate` must not change the object). `LightingKeys.Remap`: add `Devices.Keys` to `stored` and re-key the table. `DeviceHost.Clone` (`DeviceHost.cs` :102-108) copies `Devices` with `DeviceTunings.Clone`; without it every later change drops the table.
- [ ] **Step 4: Run** the same filters, then the whole `tests/DeadWare.Tests` and `tests/DeadWare.Devices.Tests` projects. Expected: all pass.
- [ ] **Step 5:** Report; the controller commits "Lighting 3a: per-device settings table, written as version 2 only when used".

### Task 2: Key map, keyboard modes and the tuned frame (Core)

**Files:**
- Create: `src/DeadWare.Core/KeyMaps.cs`, `src/DeadWare.Core/DeviceModes.cs`
- Modify: `src/DeadWare.Core/LightingPaint.cs`, `src/DeadWare.Core/LightingEffects.cs` (make `Wrap01` and `Dim` **public**: `DeadWare.Core` has no InternalsVisibleTo and the tests call them), `src/DeadWare.Core/DeviceHostProtocol.cs` (DeviceView members only)
- Test: `tests/DeadWare.Tests/KeyMapsTests.cs`, `tests/DeadWare.Tests/DeviceModesTests.cs` (new), `tests/DeadWare.Tests/LightingPaintTests.cs`

**Interfaces:**
- Consumes: Task 1's `DeviceTuning`, `DeviceTunings.Of`.
- Produces:
  - `public sealed record ZoneMatrix(int Width,int Height,IReadOnlyList<uint> Cells)` (row-major, `KeyMaps.Empty=0xFFFFFFFF` for no light); `LightingZone(int Index,string Name,int LedCount,ZoneMatrix? Matrix=null)`; `LightingDevice(...,int ActiveMode,IReadOnlyList<string>? LedNames=null)`.
  - `public sealed record KeyMapView(IReadOnlyList<string> Names,IReadOnlyList<double?> X,IReadOnlyList<double?> Y,double KeyWidth)` (one entry per light, positions 0..1 or null, `KeyWidth` as a fraction of the width).
  - `public static class KeyMaps`: `const int KeyboardType=5`; `bool IsKeyboard(LightingDevice d)`; `int LightCount(LightingDevice d)` (`d.LedNames?.Count`, the device's own total as OpenRGB reports it, else the sum of zone light counts); `KeyMapView? Of(LightingDevice d)` (null unless the device has exactly one zone, that zone has a matrix and its light count equals `LightCount(d)`; `X`/`Y` are the mean column and row of the light's cells divided by `Width-1` and `Height-1`, 0.5 on an axis of size 1, null for a light in no cell; cells naming an index at or beyond the light count are ignored; `Names[i]` from `LedNames` when present and of the right length, else `""`; `KeyWidth=Math.Clamp(minXSpacing*0.85,0.008,0.10)` where `minXSpacing` is the smallest positive gap between distinct X values, 0.04 when there is none); `bool SupportsWave(LightingDevice d)` (`IsKeyboard` and `Of(d)` has at least one position).
  - `public static class DeviceModes`: consts `Static="static",Breathing="breathing",Cycle="cycle",Wave="wave"`; `const double WaveLength=1.6`; `bool Animated(string? kind)` (breathing, cycle, wave); `string? CantRun(LightingDevice d,string kind)` (null when it can; "Lighting modes are for keyboards." for a non-keyboard; "This keyboard doesn't report its key positions." for wave without `SupportsWave`); `DeviceMode? Running(LightingSettings s,LightingDevice d)` (the saved mode when `CantRun` is null, else null); `Rgb[] Frame(LightingSettings s,LightingDevice d,DeviceMode mode,double tMs)` (one colour per light, before tuning; on a keyboard with several zones Static and Breathing use each zone's own `ZoneColor` for that zone's lights, Colour cycle is one hue for all, and Rainbow wave can't run). `Frame` builds the key map once per call, never per light; rebuilding it each frame is accepted (112 lights).
  - In `LightingPaint`: `Rgb[] Lights(LightingSettings s,LightingDevice d,double tMs)` (the base frame: `DeviceModes.Frame` when `Running` is not null, else each zone's `ZoneShown` repeated `LedCount` times); `Rgb Tune(Rgb c,DeviceTuning? t,int pageBrightness)`; `bool Animating(LightingSettings s,IEnumerable<LightingDevice> devices)`; `bool Animating(LightingSettings s,IEnumerable<DeviceView> devices)` (for a view: `Kind` is Direct, `LightingMode` is animated and `ModeProblem` is null); `Paint` uses `Lights` then `Tune`.
  - `DeviceView(...,IReadOnlyList<ZoneView> Zones,int Brightness=100,DeviceCorrection? Correction=null,DeviceMode? LightingMode=null,string? ModeProblem=null,bool Keyboard=false,bool Wave=false,KeyMapView? Keys=null)`; `LightingPaint.View` fills them (`LightingMode` is the saved mode even when it can't run; `ModeProblem` is `CantRun`'s sentence for it; `Keys` only for a device with a key map).

- [ ] **Step 1: Write the failing tests.**
  - `KeyMapsTests`: `Positions_are_the_mean_cell_normalised` (2×3 matrix, light 0 in two cells of the top row: X is their mean column / 2, Y 0); `A_one_row_matrix_gives_y_one_half`; `A_one_column_matrix_gives_x_one_half`; `An_empty_cell_and_an_index_beyond_the_lights_are_ignored`; `A_light_in_no_cell_has_no_position`; `An_all_empty_matrix_supports_no_wave`; `Two_zones_or_a_short_zone_give_no_map` (the short zone: one zone of 3 lights on a device whose `LedNames` has 5); `Only_a_keyboard_supports_wave` (type 6 with a full matrix: `Of` not null, `SupportsWave` false); `Key_width_follows_the_smallest_gap_and_is_clamped`.
  - `DeviceModesTests` (keyboard with one zone of 3 lights at X 0, 0.5, 1; `tMs` chosen so phases are exact):
    - `Static_is_the_saved_zone_colour_on_every_light`;
    - `Breathing_pulses_every_light_together_by_half_minus_half_cos` at phase 0 (black), 0.25 (half) and 0.5 (full) of the zone colour, with speed 0 (0.05 cycles a second: `tMs` 0, 5000, 10000);
    - `Breathing_without_a_saved_colour_pulses_the_default_colour`;
    - `Colour_cycle_is_one_hue_for_the_whole_keyboard` (all lights equal; equals `LightingEffects.HsvToRgb(phase*360,1,1)`);
    - `Rainbow_wave_runs_left_to_right_with_wavelength_1_6` (at `tMs` 0 light at X `x` has hue `Wrap01(x/1.6)*360`; a light with no position uses X 0.5);
    - `Speed_maps_as_the_desk_effects_do` (`LightingEffects.CyclesPerSecond`);
    - `CantRun_gives_the_two_sentences`; `A_mode_that_cant_run_is_not_running_but_stays_saved`.
  - `LightingPaintTests` additions:
    - `Default_tuning_changes_nothing`: a loop over every channel value 0..255 and every page brightness 0..100 asserting `Tune(new(v,v,v),null,p)==new Rgb(v,v,v).Scale(p)` (25,856 cases), plus one `Paint` case with no entry on the existing `d` equal to `ZoneShown(...).Scale(page)` for every light;
    - `Correction_then_brightness_with_one_rounding`: base {101,51,21}, correction 200/100/50, device 50, page 50 gives {51,13,3};
    - `Correction_clamps_at_255_before_brightness`: {200,0,0} with red 200 at device 50 gives {128,0,0};
    - `Brightness_zero_and_correction_zero_are_black`;
    - `Lights_off_is_black_whatever_the_tuning_or_mode`;
    - `A_hardware_device_ignores_tuning_and_mode` (Kind Hardware, no colours);
    - `A_keyboard_mode_replaces_the_desk_effect_for_that_device_only`;
    - `Animating_needs_lights_on_and_a_moving_desk_effect_or_a_running_animated_mode` (static desk + wave keyboard true; same with lights off false; same with the keyboard in a hardware mode false; mode saved but can't run false; static mode false), for both overloads;
    - `View_carries_tuning_mode_problem_and_keys`.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~KeyMapsTests|FullyQualifiedName~DeviceModesTests|FullyQualifiedName~LightingPaintTests"`. Expected: compile errors, then failures.
- [ ] **Step 3: Implement.** `Tune` must be exactly: per channel `n=Math.Min(v*gain,25500)` (integers, `gain` 100 when no correction), then `(byte)Math.Round(n*device*page/1_000_000.0,MidpointRounding.AwayFromZero)` with `device` 100 when not set and `page` clamped 0..100. This keeps today's result bit for bit at defaults; do not multiply by fractions. Breathing phase is `LightingEffects.PhaseAt(new(0,0),tMs,new EffectParams{Speed=mode.Speed})`; colour cycle the same phase times 360 as hue; wave `Wrap01(x/DeviceModes.WaveLength-tMs/1000*CyclesPerSecond(speed))*360` as hue.
- [ ] **Step 4: Run** the filter, then the whole `tests/DeadWare.Tests` project. Expected: all pass, the old paint tests untouched.
- [ ] **Step 5:** Report with the mutation evidence for `Default_tuning_changes_nothing`: replace the integer form by `v*(page/100.0)` and show it fails (the controller's own calculation found 13 differing cases, the first at v 25, page 58). Controller commits "Lighting 3a: key map, keyboard modes and the tuned frame".

### Task 3: Light names and matrices from OpenRGB

**Files:**
- Modify: `src/DeadWare.Devices/OpenRgbSession.cs`, `src/DeadWare.Devices/LightingDeviceService.cs` (`Rescan`, `Shape`), `tests/DeadWare.Devices.Tests/FakeOpenRgbSession.cs`, `tests/DeadWare.Devices.Tests/Fixtures/controllers.json`
- Test: `tests/DeadWare.Devices.Tests/LightingDeviceServiceTests.cs`

**Interfaces:**
- Consumes: Task 2's `ZoneMatrix`, `LightingZone.Matrix`, `LightingDevice.LedNames`, `KeyMaps`.
- Produces: `OpenRgbController(...,int ActiveMode,IReadOnlyList<string>? LedNames=null)` filled from the device's own light list (its count is the device's total, which `KeyMaps.LightCount` uses); zones carry `Matrix`; `LightingDeviceService.Devices` carries both; the fixture's `SteelSeries Apex Pro` has 112 light names and a matrix.

- [ ] **Step 1: Check the library.** Open OpenRGB.NET 3.1.1 (`%USERPROFILE%\.nuget\packages\openrgb.net\3.1.1`, via its XML docs or a reflection one-liner in a scratch test) and record in the report the exact members for a device's lights and their names and for a zone's matrix (the map expects `Device.Leds[].Name` and `Zone.MatrixMap` with a width, a height and a `uint[,]`), including which index is the row. If the library has no matrix, stop and report: the plan changes.
- [ ] **Step 2: Fixture.** DeadLight's fixture has no light names and no matrix (checked 2026-11-20), so synthesise them. Add to this fixture's `SteelSeries Apex Pro`: `leds`, 112 names `Key 0` to `Key 111`, and on its zone `matrix:{width:22,height:6,cells:[...]}` (row-major, 132 cells): indices 0..111 placed in row order with 4294967295 in the remaining 20 cells, spread so that every row has at least one empty cell and light 60 occupies two adjacent cells (a wide key). Widen the fake's private `Dev`/`Zone` records to read them. Say in the report that the layout is synthetic: the owner's real matrix is first seen at the hardware check.
- [ ] **Step 3: Write the failing tests:** `The_keyboards_key_map_reaches_the_device` (112 names; `KeyMaps.SupportsWave` true; all 112 lights have positions; every position 0..1; light 60's X is the mean of its two cells); `Devices_without_a_matrix_have_no_key_map` (the DRAM and the mouse); `A_matrix_arriving_later_changes_the_shape` (`Rescan()` returns true when only the keyboard's matrix appears, so a state is raised).
- [ ] **Step 4: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~LightingDeviceServiceTests`. Expected: the three fail.
- [ ] **Step 5: Implement** the conversion in `OpenRgbSession.ReadControllers` (matrix flattened row-major into `ZoneMatrix`; a zone without a matrix, or with width or height 0, gets null), pass names and matrices through `Rescan`, and extend `Shape` with whether each zone has a matrix and its size.
- [ ] **Step 6: Run** the whole `tests/DeadWare.Devices.Tests` project. Expected: all pass. Controller commits "Lighting 3a: read light names and key matrices from OpenRGB".

### Task 4: Helper: requests, tick and preview

**Files:**
- Modify: `src/DeadWare.Core/DeviceHostProtocol.cs` (two requests, `PreviewEvent`, a reply code), `src/DeadWare.Devices/DeviceHost.cs`, `src/DeadWare.Devices/LightingDeviceService.cs` (`Preview`, once-per-change log)
- Test: `tests/DeadWare.Tests/DeviceHostProtocolTests.cs`, `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `tests/DeadWare.Devices.Tests/LightingDeviceServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces:
  - `SetDeviceTuningRequest(string DeviceKey,int? Brightness=null,DeviceCorrection? Correction=null,bool ResetCorrection=false)` discriminator `setDeviceTuning` (null members leave that setting as it is);
  - `SetDeviceModeRequest(string DeviceKey,string? Kind,int Speed=40)` discriminator `setDeviceMode` (`Kind` null = follow the desk);
  - `HostReplyCodes.UnknownDevice="unknown device"`, explained as "That device isn't listed any more. It may have been unplugged.";
  - `PreviewEvent(Dictionary<string,string> Colors,Dictionary<string,string>? Lights=null)`: `Lights` maps a device key to its lights' colours packed as upper-case `RRGGBB` per light with no separator, for each painted device that has a key map; colours before tuning;
  - `LightingDeviceService.Preview(settings,tMs)` keeps its signature and result (an existing test reads it as a dictionary); new `Dictionary<string,string>? PreviewLights(LightingSettings settings,double tMs)` (null when no painted device has a key map). The host calls both with one `Now()` reading.

- [ ] **Step 1: Read** `DeviceHostTests.cs` :1004-1060 and :1297-1385 (the map did not), so the new tick and preview tests use the same helpers and timings.
- [ ] **Step 2: Write the failing tests.**
  - Protocol: both requests and a `PreviewEvent` with `Lights` in `Messages()`; `A_preview_without_lights_still_reads` (JSON without the member).
  - Host:
    - `SetDeviceTuning_saves_and_repaints` (brightness 50 on the keyboard halves its next frame; saved; the state carries it);
    - `SetDeviceTuning_leaves_unnamed_settings_alone` and `ResetCorrection_removes_it`;
    - `SetDeviceTuning_out_of_range_replies_the_sentence_and_changes_nothing`;
    - `A_request_for_an_unknown_device_is_refused` (both requests; `UnknownDevice`; nothing saved);
    - `An_unplugged_devices_entry_is_kept` (tune, remove the device from the fake, rescan: the entry is still in the settings; a new request for it is refused);
    - `SetDeviceMode_refuses_what_the_device_cant_do` (wave on a keyboard without a matrix; any mode on the mouse: the two sentences);
    - `SetDeviceMode_null_returns_to_the_desk_effect`;
    - `Tuning_a_hardware_device_is_saved_but_sends_no_frame`;
    - `Tick_is_fast_for_an_animated_keyboard_mode_under_a_static_desk` and `..._and_slow_again_when_it_stops` (mode back to follow, lights off, keyboard to a hardware mode: each returns to the slow tick);
    - `Preview_flows_for_an_animated_keyboard_mode_under_a_static_desk` with per-light colours for the keyboard (112 × 6 characters, not all equal under wave) and none for the DRAM;
    - `Preview_lights_are_before_tuning` (device brightness 0: the packed colours are not black);
    - `A_tuning_change_does_not_restart_the_mode` (phase continues across the change: the frame right after equals the one the unchanged clock predicts);
    - the reply-before-state order for one new request, as the existing order tests do it.
  - Service: `A_saved_mode_that_cant_run_is_logged_once_not_per_frame` (logged from `Paint`, so it happens with no window subscribed; once per device and reason; logged again after the mode has run in between).
- [ ] **Step 3: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~DeviceHostTests`. Expected: compile errors, then failures.
- [ ] **Step 4: Implement.** `Clone` copies `Devices` with `DeviceTunings.Clone`. The two `Apply` cases go through `Mutate`; the edit looks the device up in `service.Devices` (absent: `UnknownDevice`), applies the change, calls `DeviceTunings.Normalize`, and for a mode returns `DeviceModes.CantRun`. Replace `LightingPaint.EffectRunning(settings)` by `LightingPaint.Animating(settings,service.Devices)` in the tick choice (:147), `PreviewLocked` (:277) and `Mutate` (:501); these three and the two window gates of Task 5 are every use of `EffectRunning`. The status text reads `Effect.Kind` directly and stays as it is. Extend `SameFrame` to compare `Lights`. The can't-run log goes in `LightingDeviceService.Paint`/`PaintDevice` with the service's existing once-per-change pattern, cleared in `Forget` and `Reset`.

  Accepted, do not engineer around: a Speed change shifts a mode's phase at once, because phase is time × speed, exactly as the desk effects behave today (`A_tuning_change_does_not_restart_the_mode` covers brightness and correction, not Speed); a request that changes nothing is still saved and raises a state; entries for unplugged devices count toward the 256 cap, and the 257th device is refused with "Too many device entries."
- [ ] **Step 5: Run** the whole Devices test project three times in a row and the whole `tests/DeadWare.Tests` project. Expected: all pass each time. Controller commits "Lighting 3a: tuning and mode requests, fast tick and preview for device modes".

### Task 5: Keys in the desk picture

**Files:**
- Modify: `src/DeadWare.Core/DeskPicture.cs` (a pure layout helper), `src/DeadWare.Services/DeviceHostClient.cs` (preview carries lights), `src/DeadWare.WinUI/DeskPictureView.cs`, `src/DeadWare.WinUI/LightingPage.cs` (preview plumbing and its two `EffectRunning` gates)
- Test: `tests/DeadWare.Tests/DeskPictureTests.cs`, `tests/DeadWare.App.Tests/DeviceHostClientTests.cs`

**Interfaces:**
- Consumes: `KeyMapView`, `DeviceView.Keys`, `PreviewEvent.Lights`, `LightingPaint.Animating(settings,views)`.
- Produces:
  - `public readonly record struct KeyRect(int Light,double X,double Y,double W,double H)`; `public static IReadOnlyList<KeyRect> DeskPictures.KeyRects(PicBox box,KeyMapView keys)`: one rectangle per positioned light, in drawing units, inside `box` inset by 5 units; centre at the inset box's origin plus position × its size; width `keys.KeyWidth` × inset width, height (inset height / number of distinct Y values) × 0.8, each at least 2 units, clipped to the inset box;
  - `sealed record PreviewColors(IReadOnlyDictionary<string,string> Zones,IReadOnlyDictionary<string,string>? Lights)`; `DeviceHostClient.Preview` becomes `event Action<PreviewColors>?`;
  - `DeskPictureView.ShowColors(PreviewColors? colors)`.

- [ ] **Step 1: Write the failing tests.** `DeskPictureTests`: `Key_rects_stay_inside_the_keyboards_box`, `Key_rects_keep_the_maps_order_left_to_right_and_top_to_bottom`, `A_light_without_a_position_gets_no_rect`, `Key_rects_have_a_minimum_size` (a 200-key row still gives width 2). `DeviceHostClientTests`: `A_preview_with_lights_reaches_the_subscriber` and the four existing preview tests updated to the new event type.
- [ ] **Step 2: Run** both filters. Expected: failures.
- [ ] **Step 3: Implement** `KeyRects` and the client change. In `DeskPictureView.DrawKeyboard`: when the keyboard part's device has `Keys`, draw the body as today without the four key-row lines, then one `FillRoundedRectangle` (radius 1.5) per `KeyRect` filled with that light's colour (the preview's packed colour for the device when a frame has it, else the part's zone colour from the state), the unlit colour when the part's look is not Lit, inside the existing glow when lit; no key map: draw as today. The colours are the intended ones; the picture's own dimming and lights-off rules are unchanged. In `LightingPage`: pass `PreviewColors` through `OnPreview`, `PreviewFrame`, `ShowFrame`, `ShowDeskColors`; replace the two `LightingPaint.EffectRunning(settings)` gates (:423, :570) by `LightingPaint.Animating(settings,state.Devices)`; the plain desk's `ShowColors` keeps taking the zone colours.
- [ ] **Step 4: Run** `tests/DeadWare.Tests`, `tests/DeadWare.App.Tests` and the solution build. Expected: all pass, 0 new warnings.
- [ ] **Step 5:** Report with a hand trace: what the keyboard part draws (a) before the key map arrives, (b) Static, (c) Rainbow wave with frames flowing, (d) lights off, (e) the helper stopped; and the size of one key at picture widths 720 and 1280. Controller commits "Lighting 3a: the picture's keyboard drawn key by key in live colours".

### Task 6: Card sections

**Files:**
- Create: `src/DeadWare.WinUI/TuningSlider.cs`
- Modify: `src/DeadWare.WinUI/LightingPage.cs` (`DeviceCard`)

**Interfaces:**
- Consumes: `DeviceView` members from Task 2, the two requests from Task 4, `Throttle`, the page's `Send(HostMessage,Action<Reply>?)`.
- Produces: `internal sealed class TuningSlider` (a labelled row: label, `Slider`, value text; `Throttle` + sending count + dragging + syncing guard, modelled on `LightingEffectBar.EffectSlider` and the page's Brightness slider; `void Show(double value,bool enabled)` applies a state value unless the user is dragging or a send is pending; `void Cancel()`; a `Func<int,HostMessage>` builds the request for a value).

- [ ] **Step 1: Build the sections** in `DeviceCard`, in this order under the zone rows, as in the mockup:
  1. **Lighting mode**, only when `device.Keyboard`: a `ComboBox` with the five items in the Global Constraints order and a `Speed` `TuningSlider` (0..100, step 1) shown for Breathing, Colour cycle and Rainbow wave. When `!device.Wave` the Rainbow wave item is disabled and the line "This keyboard doesn't report its key positions." shows under the box. When `device.ModeProblem` is set, the box shows the saved mode and the problem sentence shows under it. Choosing an item sends `SetDeviceModeRequest` (null kind for Follow) with the current speed; a refused reply puts the box back from the state.
  2. **Brightness**: a `This device` `TuningSlider` 0..100 with the value as `N%`; under it "Works with the page's Brightness: N% of M% here." only when either is below 100.
  3. **Colour correction**: a header row with a `Reset` button, `Red`, `Green`, `Blue` `TuningSlider`s 0..200 (large change 10), and the line "For a device whose white looks tinted next to the others. 100 leaves a colour as it is." `Reset` sends `ResetCorrection=true` and is disabled at 100/100/100. Each slider sends all three channels.
  - In picture mode, a device with more than one part shows under Colour correction: "Brightness and colour correction apply to the whole device: " followed by its part labels joined with ", ".
  - A hardware-mode device shows first: "This device runs its own effect, <mode>. DeadWare's colours, brightness and colour correction don't reach it while it does." (replacing today's shorter own-effect line), with all three sections disabled, not hidden. An unsupported device shows none of the three. Everything is disabled while the helper isn't live, and `Disable()` covers them.
- [ ] **Step 2: Build** `dotnet build DeadWare.slnx -c Release`. Expected: 0 errors, no new warnings.
- [ ] **Step 3: Run** all three test projects with the measurement filter. Expected: all pass.
- [ ] **Step 4:** Report with hand traces: (a) drag Brightness from 100 to 40 and release: requests sent, what the slider shows if the last reply is refused; (b) a state arrives while a slider is being dragged; (c) select another device while a send is pending; (d) the card for the keyboard, the hub, the front fans and an unsupported device, section by section, live and not live; (e) Reset at defaults; (f) keyboard use: Tab order through the sections and arrow keys on a slider. Controller commits "Lighting 3a: lighting mode, brightness and colour correction on the device card".

### Task 7: Guide, release note and package

**Files:**
- Modify: `docs/user-guide.md` (Lighting), `Directory.Build.props` (Version 0.20.0), `docs/superpowers/specs/2026-11-15-lighting-design.md` (one pointer line under the title to the 3a design)
- Create: `docs/releases/v0.20.0.md`

- [ ] **Step 1: Write** the guide's device card sections from the code as built (read `LightingPage.cs`, `DeviceModes.cs`, `KeyMaps.cs`; state nothing unverified): the three sections, the five modes and what each does, Speed, how device and page brightness combine, what colour correction is for, the hardware-effect note, the keyboard drawn key by key, that the picture shows intended colours before correction and brightness, and the rollback note ("If you go back to 0.19.0 after tuning a device, 0.19.0 runs the lights but won't save lighting changes; your tuning is there again when you update."). Release note in the existing voice: what changed in this version only.
- [ ] **Step 2: Run** `./scripts/publish.ps1 -Version 0.20.0` (controller's step). Expected: exit 0, package in `artifacts`, all three test projects green.
- [ ] **Step 3:** Controller commits "Lighting 3a: guide, 0.20.0 release note".

---

## After the tasks (controller)

1. Whole-branch review with the saved workflow (`base` = 9de8dea, the main commit this branch starts from, `head` = the last commit, this spec), one fix wave, re-review of the fix range.
2. The owner's hardware check from the spec, only after the "no lighting software" freeze test has a verdict or on the owner's word; test build launched through an owner-approved elevated script.
3. Push, draft pull request, the owner merges and tags.

## Calls made in writing this plan (for the owner)

- 3a ships as **0.20.0**. The tag stays the owner's call.
- Tuning for a device running its own effect is accepted and saved by the helper (the card's controls are disabled, so this only matters for a device that changes mode later).
- Stored tuning is capped at 256 devices.

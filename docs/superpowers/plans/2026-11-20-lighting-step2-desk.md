# Lighting step 2: desk layout and effects. Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The lighting helper runs Wave, Breathing and Spectrum from a desk layout it arranges automatically, and the Lighting page gains effect tiles, a generic desk card with Edit layout, and live colours.

**Architecture:** Effect maths, the layout model, auto-arrange and the desk geometry are pure code in `DeadWare.Core`. `DeadWare.Devices` paints from them on a 33 ms tick while an effect runs, owns the layout in `lighting.json`, and sends preview frames to a subscribed window. `DeadWare.Services`' `DeviceHostClient` surfaces those frames. The WinUI page draws the desk with Win2D and sends whole-layout edits.

**Tech Stack:** .NET 10, WinUI 3 (Windows App SDK 2.6.5), Win2D, xUnit 2.9.3, System.Text.Json, OpenRGB.NET 3.1.1.

**Spec:** `docs/superpowers/specs/2026-11-19-lighting-step2-desk-design.md` (approved 2026-11-20). Read it before starting; this plan argues from it. The main spec is `docs/superpowers/specs/2026-11-15-lighting-design.md`. The behavioural reference is DeadLight at `I:\DeadLight` (read only): `shared/effects.ts`, `shared/color.ts`, `shared/layout.ts`, `electron/painter.ts`, `renderer/components/LayoutTab.tsx`, and `tests/effects.test.ts`, `tests/layout.test.ts`, `tests/painter.test.ts`.

## Global Constraints

- **Where:** worktree `I:\DeadWare\.claude\worktrees\lighting-step2`, branch `claude/lighting-step2`. Never run git or dotnet in `I:\DeadWare` itself. A restarted session or workflow agent starts in another folder: `cd` into the worktree first.
- **Naming:** no file, class, member or log may be called "helper". User-facing text may say "lighting helper".
- **Versions stay at 1:** `LightingSettings.CurrentVersion` and `DeviceHostProtocol.Version` are not raised. New file fields are optional on read.
- **Effect maths**, from DeadLight unchanged: speed 0–100 → 0.05–2 cycles per second; spread 0–100 → wavelength 3.0–0.2; distance `x·cos(a) + y·sin(a)` with 0° left to right and 90° back to front; phase = fractional part of `distance / wavelength − seconds × cycles per second`.
- **Defaults:** speed 40, angle 0, spread 50, no colours; at most 8 colours; desk shape L left; tower anchor (0.14, 0.3).
- **Pacing:** paint tick 33 ms while an effect runs with lights on, else 500 ms. Conflict scan and rescan stay at 5 s. Per-device caps (33 ms, 200 ms for type 1) and the 400 ms refresh are unchanged. Preview frames: at most one per 100 ms.
- **Grids:** auto-arrange uses 12 columns by 8 rows; hand placement snaps to 1/48; a drag under 0.01 is a click.
- **A change that can't be saved is refused** and the live settings stay as they were (`DeviceHost.Mutate`).
- **Hardware-mode devices are never written to by an effect**, and changing effect or layout never restarts OpenRGB, rescans, or switches a device's mode.
- **Logs:** changes only. Never a line per tick or per frame.
- **Tests:** `src/DeadWare.WinUI` has no test project; logic that needs tests goes in Core or Services. Helper tests use `Clock` and `FakeOpenRgbSession` and wait on observable signals, never on a fixed fake-time advance followed by a real-time wait.
- **Code style:** the repository's dense one-line style in Core, Services and Devices; code-only construction in WinUI pages.
- **Build and tests:** `dotnet build DeadWare.slnx -c Release` must give 0 errors and no new warnings (4 xUnit analyzer warnings in `DeadWare.App.Tests` pre-exist). Before every commit, run the three test projects with `--filter "FullyQualifiedName!~MeasurementTests"`. A failure near midnight UTC or under load is re-run before it is believed.
- **Commits** end with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. Never tag. `scripts/publish.ps1` must stay Windows PowerShell 5.1-compatible.
- **Hardware:** never launch DeadWare, `DeadWare.Devices.exe`, OpenRGB or DeadLight from a Claude shell. No on-screen automation while the owner is gaming. Each live step needs the owner's approval.

## Review Focus

1. **A device added or re-keyed while the window holds a layout draft.** The user expects the new device to take part in Wave and the dragged device to land where it was dropped. Tests: Task 6 (`SetLayout_places_a_device_the_draft_lacks`) and Task 3 (`Remap_moves_layout_entries`, `Remap_then_arrange_keeps_a_returning_device_in_place`).
2. **Effect or layout changed while OpenRGB is restarting or blocked.** The change is saved and applies when lighting resumes; nothing throws and no mode is switched. Test: Task 6 (`SetEffect_while_waiting_is_saved_and_painted_after_reconnect`).
3. **A `lighting.json` edited by hand or written by a later build:** an unknown effect name, speed 500, a layout point outside the square. Unknown and out-of-range values must not set aside the user's colours; malformed ones follow step 1's rule. Tests: Task 3.
4. **The window hidden, closed or crashed during an effect.** Lights keep moving, and no preview frame is written to a pipe nobody reads. Tests: Task 7 (`Preview_stops_when_the_client_goes`, `Preview_never_displaces_a_pending_state`).
5. **Lights off, or a hardware-mode device, during an effect.** Black refresh only, no effect evaluated, the Node Pro never sent `UpdateLeds`. Tests: Task 4 and Task 6 (`Tick_is_slow_while_lights_are_off`).

---

### Task 1: Effect maths in Core

**Files:**
- Modify: `docs/superpowers/specs/2026-11-15-lighting-design.md` (one line under the title: step 2 is specified in `2026-11-19-lighting-step2-desk-design.md`, which governs where they differ: generic desk card, Breathing and Spectrum rules)
- Create: `src/DeadWare.Core/LightingEffects.cs`
- Test: `tests/DeadWare.Tests/LightingEffectsTests.cs`

**Interfaces:**
- Consumes: `Rgb` (`LightingModels.cs`).
- Produces:

```csharp
public readonly record struct DeskPoint(double X,double Y);
public enum EffectKind { Static, Wave, Breathing, Spectrum }
public sealed class EffectParams { public double Speed{get;set;}=40; public double AngleDeg{get;set;} public double Spread{get;set;}=50; public List<string> Colors{get;set;}=new(); }
public sealed class EffectSettings { public string Kind{get;set;}="Static"; public EffectParams Wave{get;set;}=new(); public EffectParams Breathing{get;set;}=new(); public EffectParams Spectrum{get;set;}=new(); }
public static class LightingEffects
{
    public const int MaxColors=8;
    public static readonly IReadOnlyList<(string Name,double Angle)> Directions; // the eight in Step 3, in that order
    public static EffectSettings Clone(EffectSettings s);          // deep, colours included
    public static EffectKind KindOf(string? kind);                 // unknown or null -> Static
    public static EffectParams? ParamsFor(EffectSettings s,EffectKind kind); // null for Static
    public static double CyclesPerSecond(double speed);
    public static double Wavelength(double spread);
    public static double Project(DeskPoint p,double angleDeg);
    public static double PhaseAt(DeskPoint p,double tMs,EffectParams q);
    public static Rgb HsvToRgb(double h,double s,double v);
    public static Rgb SamplePalette(IReadOnlyList<Rgb> colors,double phase);
    public static Rgb Evaluate(EffectKind kind,DeskPoint pos,double tMs,EffectParams q,Rgb zoneColor);
    public static int NearestDirection(double angleDeg);            // index into Directions
    public static string? Validate(EffectSettings s);               // null when valid
    public static void Normalize(EffectSettings s);                 // clamps and wraps in place
}
```

- [ ] **Step 1: Write the failing tests.** Port from `I:\DeadLight\tests\effects.test.ts` the cases for `cyclesPerSecond`, `wavelength`, `project`, `phaseAt`, `samplePalette` and Wave, with their exact numbers. Do not port its Breathing and Spectrum cases. Add:

```csharp
[Theory][InlineData(0,0.05)][InlineData(100,2.0)][InlineData(-5,0.05)][InlineData(250,2.0)]
public void Speed_maps_to_cycles_per_second(double speed,double hz)=>Assert.Equal(hz,LightingEffects.CyclesPerSecond(speed),6);
[Theory][InlineData(0,3.0)][InlineData(100,0.2)][InlineData(50,1.6)]
public void Spread_is_inverted(double spread,double wavelength)=>Assert.Equal(wavelength,LightingEffects.Wavelength(spread),6);
[Fact] public void Breathing_is_in_step_and_uses_the_zone_colour()
{
    var q=new EffectParams{Speed=40};var red=new Rgb(255,0,0);
    var a=LightingEffects.Evaluate(EffectKind.Breathing,new(0.1,0.9),700,q,red);
    Assert.Equal(a,LightingEffects.Evaluate(EffectKind.Breathing,new(0.9,0.1),700,q,red));
    Assert.Equal(Rgb.Black,LightingEffects.Evaluate(EffectKind.Breathing,new(0.5,0.5),0,q,red));       // phase 0
    Assert.Equal(0,a.G);Assert.Equal(0,a.B);
}
[Fact] public void Spectrum_is_in_step_and_ignores_colours()
{
    var q=new EffectParams{Colors={"#00FF00"}};
    Assert.Equal(new Rgb(255,0,0),LightingEffects.Evaluate(EffectKind.Spectrum,new(0.3,0.7),0,q,Rgb.Black)); // hue 0
    Assert.Equal(LightingEffects.Evaluate(EffectKind.Spectrum,new(0,0),900,q,Rgb.Black),LightingEffects.Evaluate(EffectKind.Spectrum,new(1,1),900,q,Rgb.Black));
}
[Fact] public void Wave_with_no_colours_is_the_hue_circle()=>Assert.Equal(LightingEffects.HsvToRgb(180,1,1),LightingEffects.Evaluate(EffectKind.Wave,new(0.8,0),0,new EffectParams{Spread=50},Rgb.Black)); // 0.8/1.6 = phase 0.5
[Fact] public void Static_returns_the_zone_colour()=>Assert.Equal(new Rgb(1,2,3),LightingEffects.Evaluate(EffectKind.Static,new(0,0),5,new(),new Rgb(1,2,3)));
[Theory][InlineData("Wave",EffectKind.Wave)][InlineData("Plasma",EffectKind.Static)][InlineData(null,EffectKind.Static)]
public void Unknown_kinds_are_static(string? name,EffectKind kind)=>Assert.Equal(kind,LightingEffects.KindOf(name));
[Fact] public void There_are_eight_directions_45_degrees_apart()
{
    Assert.Equal(8,LightingEffects.Directions.Count);
    Assert.Equal(new[]{0d,45,90,135,180,225,270,315},LightingEffects.Directions.Select(d=>d.Angle).Order());
    Assert.Equal("Left to right",LightingEffects.Directions[LightingEffects.NearestDirection(359)].Name);
    Assert.Equal("Front to back",LightingEffects.Directions[LightingEffects.NearestDirection(270)].Name);
}
[Theory][InlineData(double.NaN,0,50)][InlineData(40,360,50)][InlineData(40,-1,50)][InlineData(40,0,101)][InlineData(-1,0,50)]
public void Validate_refuses_out_of_range_numbers(double speed,double angle,double spread)=>Assert.NotNull(LightingEffects.Validate(new EffectSettings{Kind="Wave",Wave=new(){Speed=speed,AngleDeg=angle,Spread=spread}}));
[Fact] public void Validate_refuses_bad_colours_nine_colours_and_unknown_kinds()
{
    Assert.NotNull(LightingEffects.Validate(new EffectSettings{Wave=new(){Colors={"red"}}}));
    Assert.NotNull(LightingEffects.Validate(new EffectSettings{Wave=new(){Colors=Enumerable.Repeat("#112233",9).ToList()}}));
    Assert.NotNull(LightingEffects.Validate(new EffectSettings{Kind="Plasma"}));
    Assert.Null(LightingEffects.Validate(new EffectSettings()));
}
[Fact] public void Normalize_clamps_wraps_and_resets_unknown_kinds()
{
    var s=new EffectSettings{Kind="Plasma",Wave=new(){Speed=500,AngleDeg=-90,Spread=-3}};
    LightingEffects.Normalize(s);
    Assert.Equal("Static",s.Kind);Assert.Equal(100,s.Wave.Speed);Assert.Equal(270,s.Wave.AngleDeg);Assert.Equal(0,s.Wave.Spread);
}
```

- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~LightingEffectsTests`. Expected: compile errors, the types are missing.
- [ ] **Step 3: Implement `LightingEffects.cs`.** Port `hsvToRgb`, `lerpColor` and `dim` from `shared/color.ts` with `Math.Round(x, MidpointRounding.AwayFromZero)`, matching JavaScript's `Math.round` for these non-negative values. Direction names, in order: Left to right (0), Back-left to front-right (45), Back to front (90), Back-right to front-left (135), Right to left (180), Front-right to back-left (225), Front to back (270), Front-left to back-right (315). `Evaluate`: Static → `zoneColor`; Wave → `SamplePalette(parsed q.Colors, PhaseAt(pos,…))`; Breathing → `zoneColor` dimmed by `0.5 − 0.5·cos(2π·phase)` with the phase at `(0,0)`; Spectrum → `HsvToRgb(phase·360,1,1)` with the phase at `(0,0)`. `Validate` checks all three parameter sets and `Kind` against the enum names, and refuses, never throws on, a null parameter set, a null `Colors` list and a null colour string (add a test for each: over the pipe these arrive from another process). `KindOf` and `Validate` compare ordinally against `Enum.GetNames<EffectKind>()`; `Enum.TryParse` would accept `"1"` and `"Wave, Static"` (test both as unknown). `Normalize` replaces null members with defaults, drops nothing from `Colors`, clamps speed and spread to 0–100 (NaN → the default), and wraps the angle into [0, 360).
- [ ] **Step 4: Run the tests until they pass**, then `dotnet build DeadWare.slnx -c Release`.
- [ ] **Step 5: Commit:** `git add -A docs/superpowers/specs src/DeadWare.Core/LightingEffects.cs tests/DeadWare.Tests/LightingEffectsTests.cs` and `git commit -m "feat(lighting): desk effect maths"`.

### Task 2: Desk layout model and auto-arrange

**Files:**
- Create: `src/DeadWare.Core/DeskLayout.cs`
- Test: `tests/DeadWare.Tests/DeskLayoutTests.cs`

**Interfaces:**
- Consumes: `DeskPoint` (Task 1), `LightingKeys.Zone`, `DeviceView`, `ZoneView`, `PaintKind`.
- Produces:

```csharp
public sealed class DeskRect { public double X{get;set;} public double Y{get;set;} public double W{get;set;} public double H{get;set;} }
public sealed class TowerGroup { public DeskPoint At{get;set;}=new(0.14,0.3); public List<string> Members{get;set;}=new(); public List<string> Excluded{get;set;}=new(); }
public sealed class DeskLayout { public int Version{get;set;}=1; public Dictionary<string,DeskPoint> Zones{get;set;}=new(); public TowerGroup Tower{get;set;}=new(); public List<DeskRect> Desk{get;set;}=DeskLayouts.Preset(DeskShape.LLeft); public List<string> Unplaced{get;set;}=new(); }
public enum DeskShape { Straight, LLeft, LRight }
public sealed record ArrangeDevice(string Key,string Name,IReadOnlyList<int> ZoneIndexes);
public static class DeskLayouts
{
    public const int Columns=12,Rows=8,MaxEntries=4096;
    public static List<DeskRect> Preset(DeskShape shape);          // fresh copies of DeadLight's DESK_PRESETS numbers
    public static DeskShape? ShapeOf(DeskLayout layout);            // which preset the rectangles equal, else null
    public static DeskLayout Empty();
    public static DeskLayout Clone(DeskLayout layout);              // deep
    public static string? Validate(DeskLayout layout);
    public static bool IsTowerDevice(string name);
    public static IReadOnlyList<ArrangeDevice> Arrangeable(IEnumerable<DeviceView> devices);
    public static DeskLayout AutoArrange(DeskLayout layout,IReadOnlyList<ArrangeDevice> devices); // the SAME object when nothing changed
}
```

- [ ] **Step 1: Write the failing tests.** Port every case of `I:\DeadLight\tests\layout.test.ts` that covers `createEmptyLayout`, `validateLayout`, `isTowerDevice` and `autoArrange` (determinism, sort by key, tower region anchored on the tower, peripheral region, overflow to the nearest free cell, same object when unchanged), keeping its numbers. Skip its rotate and mirror cases. Add:

```csharp
static DeviceView Dev(string key,string name,PaintKind kind,params int[] leds)=>new(key,name,0,kind,null,null,false,leds.Select((n,i)=>new ZoneView(LightingKeys.Zone(key,i),i,"z"+i,n,"#000000",false)).ToList());
[Fact] public void Arrangeable_skips_unsupported_devices_and_zero_led_zones()
{
    var list=DeskLayouts.Arrangeable([Dev("a|1","Fans",PaintKind.Direct,8,0,8),Dev("b|2","Odd",PaintKind.Unsupported,4),Dev("c|3","Node",PaintKind.Hardware,0,0),Dev("d|4","Node2",PaintKind.Hardware,48)]);
    Assert.Equal(new[]{"a|1","d|4"},list.Select(d=>d.Key));
    Assert.Equal(new[]{0,2},list[0].ZoneIndexes);
}
[Fact] public void Unplaced_devices_are_not_arranged_or_made_members()
{
    var l=DeskLayouts.Empty();l.Unplaced.Add("a|1");
    Assert.Same(l,DeskLayouts.AutoArrange(l,[new("a|1","Fans",[0])]));
}
[Fact] public void A_device_that_is_not_arrangeable_never_joins_the_tower()
{
    var next=DeskLayouts.AutoArrange(DeskLayouts.Empty(),DeskLayouts.Arrangeable([Dev("b|2","Odd",PaintKind.Unsupported,4)]));
    Assert.Empty(next.Tower.Members);
}
[Fact] public void Validate_checks_unplaced_and_the_entry_caps()
{
    var l=DeskLayouts.Empty();l.Unplaced=null!;Assert.NotNull(DeskLayouts.Validate(l));
    l=DeskLayouts.Empty();for(int i=0;i<=DeskLayouts.MaxEntries;i++) l.Zones["k#"+i]=new(0.5,0.5);Assert.NotNull(DeskLayouts.Validate(l));
    l=DeskLayouts.Empty();l.Zones[new string('k',513)]=new(0.5,0.5);Assert.NotNull(DeskLayouts.Validate(l));
    l=DeskLayouts.Empty();l.Zones["k#0"]=new(1.2,0.5);Assert.NotNull(DeskLayouts.Validate(l));
    l=DeskLayouts.Empty();l.Zones["k#0"]=new(double.NaN,0.5);Assert.NotNull(DeskLayouts.Validate(l));
    l=DeskLayouts.Empty();l.Desk.Add(new(){X=0.1,Y=0.1,W=0.1,H=0.1});Assert.NotNull(DeskLayouts.Validate(l));   // three rectangles
    l=DeskLayouts.Empty();l.Version=2;Assert.NotNull(DeskLayouts.Validate(l));
}
[Fact] public void Validate_refuses_nulls_without_throwing()
{
    foreach(var broken in new Action<DeskLayout>[]{l=>l.Zones=null!,l=>l.Tower=null!,l=>l.Tower.Members=null!,l=>l.Tower.Excluded=null!,l=>l.Desk=null!,l=>l.Desk[0]=null!,l=>l.Tower.Members.Add(null!),l=>l.Unplaced.Add(null!)})
    {var l=DeskLayouts.Empty();broken(l);Assert.NotNull(DeskLayouts.Validate(l));}
}
[Fact] public void Presets_are_fresh_copies_and_ShapeOf_reads_them_back()
{
    Assert.NotSame(DeskLayouts.Preset(DeskShape.LLeft)[0],DeskLayouts.Preset(DeskShape.LLeft)[0]);
    Assert.Equal(DeskShape.LRight,DeskLayouts.ShapeOf(new DeskLayout{Desk=DeskLayouts.Preset(DeskShape.LRight)}));
}
```

- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~DeskLayoutTests`.
- [ ] **Step 3: Implement `DeskLayout.cs`** as a port of `shared/layout.ts` without rotate and mirror. Differences from DeadLight: `AutoArrange` takes only arrangeable devices and places only their listed zone indexes (key `LightingKeys.Zone(device.Key,index)`); it skips devices in `Unplaced` for both membership and points; `Validate` also requires `Unplaced`, requires `Version==1`, caps `Zones`, `Members`, `Excluded` and `Unplaced` at `MaxEntries` each and every key at 512 characters, refuses NaN, and refuses (never throws on) a null member, list element or rectangle. The peripheral pattern is DeadLight's `PERIPHERAL` regex, case-insensitive. Sort with `string.CompareOrdinal`.
- [ ] **Step 4: Run the tests until they pass**, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): desk layout model and auto-arrange"`.

### Task 3: Settings, the file and key remap

**Files:**
- Modify: `src/DeadWare.Core/LightingModels.cs` (class `LightingSettings`), `src/DeadWare.Core/LightingSettingsCodec.cs`, `src/DeadWare.Core/LightingKeys.cs`
- Test: `tests/DeadWare.Tests/LightingSettingsCodecTests.cs`, `tests/DeadWare.Tests/LightingKeysTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: `LightingSettings.Effect` (`EffectSettings`, default `new()`), `LightingSettings.Layout` (`DeskLayout`, default `DeskLayouts.Empty()`); `LightingSettingsCodec.Read` normalises then validates both; `LightingKeys.Remap` (same signature) also re-keys the layout.

- [ ] **Step 1: Write the failing tests.**

```csharp
// LightingSettingsCodecTests
[Fact] public void A_file_without_effect_or_layout_gets_static_and_an_empty_layout()
{
    var r=LightingSettingsCodec.Read("""{"Version":1,"LightsOn":true,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{}}""");
    Assert.Null(r.Error);Assert.Equal("Static",r.Settings.Effect.Kind);Assert.Empty(r.Settings.Layout.Zones);Assert.Equal(DeskShape.LLeft,DeskLayouts.ShapeOf(r.Settings.Layout));
}
[Fact] public void Null_effect_and_layout_read_as_defaults()
{
    var r=LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":null,"Layout":null}""");
    Assert.Null(r.Error);Assert.NotNull(r.Settings.Effect);Assert.NotNull(r.Settings.Layout);
}
[Fact] public void An_unknown_effect_and_out_of_range_numbers_keep_the_file()
{
    var r=LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":{"Kind":"Plasma","Wave":{"Speed":500,"AngleDeg":725,"Spread":-1,"Colors":[]}}}""");
    Assert.False(r.Quarantine);Assert.Equal("Static",r.Settings.Effect.Kind);Assert.Equal(100,r.Settings.Effect.Wave.Speed);Assert.Equal(5,r.Settings.Effect.Wave.AngleDeg);Assert.Equal("#112233",r.Settings.DefaultColor);
}
[Theory]
[InlineData("""{"Kind":"Wave","Wave":{"Colors":["red"]}}""")]                       // bad colour
[InlineData("""{"Kind":"Wave","Wave":{"Colors":["#112233","#112233","#112233","#112233","#112233","#112233","#112233","#112233","#112233"]}}""")]
[InlineData("""{"Kind":"Wave","Wave":{"Speed":"fast"}}""")]                         // wrong type
public void A_malformed_effect_sets_the_file_aside(string effect)=>Assert.True(LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":"""+effect+"}").Quarantine);
[Fact] public void A_layout_point_outside_the_square_sets_the_file_aside()=>Assert.True(LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Layout":{"Version":1,"Zones":{"a|1#0":{"X":2,"Y":0}},"Tower":{"At":{"X":0.1,"Y":0.3},"Members":[],"Excluded":[]},"Desk":[],"Unplaced":[]}}""").Quarantine);
[Fact] public void Effect_and_layout_round_trip()
{
    var s=new LightingSettings{Effect=new(){Kind="Wave",Wave=new(){Speed=70,AngleDeg=135,Spread=20,Colors={"#FF0000","#0000FF"}}}};
    s.Layout.Zones["a|1#0"]=new(0.25,0.75);s.Layout.Tower.Members.Add("a|1");s.Layout.Unplaced.Add("b|2");
    var back=LightingSettingsCodec.Read(LightingSettingsCodec.Write(s)).Settings;
    Assert.Equal("Wave",back.Effect.Kind);Assert.Equal(135,back.Effect.Wave.AngleDeg);Assert.Equal(new[]{"#FF0000","#0000FF"},back.Effect.Wave.Colors);
    Assert.Equal(new DeskPoint(0.25,0.75),back.Layout.Zones["a|1#0"]);Assert.Equal(new[]{"a|1"},back.Layout.Tower.Members);Assert.Equal(new[]{"b|2"},back.Layout.Unplaced);
}
[Fact] public void Write_refuses_an_invalid_effect()=>Assert.Throws<InvalidDataException>(()=>LightingSettingsCodec.Write(new LightingSettings{Effect=new(){Kind="Plasma"}}));
// LightingKeysTests
[Fact] public void Remap_moves_layout_entries()
{
    var s=new LightingSettings();
    s.Layout.Zones["SER|loc#0"]=new(0.4,0.4);s.Layout.Tower.Members.Add("SER|loc");s.Layout.Tower.Excluded.Add("SER|loc");s.Layout.Unplaced.Add("SER|loc");
    Assert.True(LightingKeys.Remap(s,["|loc"]));
    Assert.Equal(new DeskPoint(0.4,0.4),s.Layout.Zones["|loc#0"]);Assert.False(s.Layout.Zones.ContainsKey("SER|loc#0"));
    Assert.Equal(new[]{"|loc"},s.Layout.Tower.Members);Assert.Equal(new[]{"|loc"},s.Layout.Tower.Excluded);Assert.Equal(new[]{"|loc"},s.Layout.Unplaced);
}
[Fact] public void A_target_that_already_has_layout_entries_is_refused()
{
    var s=new LightingSettings();s.Layout.Zones["SER|loc#0"]=new(0.4,0.4);s.Layout.Zones["|loc#0"]=new(0.6,0.6);
    Assert.False(LightingKeys.Remap(s,["|loc"]));
}
[Fact] public void Remap_then_arrange_keeps_a_returning_device_in_place()
{
    var s=new LightingSettings();s.Layout.Zones["SER|loc#0"]=new(0.4,0.4);s.Layout.Tower.Members.Add("SER|loc");
    LightingKeys.Remap(s,["|loc"]);
    var next=DeskLayouts.AutoArrange(s.Layout,[new("|loc","Fans",[0])]);
    Assert.Same(s.Layout,next);Assert.Equal(new DeskPoint(0.4,0.4),next.Zones["|loc#0"]);
}
```

- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~LightingSettingsCodecTests|FullyQualifiedName~LightingKeysTests"`.
- [ ] **Step 3: Implement.** `LightingSettingsCodec.Read`: after deserialising, replace a null `Effect` or `Layout` with its default and call `LightingEffects.Normalize` before `Validate`. `Values` adds `LightingEffects.Validate(settings.Effect)` and `DeskLayouts.Validate(settings.Layout)`, each null-checked. A wrong JSON type fails as `JsonException`, which today returns Quarantine before `Version` is looked at. That would break Ruling AS (a later DeadWare's file is never set aside) now that `Effect` and `Layout` are typed: `tests/DeadWare.Devices.Tests/DeviceHostTests.cs` has `Newer_lighting_json_is_used_but_never_saved`, whose version 2 file carries a `Layout` of another shape. So on `JsonException`, read `Version` with `JsonDocument`; when it is above `CurrentVersion`, remove `Effect` and `Layout` from the JSON (`JsonNode`) and deserialise again, returning `Newer` (with defaults and the error if that fails too). Add `A_newer_file_with_another_layout_shape_is_kept` to the codec tests, and leave the existing host test unchanged and passing. `LightingKeys.Remap`: add to `stored` every device key found in `Layout.Zones` (through `TrySplitZone`), `Tower.Members`, `Tower.Excluded` and `Unplaced`; apply `Rekey` to all four (keep list order, drop a duplicate a rename creates).
- [ ] **Step 4: Run the tests until they pass**, plus the whole `tests/DeadWare.Tests` project with the measurement filter, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): effect and layout in lighting.json, remapped with device keys"`.

### Task 4: Painting an effect

**Files:**
- Modify: `src/DeadWare.Core/LightingPaint.cs`, `src/DeadWare.Devices/LightingDeviceService.cs`
- Test: `tests/DeadWare.Tests/LightingPaintTests.cs`, `tests/DeadWare.Devices.Tests/LightingDeviceServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:

```csharp
// LightingPaint
public static bool EffectRunning(LightingSettings settings);                                   // LightsOn and Kind is not Static
public static Rgb ZoneShown(LightingSettings settings,LightingDevice device,LightingZone zone,double tMs); // before brightness
public static DevicePaint Paint(LightingSettings settings,LightingDevice device,double tMs=0);
// LightingDeviceService
public void Paint(LightingSettings settings,double tMs=0);
public Dictionary<string,string> Preview(LightingSettings settings,double tMs);                // zone key -> #RRGGBB, Direct devices, zones with LEDs
```

- [ ] **Step 1: Write the failing tests.** In `LightingPaintTests`, add fixture fields `s` (a `LightingSettings`) and `d` for these tests (the file's existing tests use locals; leave them): a two-zone Direct device `d` (zones of 2 and 3 LEDs) and `s.Effect.Kind="Wave"`, `s.Effect.Wave.Colors={"#FF0000","#0000FF"}`, `Spread=50`:

```csharp
[Fact] public void A_wave_gives_each_zone_its_points_colour_on_every_led()
{
    s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0,0);s.Layout.Zones[LightingKeys.Zone(d.Key,1)]=new(0.8,0);   // phases 0 and 0.5
    var p=LightingPaint.Paint(s,d,0);
    Assert.Equal(new[]{new Rgb(255,0,0),new Rgb(255,0,0),new Rgb(0,0,255),new Rgb(0,0,255),new Rgb(0,0,255)},p.Colors);
}
[Fact] public void A_zone_without_a_point_keeps_its_static_colour(){s.Layout.Zones.Clear();Assert.All(LightingPaint.Paint(s,d,0).Colors,c=>Assert.Equal(new Rgb(0x46,0x82,0xB4),c));}
[Fact] public void An_unplaced_device_keeps_its_static_colour(){s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0,0);s.Layout.Unplaced.Add(d.Key);Assert.Equal(new Rgb(0x46,0x82,0xB4),LightingPaint.Paint(s,d,0).Colors[0]);}
[Fact] public void Brightness_is_applied_after_the_effect(){s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0,0);s.Brightness=50;Assert.Equal(new Rgb(128,0,0),LightingPaint.Paint(s,d,0).Colors[0]);}
[Fact] public void Lights_off_is_black_whatever_the_effect(){s.LightsOn=false;Assert.All(LightingPaint.Paint(s,d,1234).Colors,c=>Assert.Equal(Rgb.Black,c));Assert.False(LightingPaint.EffectRunning(s));}
[Fact] public void A_hardware_device_is_untouched_by_an_effect()
{
    var node=new LightingDevice("n|1","Corsair Lighting Node Pro",0,[new(0,"Channel 1",48)],["Direct","Rainbow Wave"],1);
    var p=LightingPaint.Paint(s,node,999);
    Assert.Equal(PaintKind.Hardware,p.Kind);Assert.Equal("Rainbow Wave",p.Mode);Assert.Empty(p.Colors);
}
[Fact] public void Breathing_uses_the_shared_colour_for_a_zone_with_none_of_its_own()
{
    s.Effect.Kind="Breathing";s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0.5,0.5);
    var half=1000/LightingEffects.CyclesPerSecond(40)/2;                                                       // phase 0.5: full colour
    Assert.Equal(new Rgb(0x46,0x82,0xB4),LightingPaint.Paint(s,d,-half).Colors[0]);
}
```

In `LightingDeviceServiceTests`: `Paint_sends_new_frames_as_effect_time_advances` (two `Paint(s,t)` calls 40 ms of fake time apart with different `t` produce two different `UpdateLeds` for a placed Direct device), `A_hardware_device_gets_no_UpdateLeds_during_an_effect`, and `Preview_lists_direct_zones_before_brightness` (brightness 50; the preview shows `#FF0000`, not `#800000`; a hardware device and a 0-LED zone are absent).

- [ ] **Step 2: Run them to see them fail** in both test projects.
- [ ] **Step 3: Implement.** `ZoneShown`: the static `ZoneColor` when Static is selected, the device is in `Layout.Unplaced`, or the zone has no point; else `LightingEffects.Evaluate(kind, point, tMs, ParamsFor(...), ZoneColor(...))`. `Paint` uses `ZoneShown(...).Scale(settings.Brightness)` for Direct devices with lights on; every other branch is unchanged. `LightingDeviceService.Paint` passes `tMs` through; `Preview` runs under `gate` over devices whose paint kind is Direct.
- [ ] **Step 4: Run both projects until they pass**, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): paint desk effects per zone"`.

### Task 5: Pipe messages

**Files:**
- Modify: `src/DeadWare.Core/DeviceHostProtocol.cs`
- Test: `tests/DeadWare.Tests/DeviceHostProtocolTests.cs`

**Interfaces:**
- Produces, each registered with `[JsonDerivedType]` under the given discriminator:

```csharp
public sealed record SetEffectRequest(string Kind,EffectParams? Params) : HostMessage;      // "setEffect"; Params is null for Static
public sealed record SetLayoutRequest(DeskLayout Layout) : HostMessage;                     // "setLayout"
public sealed record SubscribePreviewRequest(bool On) : HostMessage;                        // "subscribePreview"
public sealed record PreviewEvent(Dictionary<string,string> Colors) : HostMessage;          // "preview"
```

- [ ] **Step 1: Write the failing tests:** a round trip through `WriteAsync`/`ReadAsync` for each of the four, asserting every field (for `SetLayoutRequest`: a zone point, a tower member, an unplaced key and the desk rectangles); a `StateEvent` round trip whose `Settings` carries a non-default effect and layout; and `A_state_with_4096_layout_zones_fits_the_frame` (4096 zones with 60-character keys serialise under `MaxMessageBytes`).
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~DeviceHostProtocolTests`.
- [ ] **Step 3: Add the four records and their `[JsonDerivedType]` lines.** The source-generated context picks the new types up through `HostMessage`.
- [ ] **Step 4: Run until they pass**, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): pipe messages for effects, layout and preview"`.

### Task 6: The device host runs effects and owns the layout

**Files:**
- Modify: `src/DeadWare.Devices/DeviceHost.cs`
- Test: `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces: `public static readonly TimeSpan FastTick=TimeSpan.FromMilliseconds(33);` and handling of `SetEffectRequest` and `SetLayoutRequest`. `Clone` deep-copies `Effect` and `Layout`.

- [ ] **Step 1: Write the failing tests**, in the file's existing style (`Build`, `Clock`, `FakeOpenRgbSession`, waiting on signals). Two existing things need care first:
  - `AdvanceUntil` (near line 135) moves the clock only while `time.Waiting(DeviceHost.Tick)` holds, so it stalls once the loop waits `FastTick`. Give it a `tick` parameter, default `DeviceHost.Tick`, and pass `DeviceHost.FastTick` while an effect runs.
  - `The_largest_settings_and_a_large_device_list_fit_one_state_message` sizes its colours to just under 1 MiB from a settings object with no layout. Auto-arrange now adds points for its devices at the first connect, so compute its `Size()` from the helper's current settings (their layout and effect) with the colours swapped in. Its assertions stay.
  - New tests:
  - `Tick_is_fast_only_while_an_effect_runs_with_lights_on`: with Static the loop waits `DeviceHost.Tick` (`clock.Waiting(DeviceHost.Tick)`); after `SetEffect` Wave it waits `FastTick`; after `SetLightsOn(false)` it waits `Tick` again.
  - `Tick_is_slow_while_lights_are_off`: Wave selected, lights off: over several ticks every frame sent is black and the fake session records no two different frames.
  - `Frames_change_as_time_passes`: Wave, one placed Direct device: `AdvanceUntil` (tick `FastTick`) the fake session has recorded two different `UpdateLeds` frames for it.
  - `A_layout_too_large_to_save_is_not_adopted_at_a_rescan`: colours sized so that the points for a newly seen device would push the written file past 1 MiB: the layout is left as it was, one log line says so, and a later `SetBrightness` is still accepted.
  - `The_five_second_work_keeps_its_cadence_at_the_fast_tick`: with Wave running, the conflict scanner is called once per 5 s of fake time, not once per tick.
  - `A_new_device_is_arranged_and_saved_once`: after a rescan that adds a device, `store` holds a layout with points for its zones, the state event carries it, and a second rescan with no change saves nothing.
  - `Remap_runs_before_arrange`: a device that returns under a new key at the same location keeps its saved points (the Task 3 case, through the host).
  - `SetEffect_saves_and_refuses`: Wave with speed 70 is saved and in the next state; speed 101, a ninth colour, a bad colour and kind `"Plasma"` are each refused with the settings unchanged; kind `"Static"` with null params is accepted; a `Params` for Wave leaves the saved Breathing and Spectrum parameters alone.
  - `SetEffect_refused_when_the_save_fails`: reply `HostReplyCodes.CouldntSave`, effect unchanged.
  - `SetEffect_while_waiting_is_saved_and_painted_after_reconnect`: sent while the session is down: reply Ok, no OpenRGB call; after reconnect the frames are the effect's.
  - `SetLayout_places_a_device_the_draft_lacks`: live devices A and B; a `SetLayout` naming only A's zones is accepted and the saved layout also has points for B.
  - `SetLayout_refuses_an_invalid_layout` (a point at x = 2) and `SetLayout_keeps_entries_for_absent_devices`.
  - `Changing_effect_or_layout_never_rescans_or_switches_modes`: the fake session's `Reads` count and mode calls are the same before and after a `SetEffect` and a `SetLayout`.
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~DeviceHostTests`.
- [ ] **Step 3: Implement.**
  - Effect time: capture `time.GetTimestamp()` in the constructor; `double Now()` is the elapsed milliseconds. Every `service.Paint(settings)` call becomes `service.Paint(settings,Now())`.
  - The loop's delay is `LightingPaint.EffectRunning(settings)?FastTick:Tick`, read under `gate` each turn. `slow` stays driven by elapsed time, so its cadence is unchanged.
  - `RescanLocked`, after a change: `Remap` first; then `DeskLayouts.AutoArrange(settings.Layout,DeskLayouts.Arrangeable(service.Views(settings)))`; adopt the result and `SaveQuietly()` when either changed. Do the arranging on a clone of the settings and adopt it only if `LightingSettingsCodec.Validate` passes; otherwise keep the old layout and log once per distinct reason, or every later `Mutate` would be refused.
  - `Clone` uses `LightingEffects.Clone` and `DeskLayouts.Clone`.
  - `Apply`: `SetEffectRequest` → `Mutate`, which refuses an unknown kind by name, sets `Kind`, and when `Params` is not null replaces that kind's parameter set (a non-null `Params` with kind Static is refused). `SetLayoutRequest` → `Mutate`, which refuses a null layout, runs `DeskLayouts.Validate`, then stores `AutoArrange(DeskLayouts.Clone(r.Layout), arrangeable)`. `LightingSettingsCodec.Validate` already runs inside `Mutate`.
- [ ] **Step 4: Run the whole `DeadWare.Devices.Tests` project until it passes**, three times in a row, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): the device host runs desk effects and arranges the layout"`.

### Task 7: Preview frames

**Files:**
- Modify: `src/DeadWare.Devices/DeviceHost.cs`, `src/DeadWare.Devices/HostPipeServer.cs`, `src/DeadWare.Devices/Program.cs`
- Test: `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `tests/DeadWare.Devices.Tests/HostPipeServerTests.cs`

**Interfaces:**
- Produces:

```csharp
// DeviceHost
public static readonly TimeSpan PreviewGap=TimeSpan.FromMilliseconds(100);
public event Action<PreviewEvent>? PreviewChanged;
public void ClientGone();                                   // ends the subscription
// HostPipeServer: new optional constructor parameter, last
Action? clientGone=null
```

- [ ] **Step 1: Write the failing tests.**
  - Host: `No_preview_without_a_subscription`; `Preview_flows_to_a_subscriber_at_most_ten_a_second` (Wave running, subscribed: over 1 s of fake time between 2 and 10 events, each differing from the one before); `No_preview_while_static_or_lights_off`; `Preview_stops_when_the_client_goes` (after `ClientGone()` no further event, and a later subscribe starts it again); `Preview_is_before_brightness` (brightness 50, colours unscaled).
  - Pipe server: `Preview_never_displaces_a_pending_state` (while the client is not reading, send a state, then a preview, then let it read: it receives the state, then the preview); `Only_the_latest_preview_is_kept` (three previews queued while a request is being handled: one arrives); `ClientGone_is_called_when_a_client_disconnects`, including a client dropped for a write timeout.
- [ ] **Step 2: Run them to see them fail.**
- [ ] **Step 3: Implement.**
  - Host: `SubscribePreviewRequest` sets a `previewOn` flag under `gate` and replies Ok without raising a state. In `TickWork`, after a successful paint, when `previewOn` and `LightingPaint.EffectRunning(settings)` and at least `PreviewGap` has passed since the last frame, take `service.Preview(settings,Now())`; if it differs from the last one sent, raise `PreviewChanged` after leaving the lock. Subscribing clears the last-sent frame so the first frame always goes.
  - Pipe server: a second slot, `pendingPreview`, beside `pending`. `SendAsync` puts a `PreviewEvent` there and anything else in `pending`. `PumpAsync` writes `pending` first and `pendingPreview` only when `pending` is empty; `HasWork` and the teardown cover both. Call `clientGone` after the teardown's `writeGate.Release()` and before the loop waits for the next client: `ClientGone` takes the host's `gate`, which a tick can hold across several 3 s OpenRGB bounds, and that must not happen under `writeGate`.
  - Host: read `Now()` once per tick and use that value for both the paint and the preview.
  - `Program.cs`: pass `host.ClientGone` to the server and add `host.PreviewChanged+=e=>_=server.SendAsync(e);`.
- [ ] **Step 4: Run the whole `DeadWare.Devices.Tests` project until it passes**, three times in a row, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): preview frames for a subscribed window"`.

### Task 8: The window's client receives previews

**Files:**
- Modify: `src/DeadWare.Services/DeviceHostClient.cs`
- Test: `tests/DeadWare.App.Tests/DeviceHostClientTests.cs`

**Interfaces:**
- Produces:

```csharp
public event Action<IReadOnlyDictionary<string,string>>? Preview;   // raised on the client's read loop, like Changed
public void SetPreview(bool on);                                     // remembered; sent now if connected, and again after every reconnect
```

- [ ] **Step 1: Write the failing tests** with the file's fake host: `A_preview_event_raises_Preview_with_its_colours`; `SetPreview_sends_a_subscribe_request` (true then false); `The_subscription_is_sent_again_after_a_reconnect` (wanted on, the fake host drops the pipe, the client reconnects: the new connection receives `SubscribePreviewRequest(true)` after its `GetStateRequest`); `SetPreview_before_the_first_connection_is_sent_once_connected`; `A_preview_event_does_not_change_State_or_raise_Changed`.
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.App.Tests -c Release --filter FullyQualifiedName~DeviceHostClientTests`.
- [ ] **Step 3: Implement.** In the read loop, a `PreviewEvent` raises `Preview`. `SetPreview` stores the wanted value under `gate` and, when connected, sends the request through the existing `SendAsync` without awaiting the caller; a failed send is ignored, since the reconnect path sends it again. After each connect's `GetStateRequest`, send `SubscribePreviewRequest(true)` when wanted.
- [ ] **Step 4: Run the whole `DeadWare.App.Tests` project until it passes**, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): the window's client subscribes to preview frames"`.

### Task 9: Desk geometry for the page

**Files:**
- Create: `src/DeadWare.Core/DeskGeometry.cs`
- Test: `tests/DeadWare.Tests/DeskGeometryTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2, `DeviceView`.
- Produces (every function returns a new layout and never mutates its argument):

```csharp
public enum DeskItemKind { Device, Zone, Tower }
public sealed record DeskItem(DeskItemKind Kind,string? DeviceKey,string? ZoneKey,DeskPoint At,string Label,bool OwnEffect,IReadOnlyList<string> ZoneKeys);
public readonly record struct DeskBox(double X0,double Y0,double X1,double Y1);
public sealed record DeskScene(IReadOnlyList<DeskItem> Items,DeskBox Tower,int TowerDevices,IReadOnlyList<DeviceView> NotPlaced);
public static class DeskGeometry
{
    public const int Snap=48; public const double DragEpsilon=0.01,TowerHalf=0.06;
    public static DeskScene Scene(DeskLayout layout,IReadOnlyList<DeviceView> devices,bool towerOpen,IReadOnlySet<string> splitDevices);
    public static DeskItem? Hit(DeskScene scene,DeskPoint at,double halfWidth,double halfHeight);   // topmost: zones, then devices, then the tower
    public static bool InTower(DeskScene scene,DeskPoint at);
    public static DeskLayout MoveDevice(DeskLayout layout,IReadOnlyList<DeviceView> devices,string deviceKey,double dx,double dy);
    public static DeskLayout MoveZone(DeskLayout layout,string zoneKey,double dx,double dy);
    public static DeskLayout MoveTower(DeskLayout layout,IReadOnlyList<DeviceView> devices,double dx,double dy);
    public static DeskLayout SetInTower(DeskLayout layout,string deviceKey,bool inside);
    public static DeskLayout SetUnplaced(DeskLayout layout,IReadOnlyList<DeviceView> devices,string deviceKey,bool unplaced);
    public static DeskLayout WithShape(DeskLayout layout,DeskShape shape);
    public static DeskLayout Reset(DeskLayout layout,IReadOnlyList<DeviceView> devices);
    public static string Where(DeskLayout layout,DeviceView device);   // "On desk", "Inside PC", "Not placed", "Own effect"
}
```

- [ ] **Step 1: Write the failing tests.**
  - `Scene`: a device on the desk is one Device item at its first placed zone's point, labelled with its name; a tower member is absent while the tower is closed and present when open; a device in `splitDevices` gives one Zone item per placed zone, labelled with the zone's name; a hardware-kind device has `OwnEffect` true; an unplaced device is in `NotPlaced` and has no item; a device with no LEDs or `PaintKind.Unsupported` appears nowhere; `TowerDevices` counts only members that are arrangeable.
  - Tower box: closed, it is the anchor ± `TowerHalf`, clamped to the square; open, it is the bounds of its members' zone points; open with no members, the closed box.
  - `Hit`: a point inside a device's half extents returns it; a zone dot wins over the device under it; a miss returns null.
  - Moves: `MoveDevice` shifts all the device's points by the delta and snaps each to 1/48 (`0.5+0.013` → `25/48`), clamped to the square; `MoveZone` moves one; `MoveTower` moves the anchor and every member's points and nothing else.
  - `SetInTower(…,false)` removes the member and adds the exclusion; `true` does the reverse; repeating either changes nothing.
  - `SetUnplaced(…,true)` adds the key, removes the device's points and its membership; `false` removes the key and gives it points again (through `DeskLayouts.AutoArrange`).
  - `WithShape` replaces only `Desk`. `Reset` keeps `Desk`, clears points, members, exclusions and `Unplaced`, puts the anchor at (0.14, 0.3), and equals `AutoArrange` over an empty layout with that desk.
  - No function mutates its input (assert the original's `Zones` count and a point after each call).
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~DeskGeometryTests`.
- [ ] **Step 3: Implement `DeskGeometry.cs`**, following the same-named logic in `LayoutTab.tsx` (`bounds`, `towerBox`, `moveZones`, `moveTower`, `setMembership`). `Where` returns "Own effect" for a hardware-kind device first, then "Not placed", "Inside PC", "On desk".
- [ ] **Step 4: Run until they pass**, then the solution build.
- [ ] **Step 5: Commit:** `git commit -m "feat(lighting): desk geometry for the layout editor"`.

### Task 10: Effect tiles and settings on the Lighting page

**Files:**
- Modify: `src/DeadWare.WinUI/LightingPage.cs`
- Create: `src/DeadWare.WinUI/LightingEffectBar.cs`

**Interfaces:**
- Consumes: `SetEffectRequest`, `LightingEffects.Directions`, `NearestDirection`, `HostState.Settings.Effect`, the page's existing `Send`, `Throttle` and colour-picker flyout.
- Produces: `internal sealed class LightingEffectBar : StackPanel` with `public void Show(EffectSettings effect,bool live)` and the constructor `LightingEffectBar(DispatcherQueue queue,Action<HostMessage> send,UIElement staticRow)`. `Throttle`, today a private nested class of `LightingPage`, becomes an internal top-level class in `src/DeadWare.WinUI/Throttle.cs`, unchanged. The page keeps its **Set all colours** and Undo controls and handlers and passes their container as `staticRow`; the bar only shows it for Static.

- [ ] **Step 1: Build the bar.** Four tiles in a row (`Static` "your saved colours", `Wave` "sweeps across the desk", `Breathing` "everything pulses together", `Spectrum` "cycles through colours together"), as radio-style toggle buttons with accessible names; the selected tile sends `SetEffectRequest(kind, that kind's parameters from the last state)` at once. Under them, the selected kind's row: Static → Set all colours and Undo (moved, behaviour unchanged); Wave → up to 8 colour swatches each opening the colour flyout with a Remove button, an Add colour button hidden at 8, a Speed slider 0–100, a Direction `ComboBox` of the eight names, a Spread slider 0–100; Breathing and Spectrum → Speed only. Slider drags go through a per-control `Throttle`, as brightness does today, and send the final value on release.
- [ ] **Step 2: Wire it to state.** The bar keeps a working copy of each kind's parameters. Every control edits that copy and sends the whole copy, so a Spread send made before Speed's state has arrived can't put Speed back. `Show` replaces the working copy from the state, except the value of a control being dragged. `Show` sets the selected tile from `LightingEffects.KindOf(effect.Kind)`, fills the controls from that kind's parameters without re-sending, and picks the direction with `NearestDirection`. A control the user is dragging is not overwritten by an arriving state (the rule the brightness slider follows). All controls are disabled when `live` is false, and a refused reply shows through the page's existing send-error line.
- [ ] **Step 3: Build:** `dotnet build DeadWare.slnx -c Release`. Expected: 0 errors, `src/DeadWare.WinUI` 0 warnings.
- [ ] **Step 4: Commit:** `git commit -m "feat(lighting): effect tiles and settings on the Lighting page"`.

### Task 11: The desk card, device list and panel

**Files:**
- Create: `src/DeadWare.WinUI/DeskView.cs`
- Modify: `src/DeadWare.WinUI/LightingPage.cs`

**Interfaces:**
- Consumes: Task 9's `DeskGeometry`, Task 8's `Preview` and `SetPreview`, `ConnectionMapView.cs` as the Win2D pattern, `Lifetime.WhileShowing`.
- Produces: `internal sealed class DeskView : UserControl` with

```csharp
public void Show(DeskLayout layout,IReadOnlyList<DeviceView> devices,bool lightsOn,bool live);
public void ShowColors(IReadOnlyDictionary<string,string>? colors);   // null: use each zone's colour from state
public string? Selected {get;set;}                                     // a device key, "tower", or null
public bool Editing {get;set;}
public event Action<string?>? SelectionChanged;
public event Action<DeskLayout>? LayoutEdited;                         // one per completed edit
```

- [ ] **Step 1: Draw the desk.** A Win2D `CanvasControl` in a card titled **Desk**, with "BACK OF DESK" above and "FRONT · YOUR SEAT" below. Draw the layout's desk rectangles as a muted outline, then `DeskGeometry.Scene(...)`'s items: a rounded box per device with its name and "N zones", edged and softly filled with its colour (the average of its zones' colours); grey with an "own effect" tag when `OwnEffect`; unlit when `lightsOn` is false; a labelled dot per Zone item; the tower as a box labelled "PC tower · N devices". The selected item gets the accent outline. Redraw only from `Show`, `ShowColors`, selection and pointer input. Colours come from theme resources so both themes work.
- [ ] **Step 2: Select.** A tap on an item selects it and raises `SelectionChanged`; a tap on the tower's label toggles it open; selecting a tower member opens the tower; a tap on empty desk clears the selection.
- [ ] **Step 3: Replace the device cards with the panel and the list.** Remove the page's card list. To the right of the desk card (below it under about 900 px of width) put the panel for the selected device: its name, then exactly what its `DeviceCard` showed (the `ZoneRow`s with their flyouts, the own-effect and zero-zone notes), reusing those classes; with nothing selected, "Select a device on the desk or in the list." Under the desk put a `ListView` of every device from the state with `DeskGeometry.Where(...)` as its second line, kept in step with the desk's selection both ways, and under it **Not placed** with a **Place on desk** button per device. The step 1 rules are kept: a relaunched helper's non-Running state disables the panel in place and removes nothing; open flyouts get their own disable; a send error survives until the next success or user action.
- [ ] **Step 4: Live colours.** In `Attach`, call `client.SetPreview(true)` and handle `Preview` by marshalling to the dispatcher and calling `ShowColors`; in `Detach`, `SetPreview(false)` and unhook. On every state for which `LightingPaint.EffectRunning(state.Settings)` is false, call `ShowColors(null)`, and drop any `Preview` frame that arrives while the latest state says so: a frame computed just before the switch can arrive after that state, and no later frame would replace it. A replaced client (lighting toggled in Settings) is re-hooked where the page already re-hooks `Changed`.
- [ ] **Step 5: Build:** `dotnet build DeadWare.slnx -c Release`, 0 errors and `src/DeadWare.WinUI` 0 warnings; run the three test projects with the measurement filter.
- [ ] **Step 6: Commit:** `git commit -m "feat(lighting): desk card, device list and device panel"`.

### Task 12: Edit layout

**Files:**
- Modify: `src/DeadWare.WinUI/DeskView.cs`, `src/DeadWare.WinUI/LightingPage.cs`

**Interfaces:**
- Consumes: `DeskGeometry` moves, `SetLayoutRequest`, `DeskLayouts.ShapeOf`.

- [ ] **Step 1: The toggle and its tools.** An **Edit layout** toggle button on the desk card. While on, show a **Desk shape** `ComboBox` (Straight, L left, L right; selected from `ShapeOf`, blank when it matches none) and **Reset positions**, which asks "Arrange every device from scratch? The desk shape is kept." in a `ContentDialog` before sending. The panel gains **Inside PC tower** (checkbox), **Move zones separately** (toggle; adds or removes the device in the view's split set) and **Take off the desk**. Leaving the page (`Detach`) turns Edit layout off.
- [ ] **Step 2: Drag.** With Editing on, pointer-pressed on an item captures the pointer; movement under `DeskGeometry.DragEpsilon` in both axes is a click. Past that, the view keeps a draft layout made with `MoveDevice`, `MoveZone` or `MoveTower` from the layout at press time and draws from it. On release: for a device box, apply `SetInTower` when the drop point's `InTower` differs from its membership; raise `LayoutEdited` once with the result. Pointer-cancel and capture-lost discard the draft. A second pointer is ignored while one drag is active.
- [ ] **Step 3: Send and hold.** The page sends `SetLayoutRequest` for each `LayoutEdited`, each checkbox, the shape, a nudge and the reset. Every edit and nudge is applied to the current draft when there is one, else to the layout from the latest state, so rapid arrow presses add up. The page counts outstanding `SetLayout` requests; the view drops its draft only at the first state that arrives with none outstanding. A refusal drops the draft at once and shows the error through the page's send-error line. Arriving states never overwrite a draft in flight.
- [ ] **Step 4: Keyboard.** With Editing on and a device selected, the arrow keys send one `MoveDevice` (or `MoveTower` for the tower) of 1/48 per press; the canvas is a tab stop with an accessible name that states the selection and its position ("Apex Pro, on desk, column 24 of 48, row 30 of 48").
- [ ] **Step 5: Build and test** as in Task 11 Step 5.
- [ ] **Step 6: Commit:** `git commit -m "feat(lighting): edit the desk layout"`.

### Task 13: Guide, release note and package

**Files:**
- Modify: `docs/user-guide.md` (the Lighting section and the feature list line), `docs/releases/v0.19.0.md`

- [ ] **Step 1: Write the guide text** in the guide's existing voice: the four effects and their settings; that Breathing pulses each zone's own colour and Spectrum uses the full colour circle; the desk card, the PC tower, the device list and Not placed; Edit layout (drag, drop on the tower, arrow keys, desk shape, Reset positions); that new devices are placed automatically; that the lights take a new position when the drag is dropped; that a device on its own effect, such as the Corsair Lighting Node Pro, keeps it under every effect; that effects keep running with the window closed. Claim only what the code does.
- [ ] **Step 2: Add the same points to `docs/releases/v0.19.0.md`**, briefly. `CHANGELOG.md` and `docs/validation.md` are left for release time.
- [ ] **Step 3: Run the publish gate on a clean tree:** `powershell -File scripts/publish.ps1`. Expected: exit 0, all three test projects green, the package within the updater's limits (2048 entries, 256 MiB compressed, 768 MiB expanded). Record the test counts and package sizes in the SDD ledger.
- [ ] **Step 4: Commit:** `git commit -m "docs(lighting): desk effects and layout in the guide and release note"`.

### Task 14: Hardware check with the owner

Not run by an agent. The controller walks the owner through the spec's ten-step hardware check, each step approved first, using owner-approved elevated scripts in the manner of step 1's `h6-*.ps1`. Before it: check that the owner is not gaming; ask for a full power-off to see whether the graphics card's RGB is detected again. At step 5, also watch for a hitch in the Wave every 5 seconds: the conflict scan runs inline in the loop and may pause the fast tick. Results and any findings go in the SDD ledger; fixes follow the normal fix-round procedure.

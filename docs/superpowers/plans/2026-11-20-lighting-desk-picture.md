# Lighting step 2 redesign: the desk as a picture. Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On the owner's PC the Lighting page's desk card is the approved picture of their desk and the inside of their PC, with every lit part in its live colour; other PCs keep the plain desk built in step 2.

**Architecture:** A picture is data in `DeadWare.Core`: parts (what they are, where they are drawn, which device and zone light them) and furniture. Core decides which picture applies, resolves parts to devices, gives each zone its point for Wave and answers what a tap hits. The device host replaces the layout with the picture's when one applies. A new WinUI control draws the picture from that data; the existing `DeskView` and its editor are untouched and stay for other PCs. (The spec says `DeskView` "gains a picture mode"; a separate control is a deliberate change of structure with the same result, chosen so the reviewed editor code cannot regress.)

**Tech Stack:** .NET 10, WinUI 3 (Windows App SDK 2.6.5), Win2D, xUnit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-11-20-lighting-desk-picture-design.md` (approved 2026-11-20). It amends `docs/superpowers/specs/2026-11-19-lighting-step2-desk-design.md`. The look is fixed by the approved mockups in `docs/superpowers/specs/2026-11-20-lighting-desk-redesign-mockups/`: `lighting-page-desk.png`, `lighting-page-inside.png`, and their source `page.html`, whose two SVG drawings use a 720 by 450 box. Read the spec and open both PNGs before starting any task.

## Global Constraints

- **Where:** worktree `I:\DeadWare\.claude\worktrees\lighting-step2`, branch `claude/lighting-step2` (draft PR #138). `cd` there in every command. Never run git or dotnet in `I:\DeadWare` itself.
- **No mutating git by implementers.** The controller gates and commits.
- **Never launch** DeadWare, `DeadWare.Devices.exe`, OpenRGB or DeadLight. A test build of an earlier commit is running on this PC from `artifacts\package-d3e45f407f3d48f485a3d1ec9f914830`; do not touch that folder or its processes.
- **Naming:** no file, class, member or log may be called "helper". User-facing text may say "lighting helper".
- **Versions stay at 1** (`LightingSettings.CurrentVersion`, `DeskLayout.Version`, `DeviceHostProtocol.Version`). The new layout field is optional on read.
- **The picture's name** is `b650m-link-hub`. **Signature devices**, by exact name: `MSI MAG B650M MORTAR WIFI (MS-7D76)` and `Corsair iCUE Link System Hub`.
- **Drawing box:** both views are drawn in a box 720 wide by 450 high, numbers taken from `page.html`. A desk-view point maps to the wave's unit square as `(x/720, y/450)`.
- **Wave region for the inside of the PC:** the case box `(140, 22, 440, 418)` of the inside view maps onto x 0.03 to 0.30 and y 0.60 to 0.98 of the unit square.
- **Nothing visible beyond the mockups.** If a task needs a visible element the mockups don't show, stop and report it; the owner sees a picture first. The mockups' "On other PCs" card and the two dashed notes are not built.
- **Step 2's working parts are not disturbed:** the effect maths, `LightingPaint`, the helper's loop and preview, `DeskView`, `DeskGeometry`, `LayoutDraftGate`, `LightingEffectBar`. A task that finds it must change one of them stops and reports.
- **Tests:** `src/DeadWare.WinUI` has no test project; logic that needs tests goes in Core. Helper tests wait on observable signals.
- **Build and tests:** `dotnet build DeadWare.slnx -c Release`, 0 errors and no new warnings. Test projects with `--filter "FullyQualifiedName!~MeasurementTests"`.
- **Commits** end with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. Never tag.

## Review Focus

1. **A device the picture expects isn't found** (the graphics card after a hard reset, the mouse unplugged, one RAM stick). The part is drawn unlit and marked "not found", can't be tapped, and nothing else moves. Tests: Task 1.
2. **A signature device is briefly missed.** The owner's page must not flip to the plain desk and lose the picture. Tests: Task 1 (`Applies`) and Task 2.
3. **A `lighting.json` from before the picture** (auto-arranged points, dragged devices, a tower with members). It is replaced whole, with no stale points left behind. Test: Task 2.
4. **The same device name twice** (two RAM sticks) or under another name (the mouse wired and wireless). Tests: Task 1.
5. **A PC that is not the owner's.** No picture applies, and step 2's plain desk and editor behave exactly as before. Tests: Task 2 and the unchanged step 2 suites.

---

### Task 1: The picture model, the owner's picture and its rules

**Files:**
- Create: `src/DeadWare.Core/DeskPicture.cs`
- Modify: `src/DeadWare.Core/DeskLayout.cs` (class `DeskLayout`, `DeskLayouts.Clone`, `DeskLayouts.Validate`; add `DeskLayouts.Same`)
- Test: `tests/DeadWare.Tests/DeskPictureTests.cs`, `tests/DeadWare.Tests/DeskLayoutTests.cs`, `tests/DeadWare.Tests/LightingSettingsCodecTests.cs`

**Interfaces:**
- Consumes: `DeskPoint`, `DeskLayout`, `DeskLayouts.Arrangeable`, `DeviceView`, `ZoneView`, `PaintKind`, `LightingKeys.Zone`.
- Produces:

```csharp
// DeskLayout gains
public string? Picture {get;set;}                      // null or absent: the plain desk
// DeskLayouts gains
public static bool Same(DeskLayout a,DeskLayout b);    // Zones equal as key-to-point maps whatever their order; the lists equal in order; Tower.At, the desk rectangles, Version and Picture equal

public enum PictureView { Desk, Inside }
public enum PartKind { Keyboard, Mouse, FanBar, FanRing, PumpRing, RamStick, Card }
public readonly record struct PicBox(double X,double Y,double W,double H) { public double CenterX=>X+W/2; public double CenterY=>Y+H/2; }
public sealed record PicturePart(string Id,PartKind Kind,PictureView View,PicBox Box,string Label,string DeviceName,bool Prefix,int Ordinal,IReadOnlyList<int> Zones,int ShownZone);
public sealed record DeskPicture(string Name,IReadOnlyList<string> Signature,PicBox Tower,PicBox Case,IReadOnlyList<PicturePart> Parts,IReadOnlyDictionary<string,string> OffPictureNames);   // device name -> how "Not in the picture" names it
public sealed record ResolvedPart(PicturePart Part,string? DeviceKey,IReadOnlyList<string> ZoneKeys,string? ShownZoneKey,bool OwnEffect) { public bool Found=>DeviceKey is not null; }
public sealed record PictureScene(DeskPicture Picture,IReadOnlyList<ResolvedPart> Parts,IReadOnlyList<string> NotInPicture);
public static class DeskPictures
{
    public const string OwnerRigName="b650m-link-hub";
    public static DeskPicture OwnerRig {get;}
    public static DeskPicture? Named(string? name);
    public static DeskPicture? Applies(IEnumerable<string> deviceNames,string? savedPicture);
    public static PictureScene Resolve(DeskPicture picture,IReadOnlyList<DeviceView> devices);
    public static DeskPoint PointOf(DeskPicture picture,PicturePart part);
    public static DeskLayout Layout(DeskPicture picture,IReadOnlyList<DeviceView> devices);
    public static ResolvedPart? Hit(PictureScene scene,PictureView view,double x,double y);   // x, y in the 720 by 450 box
    public static bool HitTower(DeskPicture picture,double x,double y);                         // desk view only
}
```

- `PicturePart.Zones` are zone indexes; `ShownZone` is the index whose colour the part shows and which a tap selects. `Ordinal` picks among several devices matching the name, in ordinal order of device key (0 first).
- `ResolvedPart.OwnEffect` is true when the device's `Kind` is `PaintKind.Hardware`.
- `PictureScene.NotInPicture` holds one entry per lit device with no part, as `"<name> (<n> zone[s])"`, where the name is the picture's `OffPictureNames` entry for that device name, else the device name, and n counts its zones with LEDs; device-list order. It lists whole devices only; a device with a part has all its lit zones in the picture.
- The model holds no furniture and no captions. The grey shapes and the drawing's text are drawn by Task 3 straight from `page.html`. `Part.Label` is for the panel, the accessible name and tests.

**The owner's picture.** Boxes are `(x, y, w, h)` in the 720 by 450 box, from `page.html`.

| Id | Kind | View | Box | Label | Device name | Prefix | Ordinal | Zones | Shown |
|---|---|---|---|---|---|---|---|---|---|
| `keyboard` | Keyboard | Desk | 290, 96, 200, 58 | Keyboard | `SteelSeries Apex Pro` | no | 0 | 0 | 0 |
| `mouse` | Mouse | Desk | 555, 108, 22, 36 | Mouse | `SteelSeries Aerox 9` | yes | 0 | 0, 1, 2, 3 | 1 |
| `fan1` | FanBar | Inside | 160, 38, 118, 26 | Radiator fan 1 | `Corsair iCUE Link System Hub` | no | 0 | 0 | 0 |
| `fan2` | FanBar | Inside | 290, 38, 118, 26 | Radiator fan 2 | same | no | 0 | 1 | 1 |
| `fan3` | FanBar | Inside | 420, 38, 118, 26 | Radiator fan 3 | same | no | 0 | 2 | 2 |
| `pump` | PumpRing | Inside | 276, 138, 60, 60 | Pump | same | no | 0 | 3 | 3 |
| `ram1` | RamStick | Inside | 392, 112, 8, 112 | RAM 1 | `Corsair Vengeance RGB DDR5` | no | 0 | 0 | 0 |
| `ram2` | RamStick | Inside | 408, 112, 8, 112 | RAM 2 | same | no | 1 | 0 | 0 |
| `gpu` | Card | Inside | 170, 270, 290, 50 | Graphics card | `Gigabyte Radeon RX 6800 XT` | yes | 0 | 0, 1, 2, 3, 4 | 0 |
| `front1` | FanRing | Inside | 502, 114, 52, 52 | Front fans | `Corsair Lighting Node Pro` | no | 0 | 0 | 0 |
| `front2` | FanRing | Inside | 502, 190, 52, 52 | Front fans | same | no | 0 | 0 | 0 |
| `front3` | FanRing | Inside | 502, 266, 52, 52 | Front fans | same | no | 0 | 0 | 0 |

`Tower` = `(40, 388, 74, 56)` (desk view). `Case` = `(140, 22, 440, 418)` (inside view). `OffPictureNames` = `MSI MAG B650M MORTAR WIFI (MS-7D76)` → `MSI motherboard headers`.

- [ ] **Step 1: Write the failing tests.** Load the device list from `tests/DeadWare.Devices.Tests/Fixtures/controllers.json` is not possible from this project, so declare two helpers in the test class: `static List<DeviceView> Rig(string mouse="SteelSeries Aerox 9 Wireless Wired")`, which builds the eight `DeviceView`s by hand with the names, zone names and LED counts of that file (the Node Pro with `PaintKind.Hardware` and its zone 1 at 0 LEDs; the board with `JRGB1`, `JRGB2` at 1 LED and two 0-LED zones; every other device `PaintKind.Direct`; the two RAM sticks keyed `"RAM|B"` then `"RAM|A"`, so key order differs from list order; every other key `name + "|loc"`), and `static string Key(string name)=>name+"|loc"`.

```csharp
[Fact] public void Applies_needs_both_signature_devices_unless_already_in_use()
{
    var board="MSI MAG B650M MORTAR WIFI (MS-7D76)";var hub="Corsair iCUE Link System Hub";
    Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([board,hub,"x"],null));
    Assert.Null(DeskPictures.Applies([board],null));
    Assert.Null(DeskPictures.Applies([hub],null));
    Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([board],DeskPictures.OwnerRigName));
    Assert.Same(DeskPictures.OwnerRig,DeskPictures.Applies([hub],DeskPictures.OwnerRigName));
    Assert.Null(DeskPictures.Applies(["x"],DeskPictures.OwnerRigName));
    Assert.Null(DeskPictures.Applies([board],"some-other-picture"));
    Assert.Null(DeskPictures.Named("nope"));Assert.Null(DeskPictures.Named(null));
}
[Fact] public void Every_part_resolves_to_its_device_and_zone()
{
    var s=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig());
    ResolvedPart P(string id)=>s.Parts.Single(p=>p.Part.Id==id);
    Assert.Equal(12,s.Parts.Count);Assert.All(s.Parts,p=>Assert.True(p.Found));
    Assert.Equal(LightingKeys.Zone(Key("SteelSeries Apex Pro"),0),P("keyboard").ShownZoneKey);
    Assert.Equal(LightingKeys.Zone(Key("SteelSeries Aerox 9 Wireless Wired"),1),P("mouse").ShownZoneKey);Assert.Equal(4,P("mouse").ZoneKeys.Count);
    Assert.Equal(LightingKeys.Zone(Key("Corsair iCUE Link System Hub"),2),P("fan3").ShownZoneKey);
    Assert.Equal(LightingKeys.Zone(Key("Corsair iCUE Link System Hub"),3),P("pump").ShownZoneKey);
    Assert.Equal("RAM|A",P("ram1").DeviceKey);Assert.Equal("RAM|B",P("ram2").DeviceKey);        // by key order, not list order
    Assert.Equal(5,P("gpu").ZoneKeys.Count);
    Assert.True(P("front2").OwnEffect);Assert.False(P("fan1").OwnEffect);
    Assert.Equal(new[]{"MSI motherboard headers (2 zones)"},s.NotInPicture);
}
[Fact] public void A_missing_device_is_not_found_and_nothing_else_moves()
{
    var rig=Rig().Where(d=>!d.Name.StartsWith("Gigabyte")&&d.Key!="RAM|A").ToList();
    var s=DeskPictures.Resolve(DeskPictures.OwnerRig,rig);
    Assert.False(s.Parts.Single(p=>p.Part.Id=="gpu").Found);
    Assert.Equal("RAM|B",s.Parts.Single(p=>p.Part.Id=="ram1").DeviceKey);Assert.False(s.Parts.Single(p=>p.Part.Id=="ram2").Found);
    Assert.Null(DeskPictures.Hit(s,PictureView.Inside,315,295));                                  // the card's box: not found, so not tappable
}
[Fact] public void The_mouse_resolves_under_its_wireless_name()=>Assert.True(DeskPictures.Resolve(DeskPictures.OwnerRig,Rig("SteelSeries Aerox 9 Wireless")).Parts.Single(p=>p.Part.Id=="mouse").Found);
[Fact] public void Desk_parts_sit_at_their_centres_and_the_case_fills_its_wave_region()
{
    var r=DeskPictures.OwnerRig;DeskPoint At(string id)=>DeskPictures.PointOf(r,r.Parts.Single(p=>p.Id==id));
    Assert.Equal(390/720.0,At("keyboard").X,6);Assert.Equal(125/450.0,At("keyboard").Y,6);
    Assert.Equal(0.03+0.27*(219-140)/440.0,At("fan1").X,6);Assert.Equal(0.60+0.38*(51-22)/418.0,At("fan1").Y,6);
    Assert.True(At("fan1").X<At("fan2").X&&At("fan2").X<At("fan3").X&&At("fan3").X<At("keyboard").X);
    Assert.All(r.Parts.Where(p=>p.View==PictureView.Inside),p=>{var a=DeskPictures.PointOf(r,p);Assert.InRange(a.X,0.03,0.30);Assert.InRange(a.Y,0.60,0.98);});
}
[Fact] public void Layout_is_exactly_the_pictures()
{
    var l=DeskPictures.Layout(DeskPictures.OwnerRig,Rig());
    Assert.Equal(DeskPictures.OwnerRigName,l.Picture);Assert.Null(DeskLayouts.Validate(l));
    Assert.Equal(1+4+4+1+1+5+1,l.Zones.Count);                                                   // keyboard, mouse, hub, two sticks, card, Node Pro channel 1
    Assert.Equal(l.Zones[LightingKeys.Zone(Key("Gigabyte Radeon RX 6800 XT GAMING OC"),0)],l.Zones[LightingKeys.Zone(Key("Gigabyte Radeon RX 6800 XT GAMING OC"),4)]);
    Assert.Equal(new[]{Key("MSI MAG B650M MORTAR WIFI (MS-7D76)")},l.Unplaced);
    Assert.Empty(l.Tower.Members);Assert.Empty(l.Tower.Excluded);
    Assert.True(DeskLayouts.Same(l,DeskPictures.Layout(DeskPictures.OwnerRig,Rig())));
}
[Fact] public void Taps_hit_found_parts_in_their_own_view_only()
{
    var s=DeskPictures.Resolve(DeskPictures.OwnerRig,Rig());
    Assert.Equal("keyboard",DeskPictures.Hit(s,PictureView.Desk,390,125)!.Part.Id);
    Assert.Equal("ram1",DeskPictures.Hit(s,PictureView.Inside,396,170)!.Part.Id);Assert.Equal("ram2",DeskPictures.Hit(s,PictureView.Inside,412,170)!.Part.Id);
    Assert.Equal("ram1",DeskPictures.Hit(s,PictureView.Inside,386,170)!.Part.Id);                 // a thin part has a tap area at least 28 wide, split fairly with its neighbour
    Assert.Null(DeskPictures.Hit(s,PictureView.Inside,260,120));                                   // board area: clear of the pump, the fan bars and RAM stick 1's tap area
    Assert.DoesNotContain(new[]{(390.0,125.0),(566.0,126.0)},at=>DeskPictures.Hit(s,PictureView.Inside,at.Item1,at.Item2)?.Part.View==PictureView.Desk); // desk parts are never hit in the inside view
    Assert.Equal("front2",DeskPictures.Hit(s,PictureView.Inside,528,216)!.Part.Id);                // own-effect parts can be tapped
    Assert.Null(DeskPictures.Hit(s,PictureView.Desk,360,50));                                      // the main monitor: furniture
    Assert.True(DeskPictures.HitTower(DeskPictures.OwnerRig,77,416));Assert.False(DeskPictures.HitTower(DeskPictures.OwnerRig,390,125));
}
```

In `DeskLayoutTests`: `Clone` copies `Picture`; `Validate` refuses a `Picture` longer than 64 characters and accepts null; `Same` is true for equal layouts and false when a point, a member, the order of `Unplaced`, the desk rectangles or `Picture` differs. In `LightingSettingsCodecTests`: a layout with `Picture` round-trips, and a file without the member reads as null.

- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~DeskPictureTests|FullyQualifiedName~DeskLayoutTests|FullyQualifiedName~LightingSettingsCodecTests"`.
- [ ] **Step 3: Implement.** `PointOf`: a Desk part → `(Box.CenterX/720, Box.CenterY/450)`; an Inside part → `x = 0.03 + 0.27·(CenterX − Case.X)/Case.W`, `y = 0.60 + 0.38·(CenterY − Case.Y)/Case.H`. `Resolve`: for each part, the devices whose name equals `DeviceName` (or starts with it when `Prefix`), sorted by key ordinally, take the one at `Ordinal`; zone keys from `LightingKeys.Zone(device.Key, index)` for each listed index the device has; a device counts for `NotInPicture` when `DeskLayouts.Arrangeable` keeps it and no part resolved to it. `Layout`: a new `DeskLayout` with `Picture` set, `Zones` = every resolved part's zone keys at `PointOf` (assign with the indexer: the three front fans share one zone key), `Unplaced` = keys of arrangeable devices with no part (device-list order), default tower and desk. `Hit`: found parts of that view only; the tap area is the part's box grown about its centre to at least 28 by 28; where two areas overlap, the nearer centre wins. `Applies`: all signature names present, or `savedPicture` names this picture and at least one is present.
- [ ] **Step 4: Run until they pass**, the whole `tests/DeadWare.Tests` project once, then the solution build.
- [ ] **Step 5: Commit** (controller): `feat(lighting): a desk picture model and the owner's rig`.

### Task 2: The device host uses the picture's layout

**Files:**
- Modify: `src/DeadWare.Devices/DeviceHost.cs` (`RescanLocked`, `SetLayout`)
- Test: `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`

**Interfaces:**
- Consumes: Task 1's `DeskPictures.Applies / Layout`, `DeskLayouts.Same`, `DeskGeometry.Reset`.
- Produces: no new public surface. `SetLayout` is refused with the exact text `This PC's layout comes from its built-in picture.` while the live layout's `Picture` is not null.

- [ ] **Step 1: Keep step 2's tests on another PC.** Every host in `DeviceHostTests` is built from `FakeOpenRgbSession.FromFixture()`, whose eight devices include both signature devices, so after this task they would all run in picture mode: the tests that send `SetLayout`, assert tower members, or count on auto-arranged points would fail for the wrong reason. Add a parameter, `FakeOpenRgbSession.FromFixture(bool owner=false)`, that renames the hub to `Corsair iCUE Link Hub (test)` unless `owner` is true, so the existing tests keep running as a PC with no picture, unchanged. Only the new tests below pass `owner:true`. If any existing test still has to change, list it and why in the report; do not change its intent.
- [ ] **Step 2: Write the failing tests**, with the file's helpers:
  - `The_picture_replaces_the_layout_at_connect`: seed `lighting.json` with a plain layout holding an auto-arranged point for the keyboard, a point for an absent key `"gone|x#0"`, a tower member and an exclusion. After connect: the saved layout names `b650m-link-hub`, equals `DeskPictures.Layout(OwnerRig, views)`, has no `"gone|x#0"`, and the state carries it.
  - `A_rescan_that_changes_nothing_in_the_picture_saves_nothing`: reorder the controllers with `session.Reorder` (the device list changes shape, the picture's layout does not) and rescan: the save count is unchanged.
  - `The_picture_survives_the_hub_going_missing`: remove the hub and rescan: the layout still names the picture, the hub's zones have no points, everything else is where it was.
  - `Losing_both_signature_devices_returns_to_the_plain_desk`: remove hub and board: `Picture` is null, the remaining devices are auto-arranged as `DeskGeometry.Reset` gives, saved once.
  - `No_picture_on_another_pc`: with the hub renamed, the layout is auto-arranged exactly as before this task and `Picture` is null (compare with `DeskLayouts.AutoArrange` over an empty layout).
  - `SetLayout_is_refused_while_a_picture_applies`: reply not Ok with the exact text; layout and file unchanged. And accepted as before when no picture applies.
  - `SetLayout_refuses_a_layout_that_names_a_picture`: on a PC with no picture, a `SetLayout` whose layout has `Picture` set is refused with "A window can't choose the desk picture." and nothing is saved.
  - `Wave_follows_the_pictures_points`: Wave with colours `#FF0000`, `#00FF00`, `#0000FF`, brightness 100. Under the fake clock, take one frame and its effect time `t` (read the time the same way the file's existing effect tests do), and assert that the first LED of the hub's zone 0, of its zone 2, and of the keyboard equal `LightingEffects.Evaluate(EffectKind.Wave, DeskPictures.PointOf(rig, part), t, wave, zoneColour)` for `fan1`, `fan3` and `keyboard`, and that the three are different colours. Auto-arranged points would fail this.
- [ ] **Step 3: Run them to see them fail:** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~DeviceHostTests`. The existing tests must still pass at this point.
- [ ] **Step 4: Implement.** In `RescanLocked`, after `Remap`, on the clone that step 2 already validates before adopting: `var views = service.Views(candidate); var picture = DeskPictures.Applies(views.Select(v => v.Name), candidate.Layout.Picture);` then: picture applies → `DeskPictures.Layout(picture, views)`; no picture but `candidate.Layout.Picture` is not null → `DeskGeometry.Reset(candidate.Layout, views)` with `Picture` null; otherwise `AutoArrange` as today. Save when `Remap` changed something or the new layout is not `DeskLayouts.Same` as the current one, and adopt only if `LightingSettingsCodec.Validate` passes, as today. Leaving a picture resets the desk shape to the default as well, since the picture's layout carries the default desk. `SetLayout`: refuse first when the live `settings.Layout.Picture` is not null (the built-in picture text), and refuse any `r.Layout.Picture` that is not null ("A window can't choose the desk picture."): only the device host sets that field. Everything else in the host stays as it is; in particular this path runs wherever `RescanLocked` already arranges (a changed device list, and the first connect).
- [ ] **Step 5: Run the whole `DeadWare.Devices.Tests` project three times in a row**, then `tests/DeadWare.Tests` and the solution build.
- [ ] **Step 6: Commit** (controller): `feat(lighting): the device host lays the owner's rig out from its picture`.

### Task 3: Draw the picture

**Files:**
- Create: `src/DeadWare.WinUI/DeskPictureView.cs`

**Interfaces:**
- Consumes: Task 1; `ConnectionMapView.cs` and `DeskView.cs` as the Win2D house pattern (canvas lifetime through `ShowSurface`/`HideSurface`, text layouts with an explicit positive width, redraw only on input).
- Produces:

```csharp
internal sealed class DeskPictureView : UserControl
{
    public void Show(PictureScene scene,bool lightsOn,bool live,bool waveRunning,double waveAngleDeg);
    public void ShowColors(IReadOnlyDictionary<string,string>? colors);   // zone key -> #RRGGBB; null: use the colours given by SetStateColors
    public void SetStateColors(IReadOnlyDictionary<string,string> colors); // zone key -> the state's colour, used when no frame applies
    public PictureView View {get;set;}                                     // setting it raises nothing
    public string? SelectedPart {get;set;}                                 // a part id; setting it raises nothing
    public event Action<ResolvedPart?>? PartTapped;                        // null: a tap on empty space or furniture
    public event Action? TowerTapped;
}
```

- [ ] **Step 1: The stage.** One Win2D `CanvasControl`, drawn in the 720 by 450 box scaled to the control's width (aspect fixed, at most 900 wide, centred). The drawing has the mockups' dark background in both themes, because it shows lights. Colours are the mockups': wood `#2B211A` with edge `#4B3A2B`, grey furniture `#3A4556` and `#9AA6B6`, labels `#C9D3E0`, muted labels `#7C899B`, accent `#5EE0FF`.
- [ ] **Step 2: The desk view**, shape for shape and caption for caption from `page.html`'s first SVG, line by line: the L-shaped desk path, the curved main monitor, the speakers, the second monitor with its rotated caption, the headphones and amp, the mouse mat and the "you" circle are furniture, drawn from the SVG's own numbers (they are not in Core). Lit parts take their boxes from Core's `PicturePart.Box`, which are the same numbers. The keyboard as a rounded outline filled dark with four dashed key rows, all in the keyboard's colour; the mouse as an ellipse in its colour on its mat; each lit part with a soft glow of its own colour; the tower as a box with a small glow strip, pump ring and RAM lines in the colours of radiator fans 1 to 3, the pump and the RAM, labelled "PC tower" and "tap to see inside ↗"; "BACK OF DESK" above. Under the keyboard "Keyboard", or "Keyboard · selected" in the accent colour when it is the selected part; under the mouse "Mouse" likewise. While `waveRunning`: a soft band across the middle at right angles to `waveAngleDeg`, an arrow and the caption "wave moving <direction name in lower case>" from `LightingEffects.Directions[LightingEffects.NearestDirection(angle)]`. Nothing moves by itself.
- [ ] **Step 3: The inside view**, shape for shape from the second SVG: the case, "rear" and "front", the three radiator fan bars, the unlit rear fan, the board area, the pump ring, the RAM sticks, the card with `GIGABYTE` lit in the card's colour, three front fan rings, the power supply shroud, and the captions exactly as the SVG has them, shared ones included ("radiator fans 1–3 · Corsair iCUE LINK hub", "pump", "RAM ×2", "graphics card", "front fans ×3", "rear fan, unlit", "power supply"). A part with `OwnEffect` is drawn grey, with "own effect" under the front fans' caption, once for the three.
- [ ] **Step 4: States.** A part that is not `Found`: unlit outline, no glow, and "not found" in small muted text centred on the part itself (beside it for a RAM stick), so it reads correctly under a shared caption. `lightsOn` false: every part unlit. `live` false: the whole drawing at reduced opacity and taps ignored. The selected part: the accent outline (white on a fan bar, as in the mockup).
- [ ] **Step 5: Taps.** Convert the pointer to box coordinates. Desk view: `DeskPictures.Hit` → `PartTapped(part)`; else `HitTower` → `TowerTapped`; else `PartTapped(null)`. Inside view: `Hit` → `PartTapped`; else `PartTapped(null)`. No dragging, no keyboard moves. Accessible name: "Desk picture. <selected part's label>, <device name>." or "Desk picture." and the control is not a tab stop (the panel's rows are the keyboard path, as today).
- [ ] **Step 6: Build:** `dotnet build DeadWare.slnx -c Release`, 0 errors, 0 warnings from `src/DeadWare.WinUI`. In the report, in place of tests: for each shape in both SVGs, the line of `page.html` it comes from and the line that draws it; and hand traces of a tap on the keyboard, on the tower, on RAM stick 2, on a not-found card, and with `live` false.
- [ ] **Step 7: Commit** (controller): `feat(lighting): draw the desk picture`.

### Task 4: The page uses the picture

**Files:**
- Modify: `src/DeadWare.WinUI/LightingPage.cs`

**Interfaces:**
- Consumes: Task 3's `DeskPictureView`; `DeskPictures.Named / Resolve`; `state.Settings.Layout.Picture`; the page's `cards` (one `DeviceCard` per device, with `ZoneRow`s), `ShowDesk`, `ShowPanel`, `ShowFrame`, `ShowStatus`.

- [ ] **Step 1: Two modes.** In `ShowDesk`, `DeskPictures.Named(layout.Picture)` decides. `ShowPanel`, `SyncList` and `ShowLayoutPanel` read `desk.Selected` today; add a field `string? selectedDevice` that they read in picture mode, and in picture mode collapse the plain `desk` and keep `desk.Selected` null. The plain `DeskView` is never shown a layout that names a picture. Picture mode: the desk card shows the title and hint for the current view ("Desk" with "tap a device to change its colours · tap the PC to see inside"; "Inside the PC" with "side window, front on the right · tap a part to change its colours" and a **← Back to desk** button), then the `DeskPictureView`, then, when `NotInPicture` is not empty, one line as in the mockup: "Not in the picture:" in normal ink at weight 600, then, muted, the entries joined with " · " and " · they keep your saved colour during effects". The **Edit layout** button and its tools, the Devices card and Not placed are collapsed, and Edit layout is switched off if it was on. Plain mode: exactly today's page. Switching modes clears the selection.
- [ ] **Step 2: Selection.** `PartTapped(part)` selects that part: the panel shows the part's device card (`selectedDevice` = the part's device key), with the row for `ShownZoneKey` highlighted (a subtle accent background on that `ZoneRow`, cleared on the others; a device on its own effect has no zone rows, so nothing is highlighted and its existing own-effect note shows). The panel's heading follows the mockups: for a device with one part, "<part label> · <device name>" with "selected on the desk" or "selected inside the PC" beneath; for a device with several parts, "<device name>" with "<part label in lower case> selected" beneath ("radiator fan 2 selected"). `PartTapped(null)` clears it. `TowerTapped` switches to the inside view and clears the selection; **Back to desk** switches back and clears it. A selected part whose device disappears or is no longer found is deselected.
- [ ] **Step 3: Colours and state.** `SetStateColors` from each state's zone colours; `ShowColors` wherever the page calls `desk.ShowColors` today, with the same rules (frames dropped while hidden, from an old client, or unless the latest state is Running with an effect); `Show` with `lightsOn`, `live`, whether Wave is the running effect, and Wave's angle.
- [ ] **Step 4: The status line.** When the helper is Running with no problem to report, the page's status line reads "<n> devices through OpenRGB · <Wave | Breathing | Spectrum> is running" ("1 device"), "… · Static is on" for Static, and "… · lights off" whenever Lights on is off, whatever the effect. Every existing problem text is unchanged and wins, and the elevation note stays the separate note it is today. This line shows in both modes.
- [ ] **Step 5: Unchanged behaviour to prove by reading:** in the report, hand-trace with file:line: (a) first state on the owner's rig; (b) tap the keyboard, change its colour in the flyout; (c) tap the tower, tap radiator fan 2, **Back to desk**; (d) the helper relaunches with a flyout open; (e) the card disappears from the device list; (f) a state arrives whose layout has no `Picture` (plain mode appears, Edit layout available); (g) page hidden and shown during Wave; (h) lights off.
- [ ] **Step 6: Build and test:** the solution build with 0 warnings from `src/DeadWare.WinUI`; `tests/DeadWare.Tests` once.
- [ ] **Step 7: Commit** (controller): `feat(lighting): the Lighting page shows the owner's rig as a picture`.

### Task 5: Guide, release note and spec pointer

**Files:**
- Modify: `docs/user-guide.md` (Lighting section), `docs/releases/v0.19.0.md`, `docs/superpowers/specs/2026-11-19-lighting-step2-desk-design.md` (one line under the title pointing to the picture spec for the desk card)

- [ ] **Step 1:** In the guide, say that the desk card depends on the PC: on a PC DeadWare has a built-in picture for, it shows that picture (what is drawn, tapping a part, tapping the PC for the inside view and **← Back to desk**, parts marked "not found", "Not in the picture", that inside the PC a wave follows the drawing from the rear of the case to the front, that nothing can be rearranged); on any other PC it shows the plain desk with **Edit layout**, as already described. Keep every sentence true to the code at the head of the branch; quote control text exactly.
- [ ] **Step 2:** The same, briefly, in the release note. Add the spec pointer line.
- [ ] **Step 3:** `powershell -File scripts/publish.ps1 -Version 0.19.0` on a clean tree (controller): exit 0, the package within the updater's limits.
- [ ] **Step 4: Commit** (controller): `docs(lighting): the desk picture in the guide and release note`.

### Task 6: The owner looks at it

Not run by an agent. With the owner's approval for each elevated launch: quit the running test build, start the new one, and compare the page with `lighting-page-desk.png` and `lighting-page-inside.png` side by side (the controller looks first through computer use, then the owner). Differences are fixed or agreed. Then the step 2 functional checks that were postponed: effects on every device without blinking, the Node Pro keeping its own effect, radiator fan order under a left-to-right Wave (swap `fan1` and `fan3` if reversed), lights off and on, closing the window, Quit. Then `s2-restore.ps1`.

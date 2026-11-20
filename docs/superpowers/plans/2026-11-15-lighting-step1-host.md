# Lighting step 1: device host and static colours. Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DeadWare gains a background device host, `DeadWare.Devices.exe`, that runs OpenRGB, applies static colours, brightness and lights on/off to every OpenRGB device, and survives a crash of the window. A Lighting page and a Settings category drive it.

**Architecture:** Pure lighting logic and the pipe contract live in `DeadWare.Core`. A new headless WinExe, `DeadWare.Devices`, owns OpenRGB (child process plus SDK via OpenRGB.NET), `lighting.json` and a named-pipe server. `DeadWare.Services` gains `DeviceHostClient`, which `MonitorSession` uses to launch, supervise and stop the host. The WinUI app adds the page, the setting and the sidebar item. The updater learns to wait for the host.

**Tech Stack:** .NET 10, WinUI 3 (Windows App SDK 2.6.5), xUnit 2.9.3, OpenRGB.NET 3.1.1 (MIT), System.IO.Pipes, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-11-15-lighting-design.md`, build-order step 1. Read it before starting; this plan argues from it. Steps 2–5 get their own plans.

## Global Constraints

- **Start from `main` after 0.18.1 has merged** (PR #132 and its polish follow-up). This step touches `MainWindow.xaml.cs`, `SettingsPage.cs` and `Monitoring.cs`, which 0.18.1 also edits. Work in the `claude/lighting-merge` worktree and rebase it onto that main first.
- **Executable name:** `DeadWare.Devices.exe`. Nothing new may be called "helper"; the updater already copies itself as `helper.exe`.
- **OpenRGB launch:** `OpenRGB.exe --server --server-host 127.0.0.1 --server-port 6744 --noautoconnect --config %ProgramData%\DeadWare\openrgb`. `--server-host 127.0.0.1` keeps OpenRGB's unauthenticated SDK server on this PC; without it OpenRGB 1.0rc3 listens on every network interface (Ruling AN, final review 2026-11-18). Stop it by killing the process tree, as DeadLight does with `taskkill /T /F`.
- **Restart pacing**, for both the host and OpenRGB: delays of 4 s, 8 s, 16 s and 32 s, capped at 60 s. Give up after 5 unexpected exits within 10 minutes. Never restart sooner than 4 s.
- **Sign-in:** with `--startup`, the host waits 10 s before its conflict scan and OpenRGB start.
- **Quit:** the window sends Stop, waits at most 5 s for the host to exit, then kills it.
- **Single instance:** mutex `Local\DeadWare.Devices.<sid>.<session>`.
- **Pipe:** `\\.\pipe\DeadWare.Devices.<sid>`. Access is limited to the current user's SID and BUILTIN\Administrators. Messages are length-prefixed JSON of at most 1 MiB, each carrying protocol version 1.
- **`%LOCALAPPDATA%\DeadWare\lighting.json`:** written only by the host. Writes go to a temp file that then replaces the original. On every read and write, invalid content is quarantined as `lighting.corrupt-<UTC yyyyMMddTHHmmssZ>.json`. Defaults are lights on, brightness 100 and colour `#4682B4`, DeadLight's steel blue.
- **`settings.json` (window-owned)** gains only `LightingEnabled` (default `false`) and `OpenRgbPath` (default `null`).
- **Log:** `%LOCALAPPDATA%\DeadWare\logs\devices.log`, rotating at 2 MiB through 3 files; an elevated host logs to protected `%ProgramData%\DeadWare\logs` instead (Ruling AT, final review). Write changes only, never a line per tick.
- **Paint pacing**, from DeadLight:
  - refresh time-to-live 400 ms
  - per-device cap of 33 ms, or 200 ms for OpenRGB controller type 1 (DRAM)
  - paint tick 500 ms (step 1 has no effects)
  - rescan every 5 s
- **Lighting Node Pro:** its default hardware mode is `Rainbow Wave`, by device name `Corsair Lighting Node Pro`. Never switch a hardware-defaulted device to Direct.
- **Conflicts that block lighting:**
  - `electron.exe` whose path contains `\DeadLight\`
  - any `OpenRGB.exe` the host didn't start
  - `iCUE.exe` or `iCUE Launcher.exe`
  - `CorsairDeviceControlService.exe` or `CorsairDeviceListerService.exe`

  `Corsair.Service.exe` is reported but blocks only the hub driver (step 5). Match by process path where one is available.
- **Not in step 1:** no hub HID driver (OpenRGB's iCUE Link detector stays enabled), no desk effects, no Aerox driver, no tray item. OpenRGB's "SteelSeries Aerox 9 Wireless" detector is switched off in its `OpenRGB.json` before every start, as DeadLight does (Ruling AR, final review).
- **Protected folder:** create `%ProgramData%\DeadWare\openrgb` with `ProtectedRoot.Security()` and check it with `InstallLocation.Check(dir, 1)`, as UpdateCore checks the sibling updates folder (Ruling AL, final review). Check the OpenRGB folder with `InstallLocation.Check` and warn when it isn't protected; an elevated host refuses to run OpenRGB from it (Ruling AM, final review).
- **Code style:** follow the repository's dense one-line style in Core and Services, and the WinUI pages' code-only construction (no XAML for new pages).
- **Commits** end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Push the branch after every task. Never tag.
- **Hardware:** no on-screen automation while the user is gaming. Never launch DeadLight or OpenRGB from a Claude shell, because those shells run inside the Claude app's MSIX container and see shadow copies of AppData.

## Review Focus

1. **DeadLight or another OpenRGB still running when lighting is enabled.** The host must report the conflict and never start its own OpenRGB; two engines fighting is the blinking the user wants gone. The test is in Task 9.
2. **OpenRGB path wrong, missing, or the port never opening.** The page needs a plain reason, and the host must not crash-loop faster than 4 s or past 5 failures in 10 minutes. Tests are in Tasks 7 and 10.
3. **A device re-enumerates with a new controller index, or a dropped or regained serial.** Its colours follow the device key, the diff caches clear, and nothing is written to a stale index. Tests are in Tasks 3 and 8.
4. **`lighting.json` corrupt, or unreadable at the moment of loading** (a sharing violation, say). A corrupt file is quarantined; an unreadable one is never overwritten. Tests are in Task 6.
5. **Quit or update while the host is busy or hung.** The window kills it after 5 s, and the updater refuses to replace files while `DeadWare.Devices.exe` still runs from the install folder. Tests are in Tasks 10 and 11.

---

### Task 1: OpenRGB.NET compatibility check (read-only)

**Files:**
- Create (scratch, not committed): `%TEMP%\dw-openrgb-check\check.cs`
- Create: `tests/DeadWare.Devices.Tests/Fixtures/controllers.json`, a sanitised device snapshot (committed in Task 8)

**Interfaces:**
- Produces: a decision recorded in the Task 8 commit message, either "OpenRGB.NET 3.1.1 reads all controllers from OpenRGB 1.0rc3 at protocol V4" or "own client required", plus the sanitised fixture.

- [ ] **Step 1: Write a file-based C# check** that runs `#:package OpenRGB.NET@3.1.1` and `new OpenRgbClient("127.0.0.1", 6742, "DeadWare-check")`. It calls only `Connect()`, `GetControllerCount()` and `GetAllControllerData()`, then prints `CommonProtocolVersion.Number` and each device's name, type, zone names and LED counts, mode names, active mode index, serial and location. **Never call `UpdateLeds`, `UpdateMode`, `SetCustomMode` or `SaveMode`.** This reads from DeadLight's running OpenRGB on port 6742 and changes nothing.
- [ ] **Step 2: Run it:** `dotnet run %TEMP%\dw-openrgb-check\check.cs`. Expected: 7–8 devices, including `Corsair iCUE Link System Hub`, `Corsair Lighting Node Pro`, `SteelSeries Apex Pro`, the RAM modules and the GPU, and a common protocol of 4. If it fails, compare with DeadLight's fixture `I:\DeadLight\tests\fixtures\controllers.json`. If the failure is a parsing fault, stop and report it, because Task 8 then needs its own client.
- [ ] **Step 3: Write `controllers.json`** from the output with the same fields as DeadLight's fixture: name, type, serial, location, zones (name and LED count), modes and active mode. **Replace serials with `SERIAL-n` and locations with `HID: \\?\HID#FAKE-n` or `I2C: FAKE-n`**, because the real values identify this PC. Nothing to commit yet.

### Task 2: Lighting model and settings codec (Core)

**Files:**
- Create: `src/DeadWare.Core/LightingModels.cs`, `src/DeadWare.Core/LightingSettingsCodec.cs`
- Test: `tests/DeadWare.Tests/LightingSettingsCodecTests.cs`

**Interfaces:**
- Produces, in namespace `DeadWare.Core`:
  - `readonly record struct Rgb(byte R, byte G, byte B)`, with `static Rgb Black`, `static bool TryParse(string? hex, out Rgb color)` (accepts exactly `#RRGGBB`, either case), `string ToHex()` (uppercase `#RRGGBB`) and `Rgb Scale(int percent)` (clamped to 0–100, rounded to nearest).
  - `sealed class ZoneColorEntry { string Color; string ZoneName; }`, as auto-properties.
  - `sealed class LightingSettings { int Version = 1; bool LightsOn = true; int Brightness = 100; string DefaultColor = "#4682B4"; Dictionary<string, ZoneColorEntry> ZoneColors = new(); Dictionary<string, string> HardwareModes = new(); }`, as auto-properties. `HardwareModes` stays empty in step 1; the step 3 menu writes it.
  - `sealed record LightingRead(LightingSettings Settings, string? Error, bool Quarantine)`
  - `static class LightingSettingsCodec`, with `const int MaxZoneEntries = 4096, MaxKeyLength = 512`, `LightingRead Read(string json)`, `string Write(LightingSettings settings)` (throws `InvalidDataException` when invalid) and `string? Validate(LightingSettings settings)`.

- [ ] **Step 1: Write the failing tests:**
  - `Read_of_empty_object_gives_defaults`: `{}` reads to LightsOn true, Brightness 100, DefaultColor `#4682B4`, with no error and no quarantine.
  - `Write_then_Read_round_trips`: a settings object with one zone entry comes back equal.
  - `Unparseable_json_asks_for_quarantine`: `{` gives Quarantine true and default settings.
  - `Out_of_range_brightness_asks_for_quarantine`: Brightness 101.
  - `Bad_colour_asks_for_quarantine`: DefaultColor `#12345G`.
  - `Too_many_zone_entries_asks_for_quarantine`: 4097 entries.
  - `Overlong_key_asks_for_quarantine`: a 513-character key.
  - `Write_refuses_invalid_settings`: Brightness -1 throws `InvalidDataException`.
  - `Rgb_scale_rounds`: `new Rgb(70,130,180).Scale(50)` is `(35,65,90)`, `Scale(0)` is black and `Scale(150)` is unchanged.
  - `Rgb_parse_and_format`: `TryParse("#4682b4")` succeeds and `ToHex()` is `#4682B4`.
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~LightingSettingsCodecTests`. Expected: compile errors, because the types are missing.
- [ ] **Step 3: Implement the types and the codec.** The rule is DeadLight's: any validation failure in parseable data is an external edit, so it gets quarantined, never repaired. Missing properties take their defaults.
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit:** `feat(lighting): lighting settings model and codec`

### Task 3: Device keys and static paint (Core)

**Files:**
- Create: `src/DeadWare.Core/LightingKeys.cs`, `src/DeadWare.Core/LightingPaint.cs`
- Test: `tests/DeadWare.Tests/LightingKeysTests.cs`, `tests/DeadWare.Tests/LightingPaintTests.cs`

**Interfaces:**
- Consumes: Task 2 types.
- Produces:
  - `static class LightingKeys`, with:
    - `string Device(string serial, string location)`, giving `"{serial}|{location}"`
    - `string Zone(string deviceKey, int zoneIndex)`, giving `"{deviceKey}#{zoneIndex}"`
    - `string LocationOf(string deviceKey)`, which splits at the **first** `|`
    - `bool TrySplitZone(string zoneKey, out string deviceKey, out int zoneIndex)`, using an anchored `#\d+$` suffix
    - `bool Remap(LightingSettings settings, IReadOnlyCollection<string> liveDeviceKeys)`, which re-keys `ZoneColors` and `HardwareModes` and returns true if anything changed
  - `sealed record LightingZone(int Index, string Name, int LedCount)`
  - `sealed record LightingDevice(string Key, string Name, int Type, IReadOnlyList<LightingZone> Zones, IReadOnlyList<string> Modes, int ActiveMode)`
  - `enum PaintKind { Direct, Hardware, Unsupported }`
  - `sealed record DevicePaint(PaintKind Kind, string? Mode, Rgb[] Colors, bool StaysLitWhenOff)`. `Colors` holds per-LED colours for Direct, or mode colours for a hardware mode.
  - `static class LightingPaint`, with:
    - `const string DirectMode = "Direct", StaticMode = "Static"`
    - `IReadOnlyDictionary<string, string> HardwareModeDefaults`, containing `{"Corsair Lighting Node Pro": "Rainbow Wave"}`
    - `string? HardwareModeFor(LightingSettings settings, LightingDevice device)`
    - `DevicePaint Paint(LightingSettings settings, LightingDevice device)`
    - `Rgb ZoneColor(LightingSettings settings, LightingDevice device, LightingZone zone)`

- [ ] **Step 1: Write the failing tests.** The remap rules port DeadLight's `remapDeviceKeys` refusals from `I:\DeadLight\shared\profile.ts:113-175`; port the matching vitest cases too.
  - `Remap_moves_entries_when_serial_drops_at_same_location`: the stored key `S1|HID:A` with live key `|HID:A` becomes `|HID:A#0`.
  - `Remap_refuses_absent_location`
  - `Remap_refuses_location_claimed_by_two_live_devices`
  - `Remap_refuses_when_target_already_stored`
  - `Remap_refuses_two_stale_keys_racing_for_one_target`
  - `LocationOf_splits_on_first_pipe`: the serial `a\r\nb|c` with location `HID:x|y` gives `HID:x|y`.
  - `TrySplitZone_keeps_hid_hash_segments`: `S|HID#VID_1B1C&PID_0C3F#b&16#3` gives index 3 and keeps the location intact.
  - `Paint_uses_zone_entry_when_name_matches`
  - `Paint_falls_back_to_default_when_zone_name_changed`
  - `Paint_scales_by_brightness`: 50% gives the scaled colour on every LED.
  - `Paint_lights_off_is_black_on_every_led`: Direct, with every colour black.
  - `Node_Pro_defaults_to_rainbow_wave_and_never_direct`: Kind Hardware with Mode `Rainbow Wave`.
  - `Hardware_device_lights_off_uses_static_black_when_offered`: Mode `Static` with Colors `[Black]`.
  - `Hardware_device_without_static_stays_lit_when_off`: StaysLitWhenOff is true.
  - `Device_without_direct_or_default_is_unsupported`
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~LightingKeysTests|FullyQualifiedName~LightingPaintTests"`.
- [ ] **Step 3: Implement.** `HardwareModeFor` takes an explicit `HardwareModes[key]` when that mode name is in `Modes`, otherwise the default for the device name when it is in `Modes`, otherwise null. `Paint` returns Unsupported when the device has neither a hardware mode nor `Direct`.
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit:** `feat(lighting): device keys, remapping and static paint`

### Task 4: Restart pacing and conflict rules (Core)

**Files:**
- Create: `src/DeadWare.Core/RestartBackoff.cs`, `src/DeadWare.Core/LightingConflicts.cs`
- Test: `tests/DeadWare.Tests/RestartBackoffTests.cs`, `tests/DeadWare.Tests/LightingConflictsTests.cs`

**Interfaces:**
- Produces:
  - `sealed class RestartBackoff`, with:
    - `static readonly TimeSpan Base = 4 s, Cap = 60 s, Window = 10 min`
    - `const int MaxFailures = 5`
    - `TimeSpan? RecordFailure(DateTimeOffset now)`, which returns null meaning "give up"
    - `void Reset()`
  - `sealed record RunningProcess(int Id, string Name, string? Path)`
  - `enum ConflictKind { DeadLight, ForeignOpenRgb, ICue, CorsairService, LegacyCorsairService }`
  - `sealed record LightingConflict(ConflictKind Kind, string Description, bool BlocksLighting)`
  - `static class LightingConflicts`, with `IReadOnlyList<LightingConflict> Find(IEnumerable<RunningProcess> processes, int? ownOpenRgbPid)`

- [ ] **Step 1: Write the failing tests:**
  - `Delays_double_from_four_seconds`: four failures one second apart give 4, 8, 16 and 32 s.
  - `Fifth_failure_within_ten_minutes_gives_up`: it returns null.
  - `Failures_older_than_ten_minutes_are_forgotten`: after an 11-minute gap the next delay is 4 s.
  - `Delay_never_exceeds_cap`
  - `Delay_never_below_four_seconds`
  - `DeadLight_electron_blocks_by_path`: `electron` at `I:\DeadLight\node_modules\electron\dist\electron.exe` gives a DeadLight conflict that blocks lighting.
  - `Other_electron_apps_do_not_block`
  - `Foreign_openrgb_blocks_but_own_does_not`
  - `iCUE_and_launcher_block`
  - `Device_services_block`
  - `Legacy_corsair_service_is_reported_but_does_not_block_lighting`: `Corsair.Service` at `…\Corsair iCUE5 Software\clink\Corsair.Service.exe` gives BlocksLighting false.
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~RestartBackoffTests|FullyQualifiedName~LightingConflictsTests"`.
- [ ] **Step 3: Implement.** Process names compare case-insensitively without `.exe`. Descriptions are user-facing, for example "DeadLight is running. Quit it from its tray icon before turning on DeadWare lighting."
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit:** `feat(lighting): restart pacing and lighting conflict rules`

### Task 5: Pipe contract (Core)

**Files:**
- Create: `src/DeadWare.Core/DeviceHostProtocol.cs`
- Test: `tests/DeadWare.Tests/DeviceHostProtocolTests.cs`

**Interfaces:**
- Consumes: Task 2 to Task 4 types.
- Produces:
  - `static class DeviceHostProtocol`, with:
    - `const int Version = 1, MaxMessageBytes = 1_048_576`
    - `string PipeName(string sid)`, giving `"DeadWare.Devices." + sid`
    - `string InstanceName(string sid, int session)`
    - `Task WriteAsync(Stream stream, HostMessage message, CancellationToken token)`, writing a 4-byte little-endian length and then UTF-8 JSON
    - `Task<HostMessage?> ReadAsync(Stream stream, CancellationToken token)`, which returns null on a clean end of stream and throws `InvalidDataException` for an oversize or malformed message
  - `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")] abstract record HostMessage`, with `int Version` (init, default `DeviceHostProtocol.Version`) and `long Id` (init). Derived records:
    - `GetStateRequest`
    - `SetLightsOnRequest(bool On)`
    - `SetBrightnessRequest(int Percent)`
    - `SetAllColorsRequest(string Color)`
    - `SetZoneColorRequest(string ZoneKey, string? Color)`, where a null colour clears the zone's own colour
    - `ReplaceColorsRequest(string DefaultColor, Dictionary<string, ZoneColorEntry> ZoneColors)`, used for Undo
    - `RescanRequest`
    - `StopRequest`
    - `Reply(bool Ok, string? Error)`
    - `StateEvent(HostState State)`
  - `enum HostStatus { Starting, Waiting, Running, Blocked, OpenRgbMissing, OpenRgbFailed, Stopping }`
  - `sealed record HostState(HostStatus Status, string Message, bool Elevated, IReadOnlyList<LightingConflict> Conflicts, IReadOnlyList<DeviceView> Devices, LightingSettings Settings)`
  - `sealed record DeviceView(string Key, string Name, int Type, PaintKind Kind, string? Mode, bool StaysLitWhenOff, IReadOnlyList<ZoneView> Zones)`
  - `sealed record ZoneView(string Key, int Index, string Name, int LedCount, string Color, bool OwnColor)`

- [ ] **Step 1: Write the failing tests:**
  - `Every_message_type_round_trips` (a theory over every derived record)
  - `Oversize_length_is_rejected_before_allocation`: the length prefix is MaxMessageBytes + 1.
  - `Truncated_body_throws`
  - `Clean_end_of_stream_returns_null`
  - `Version_survives_round_trip`
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~DeviceHostProtocolTests`.
- [ ] **Step 3: Implement** with a `JsonSerializerContext` source generator covering `HostMessage`.
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit:** `feat(lighting): device host pipe contract`

### Task 6: Device host project, lighting store and log

**Files:**
- Create: `src/DeadWare.Devices/DeadWare.Devices.csproj`, `src/DeadWare.Devices/AssemblyInfo.cs`, `src/DeadWare.Devices/LightingStore.cs`, `src/DeadWare.Devices/HostLog.cs`
- Create: `tests/DeadWare.Devices.Tests/DeadWare.Devices.Tests.csproj`, `tests/DeadWare.Devices.Tests/LightingStoreTests.cs`, `tests/DeadWare.Devices.Tests/HostLogTests.cs`
- Modify: `DeadWare.slnx`, adding both projects

**Interfaces:**
- Consumes: Task 2 codec.
- Produces:
  - **The project file:** `OutputType` WinExe, `TargetFramework` net10.0-windows, `Platforms` x64, `RuntimeIdentifier` win-x64, `AssemblyName` DeadWare.Devices, `StartupHookSupport` false, `ApplicationIcon` `../../assets/deadware.ico`. References: Core, UpdateCore, and `OpenRGB.NET` 3.1.1 (or nothing, if Task 1 said "own client"). `InternalsVisibleTo("DeadWare.Devices.Tests")`.
  - `internal sealed class LightingStore(string path)`, with `LightingRead Load()` and `void Save(LightingSettings settings)`.
  - `internal sealed class HostLog : IDisposable`, with `const long MaxBytes = 2_097_152`, `const int Files = 3`, a constructor `HostLog(string directory, Func<DateTimeOffset>? clock = null)` and `void Write(string line)`. `Write` never throws, prefixes the time in ISO form, and rotates `devices.log` to `devices.1.log` to `devices.2.log`.

- [ ] **Step 1: Write the failing tests:**
  - `Missing_file_loads_defaults_without_error`
  - `Corrupt_file_is_quarantined_and_defaults_load`: the file becomes `lighting.corrupt-*.json`, its original content survives, and `Load` returns defaults.
  - `Unreadable_file_throws_and_is_not_touched`: hold the file open with `FileShare.None`; `Load` throws `IOException` and the file is unchanged.
  - `Save_replaces_atomically`: no temp file is left behind, and the content reads back.
  - `Save_refuses_invalid_settings`
  - `Log_rotates_at_two_mebibytes_and_keeps_three_files`
  - `Log_write_never_throws_when_directory_is_read_only`
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Devices.Tests -c Release`.
- [ ] **Step 3: Implement.** `Save` writes `lighting.json.tmp` and then calls `File.Move(tmp, path, overwrite: true)`.
- [ ] **Step 4: Run the tests until they pass.** Also run `dotnet build DeadWare.slnx -c Release` and expect 0 warnings from the new projects.
- [ ] **Step 5: Commit:** `feat(lighting): device host project, lighting store and capped log`

### Task 7: OpenRGB engine and protected config folder

**Files:**
- Create: `src/DeadWare.Devices/OpenRgbEngine.cs`, `src/DeadWare.Devices/ProcessRunner.cs`, `src/DeadWare.Devices/ProtectedFolders.cs`
- Test: `tests/DeadWare.Devices.Tests/OpenRgbEngineTests.cs`, `tests/DeadWare.Devices.Tests/ProtectedFoldersTests.cs`

**Interfaces:**
- Consumes: Task 4 `RestartBackoff`; Task 6 `HostLog`; UpdateCore `ProtectedRoot.Security()` and `InstallLocation.Check(string, int)`.
- Produces:
  - `internal interface IProcessRunner { IRunningProcess Start(ProcessStartInfo info); }`
  - `internal interface IRunningProcess : IDisposable { int Id { get; } bool HasExited { get; } Task WaitForExitAsync(CancellationToken token); void KillTree(); }`
  - `internal sealed class SystemProcessRunner : IProcessRunner`
  - `internal enum EngineStatus { Starting, Running, Restarting, Failed, Stopped }`
  - `internal sealed class OpenRgbEngine : IAsyncDisposable`, with:
    - `const int Port = 6744`
    - `static IReadOnlyList<string> Arguments(string configDir)`
    - a constructor `OpenRgbEngine(string exePath, string configDir, IProcessRunner runner, Func<int, CancellationToken, Task<bool>> portOpen, TimeProvider time, HostLog log)`
    - `int? ProcessId { get; }`, `EngineStatus Status { get; }`, `string? FailureReason { get; }`
    - `event Action? Changed`
    - `Task StartAsync(CancellationToken token)`
    - `Task StopAsync()`
  - `internal static class ProtectedFolders`, with `string OpenRgbConfigDirectory()` (creates `%ProgramData%\DeadWare\openrgb` with `ProtectedRoot.Security()` if missing, then verifies it) and `LocationProtection CheckOpenRgbFolder(string exePath)`.

- [ ] **Step 1: Write the failing tests**, with a fake runner, a fake port probe and `FakeTimeProvider` (add `Microsoft.Extensions.TimeProvider.Testing` to the test project):
  - `Arguments_match_spec`: the exact list from Global Constraints.
  - `Missing_exe_fails_without_starting`: Status Failed, with the reason "OpenRGB.exe was not found at …".
  - `Port_never_opening_within_thirty_seconds_kills_and_backs_off`
  - `Unexpected_exit_restarts_after_four_seconds_then_eight`: there is no start before 4 s of fake time.
  - `Five_exits_in_ten_minutes_fail_with_reason`
  - `Stop_kills_tree_and_does_not_restart`
  - `Writable_temp_folder_is_not_protected` (in `ProtectedFoldersTests`): `CheckOpenRgbFolder` on a temp folder the user can write returns Protected false.
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~OpenRgbEngineTests|FullyQualifiedName~ProtectedFoldersTests"`.
- [ ] **Step 3: Implement.** `SystemProcessRunner` starts with `UseShellExecute = false`, `CreateNoWindow = true` and `WorkingDirectory` set to the exe's folder, and `KillTree` calls `Process.Kill(entireProcessTree: true)`. The port probe is a `TcpClient` connect to 127.0.0.1:6744, retried every 500 ms. The engine's status changes are written to `HostLog`.
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit:** `feat(lighting): OpenRGB engine with paced restarts and protected config folder`

### Task 8: OpenRGB session and device service

**Files:**
- Create: `src/DeadWare.Devices/OpenRgbSession.cs`, `src/DeadWare.Devices/LightingDeviceService.cs`
- Test: `tests/DeadWare.Devices.Tests/LightingDeviceServiceTests.cs`, `tests/DeadWare.Devices.Tests/Fixtures/controllers.json` (from Task 1), `tests/DeadWare.Devices.Tests/FakeOpenRgbSession.cs`

**Interfaces:**
- Consumes: Task 3 paint; Task 6 log.
- Produces:
  - `internal sealed record OpenRgbController(int Index, string Name, int Type, string Serial, string Location, IReadOnlyList<LightingZone> Zones, IReadOnlyList<string> Modes, int ActiveMode)`
  - `internal interface IOpenRgbSession : IDisposable`, with:
    - `bool Connected { get; }`
    - `void Connect()`
    - `IReadOnlyList<OpenRgbController> ReadControllers()`
    - `void UpdateLeds(int index, ReadOnlySpan<Rgb> colors)`
    - `void UpdateMode(int index, int modeIndex, IReadOnlyList<Rgb>? colors)`
    - `event Action? DeviceListUpdated`
  - `internal sealed class OpenRgbSession : IOpenRgbSession`, the OpenRGB.NET adapter, port 6744, client name `DeadWare`.
  - `internal sealed class LightingDeviceService`, with:
    - `static readonly TimeSpan HidCap = 33 ms, DramCap = 200 ms, RefreshTtl = 400 ms`
    - a constructor `LightingDeviceService(IOpenRgbSession session, TimeProvider time, HostLog log)`
    - `IReadOnlyList<LightingDevice> Devices { get; }`
    - `bool Rescan()`
    - `void Reset()`
    - `void Paint(LightingSettings settings)`
    - `IReadOnlyList<DeviceView> Views(LightingSettings settings)`

- [ ] **Step 1: Write the failing tests** against a fake session loaded from the sanitised fixture:
  - `Rescan_builds_keys_from_serial_and_location`
  - `Direct_device_switches_mode_once_then_only_updates_leds`: `UpdateMode` is called with the Direct index once across three paints.
  - `Node_Pro_gets_rainbow_wave_by_name_and_no_led_writes`
  - `Unchanged_frame_is_not_resent_before_400ms_and_is_after`
  - `Dram_device_is_capped_at_200ms`
  - `Hid_device_is_capped_at_33ms`
  - `Lights_off_refreshes_black`: after 400 ms an identical black frame is re-sent.
  - `Index_change_on_rescan_writes_to_new_index_by_key`
  - `Reset_clears_cache_so_next_paint_resends_and_reswitches_mode`
  - `Views_mark_own_colour_and_default_colour_zones`
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~LightingDeviceServiceTests`.
- [ ] **Step 3: Implement.** Port `pushFrame` from `I:\DeadLight\electron\device-service.ts:100-125`, including its caps, time-to-live and mode-set-once logic. Treat a device of type 1 as DRAM.
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit**, with the Task 1 decision in the body: `feat(lighting): OpenRGB session and paced device painting`

### Task 9: Pipe server, host orchestration and entry point

**Files:**
- Create: `src/DeadWare.Devices/HostPipeServer.cs`, `src/DeadWare.Devices/DeviceHost.cs`, `src/DeadWare.Devices/ProcessConflictScanner.cs`, `src/DeadWare.Devices/Program.cs`
- Test: `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `tests/DeadWare.Devices.Tests/HostPipeServerTests.cs`

**Interfaces:**
- Consumes: Tasks 4 to 8.
- Produces:
  - `internal sealed record HostOptions(string OpenRgbPath, string ConfigDir, string LightingPath, string LogDir, bool Startup, string Sid, int Session)`
  - `internal interface IConflictScanner { IReadOnlyList<LightingConflict> Scan(int? ownOpenRgbPid); }`
  - `internal sealed class DeviceHost : IAsyncDisposable`, with:
    - a constructor `DeviceHost(HostOptions options, LightingStore store, OpenRgbEngine engine, Func<IOpenRgbSession> sessionFactory, IConflictScanner conflicts, TimeProvider time, HostLog log)`
    - `HostState State { get; }`
    - `event Action<HostState>? StateChanged`
    - `Task RunAsync(CancellationToken token)`
    - `Task<HostMessage> HandleAsync(HostMessage request)`
  - `internal sealed class HostPipeServer : IAsyncDisposable`, with:
    - a constructor `HostPipeServer(string pipeName, Func<HostMessage, Task<HostMessage>> handle, HostLog log)`
    - `Task RunAsync(CancellationToken token)`
    - `Task SendAsync(HostMessage message)`
  - `Program.Main`. Its arguments are `--openrgb <path>` (required) and `--startup` (optional). Exit codes: 0 after Stop, 2 for bad arguments, 3 if another host already holds the mutex.

- [ ] **Step 1: Write the failing tests:**
  - `Blocking_conflict_reports_Blocked_and_never_starts_engine`: the scanner returns DeadLight; the status becomes Blocked; the engine is never started.
  - `Startup_flag_waits_ten_seconds_before_scanning`: fake time is used.
  - `Missing_openrgb_reports_OpenRgbMissing`
  - `SetAllColors_clears_zone_entries_saves_and_repaints`
  - `SetZoneColor_null_clears_own_colour`
  - `ReplaceColors_restores_exact_snapshot`
  - `SetBrightness_out_of_range_replies_error_and_changes_nothing`
  - `Unreadable_lighting_json_runs_with_defaults_but_never_saves`: the status message says the settings file couldn't be read.
  - `Stop_kills_engine_and_ends_RunAsync`
  - `Pipe_rejects_wrong_version`: a request with Version 2 gets `Reply(false, "version")`.
  - `Pipe_round_trips_request_and_state_event`: an in-process client.
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~DeviceHostTests|FullyQualifiedName~HostPipeServerTests"`.
- [ ] **Step 3: Implement.**
  - The pipe uses `NamedPipeServerStreamAcl.Create` with `PipeSecurity` rules for `WindowsIdentity.GetCurrent().User` and `BuiltinAdministratorsSid` only, and `maxNumberOfServerInstances` 1.
  - The run loop ticks every 500 ms and paints, rescans every 5 s, and repaints at once after a settings change, broadcasting `StateEvent`.
  - `Elevated` comes from `Environment.IsPrivilegedProcess`.
  - `Program` builds paths from `Environment.GetFolderPath(LocalApplicationData)` and the folder from `ProtectedFolders.OpenRgbConfigDirectory()`, and takes the mutex from `DeviceHostProtocol.InstanceName`.
  - Log start, stop, conflicts, engine status and device-list changes; never log ticks.
- [ ] **Step 4: Run the tests until they pass.** Then run `dotnet build DeadWare.slnx -c Release`.
- [ ] **Step 5: Commit:** `feat(lighting): device host orchestration, pipe server and entry point`

### Task 10: Host client and session integration (Services)

**Files:**
- Create: `src/DeadWare.Services/DeviceHostClient.cs`
- Modify: `src/DeadWare.Core/Monitoring.cs` (UserSettings and SettingsCodec), `src/DeadWare.Services/MonitorSession.cs` (create, start, stop)
- Test: `tests/DeadWare.App.Tests/DeviceHostClientTests.cs`, and `tests/DeadWare.Tests/SettingsCodecLightingTests.cs` (put it where the existing SettingsCodec tests live)

**Interfaces:**
- Consumes: Task 4 `RestartBackoff`; Task 5 protocol.
- Produces:
  - `UserSettings.LightingEnabled` (bool, default false) and `UserSettings.OpenRgbPath` (string?). `SettingsCodec.Read` sets the path to null if it is longer than 260 characters, contains control characters, or doesn't end in `OpenRGB.exe`.
  - `internal interface IDeviceHostProcess : IDisposable { int Id { get; } bool HasExited { get; } Task WaitForExitAsync(CancellationToken token); void Kill(); }`
  - `internal interface IDeviceHostLauncher { IDeviceHostProcess Start(string openRgbPath, bool startup); IDeviceHostProcess? FindRunning(); }`
  - `internal sealed class DeviceHostClient : IAsyncDisposable`, with:
    - a constructor `DeviceHostClient(string openRgbPath, IDeviceHostLauncher launcher, Func<CancellationToken, Task<Stream>> connect, TimeProvider time)`
    - `HostState? State { get; }`, `string? Problem { get; }`
    - `event Action? Changed`
    - `Task StartAsync(bool startup, CancellationToken token)`
    - `Task<Reply> SendAsync(HostMessage request, CancellationToken token)`
    - `Task StopAsync()`
  - `MonitorSession.Lighting` (`DeviceHostClient?`) and `MonitorSession.SetLightingAsync(bool enabled, string? openRgbPath)`, which saves the settings and starts, restarts or stops the client.

- [ ] **Step 1: Write the failing tests:**
  - `Starts_host_when_none_running_and_connects`
  - `Connects_to_running_host_without_launching`
  - `Unexpected_host_exit_relaunches_after_four_seconds`
  - `Fifth_exit_in_ten_minutes_sets_Problem_and_stops_relaunching`
  - `Stop_sends_StopRequest_and_waits_up_to_five_seconds_then_kills`
  - `Access_denied_on_connect_reports_elevation_problem_and_does_not_launch`: Problem is "The lighting helper is running as administrator. Restart DeadWare as administrator to control it."
  - `SettingsCodec_drops_unsafe_openrgb_path`
  - `SettingsCodec_lighting_defaults_off`
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.App.Tests -c Release --filter FullyQualifiedName~DeviceHostClientTests`.
- [ ] **Step 3: Implement.**
  - The real launcher starts `Path.Combine(AppContext.BaseDirectory, "DeadWare.Devices.exe")` with `--openrgb <path>`, plus `--startup` on a sign-in launch.
  - Connecting uses `NamedPipeClientStream(".", DeviceHostProtocol.PipeName(sid), InOut, Asynchronous)` with a 2 s timeout.
  - `MonitorSession.Start` creates and starts the client when `LightingEnabled` is on and a path is set.
  - `ShutdownCoreAsync` awaits `Lighting.StopAsync()` alongside the collector stop.
- [ ] **Step 4: Run the tests until they pass.**
- [ ] **Step 5: Commit:** `feat(lighting): device host client and session integration`

### Task 11: Updater waits for the device host

**Files:**
- Create: `src/DeadWare.UpdateCore/InstallProcesses.cs`
- Modify: `src/DeadWare.Updater/Program.cs`, after the `while(!parent.HasExited)` loop
- Test: `tests/DeadWare.Tests/InstallProcessesTests.cs`

**Interfaces:**
- Produces: `public static class InstallProcesses`, with `const string DevicesExecutable = "DeadWare.Devices.exe"` and `IReadOnlyList<int> Running(string installDirectory, IEnumerable<(int Id, string? Path)> processes)`. It returns the ids whose path equals `<install>\DeadWare.Devices.exe`, compared case-insensitively.

- [ ] **Step 1: Write the failing tests:**
  - `Finds_host_from_install_folder`
  - `Ignores_host_from_another_folder`
  - `Ignores_processes_without_path`
- [ ] **Step 2: Run them to see them fail:** `dotnet test tests/DeadWare.Tests -c Release --filter FullyQualifiedName~InstallProcessesTests`.
- [ ] **Step 3: Implement it and wire it into the updater.** After the parent exits, poll `Process.GetProcessesByName("DeadWare.Devices")` every 100 ms for up to 10 s. If a host is still running, throw `OperationCanceledException("The lighting helper did not stop. App files were not replaced.")`.
- [ ] **Step 4: Run the tests until they pass.** Then run `dotnet build src/DeadWare.Updater -c Release`.
- [ ] **Step 5: Commit:** `fix(updater): wait for the lighting helper before replacing files`

### Task 12: Lighting page and sidebar item (WinUI)

**Files:**
- Create: `src/DeadWare.WinUI/LightingPage.cs`
- Modify: `src/DeadWare.WinUI/MainWindow.xaml.cs` (`Destinations` and the page factory), `.github/workflows/winui.yml` (the page smoke list)

**Interfaces:**
- Consumes: Task 10 `MonitorSession.Lighting` and `SetLightingAsync`; Task 5 messages and state.
- Produces: page key `lighting`, which `--page lighting` reaches, placed after `cooling` with the label `Lighting` and the Segoe Fluent glyph `\uE82F` (Lightbulb). The `Destinations` tuple gains an optional glyph used instead of the `Symbol`.

- [ ] **Step 1: Build the page**, code-only like `CoolingPage`:
  - **Header:** a `Lights on` ToggleSwitch, sending `SetLightsOnRequest`. A `Brightness` slider (0–100) that sends `SetBrightnessRequest` at most every 100 ms and once more on release.
  - **Status line:** from `HostState.Message`, the conflict descriptions and `DeviceHostClient.Problem`. When not elevated, it adds "Restart as administrator for RAM, motherboard and GPU lighting".
  - **When lighting is off in Settings:** the text "Lighting is off. Turn it on in Settings → Lighting." and an `Open Settings` button that navigates to `desktop`.
  - **Static card:** a `Set all colours` button opening a ColorPicker flyout. Picks are sent as `SetAllColorsRequest`, throttled to 100 ms. The flyout has `Undo`, which sends `ReplaceColorsRequest` with the snapshot taken when the flyout opened.
  - **Device list:** one card per `DeviceView`. A hardware-mode device shows "Own hardware effect: {Mode}", plus "Stays lit when lights are off" when that applies. Every zone row has a colour swatch button: a ColorPicker flyout sends `SetZoneColorRequest`, and `Use the shared colour` sends a null colour. An Unsupported device shows "DeadWare can't drive this device".
  - **Hidden window:** subscribe to `Lighting.Changed` only through `this.WhileShowing(attach, detach)`, so a hidden page does no work.
- [ ] **Step 2: Add the sidebar item and factory entry.**
- [ ] **Step 3: Build:** `dotnet build src/DeadWare.WinUI -c Release`. Expected: success.
- [ ] **Step 4: Add `'lighting'` to the page smoke list** in `.github/workflows/winui.yml`, in the loop that starts `foreach ($page in 'sensors', …`. On the runner lighting is off, so the page must open and quit cleanly with exit code 0. Leave this check to CI. Don't run the app locally from a Claude shell: it would create a shadow copy of DeadWare's AppData folder that hides the real one from later Claude reads.
- [ ] **Step 5: Commit:** `feat(winui): Lighting page and sidebar item`

### Task 13: Settings → Lighting (WinUI)

**Files:**
- Modify: `src/DeadWare.WinUI/SettingsPage.cs`, adding `Lighting` to `Sections` after `History and alerts`, plus its builder

**Interfaces:**
- Consumes: Task 10 `SetLightingAsync`; Task 4 `LightingConflicts.Find`; UpdateCore `InstallLocation.Check`.

- [ ] **Step 1: Build the section:**
  - **Enable lighting** (ToggleSwitch), calling `SetLightingAsync`. It is disabled until an OpenRGB path is set, and the reason is shown beside it.
  - **OpenRGB location:** a read-only text box and `Browse…`, a `FileOpenPicker` filtered to `OpenRGB.exe` and initialised with the window handle like the other pickers. Below it, the version from `FileVersionInfo.GetVersionInfo(path).ProductVersion`, with a note when it isn't 1.0rc3. Then the protection warning when `InstallLocation.Check(folder).Protected` is false: "Programs running without administrator rights can change this OpenRGB folder. DeadWare runs OpenRGB as administrator, so copy OpenRGB to C:\Program Files\OpenRGB and choose it here." (Reworded in the final fix wave, since an elevated host now refuses such a folder: "Programs running without administrator rights can change this OpenRGB folder, so while DeadWare runs as administrator it won't run OpenRGB from here. Copy OpenRGB to C:\Program Files\OpenRGB and choose it here.")
  - **Conflicts**, read only: `LightingConflicts.Find` over the current process list. Each blocking conflict's description is shown, refreshed when the section is shown. Step 5 adds the DeadLight startup-task status, the Corsair Service start type and iCUE's Run entry; step 1 shows running processes only.
- [ ] **Step 2: Build:** `dotnet build src/DeadWare.WinUI -c Release`.
- [ ] **Step 3: Smoke-check through CI.** The existing `desktop` entry in the `winui.yml` page smoke list covers the Settings page; there is no local run.
- [ ] **Step 4: Commit:** `feat(winui): Settings lighting category`

### Task 14: Packaging, notices and user guide

**Files:**
- Modify: `scripts/publish.ps1`, `THIRD-PARTY-NOTICES.md`, `docs/user-guide.md`, `Directory.Build.props` (Version `0.19.0`; the release itself stays the user's call)

- [ ] **Step 1: Publish the host in `publish.ps1`.** Publish `src/DeadWare.Devices/DeadWare.Devices.csproj` as the updater is published (self-contained, single file, compressed, with `-p:DebugType=None`) to `artifacts/devices-<id>`, then copy `DeadWare.Devices.exe` into `$stage`. Extend the licence inventory to read `src/DeadWare.Devices/obj/project.assets.json` too, deduplicating by package name.
- [ ] **Step 2: Add OpenRGB.NET to `THIRD-PARTY-NOTICES.md`** (MIT, diogotr7, version 3.1.1). Add a line saying OpenRGB itself isn't distributed with DeadWare: DeadWare talks to a user-supplied OpenRGB over its network SDK.
- [ ] **Step 3: Write a "Lighting" section in `docs/user-guide.md`.** It covers turning lighting on, choosing OpenRGB (with the protected-folder advice), what blocks it (DeadLight, another OpenRGB, iCUE), the Lighting Node Pro's own effect, and that Quit DeadWare stops the lights' helper.
- [ ] **Step 4: Run the full gate:** `pwsh -File scripts/publish.ps1 -Version 0.19.0 -TestFilter 'FullyQualifiedName!~MeasurementTests'`. Expected: all tests pass, and a package is produced within the updater limits (at most 2048 entries, 256 MB compressed, 768 MB expanded), containing `DeadWare.Devices.exe` and listing it in `deadware-package.json`.
- [ ] **Step 5: Commit and push:** `build: package the lighting device host; notices and user guide`

### Task 15: Hardware check on the user's PC (needs the user, step by step)

No code. Every step is described to the user and runs only with their go-ahead. Check GPU load first, and never automate the screen while they're gaming.

- [ ] **Step 1: The user copies OpenRGB.** They copy the whole OpenRGB 1.0rc3 folder from `I:\DeadLight\resources\openrgb` to `C:\Program Files\OpenRGB`; it needs administrator rights.
- [ ] **Step 2: The user quits DeadLight** from its tray icon. This also stops its OpenRGB.
- [ ] **Step 3: The user starts the test build** from the published stage folder, from Explorer, as administrator. This keeps it outside the Claude container.
- [ ] **Step 4:** In Settings → Lighting, the user chooses `C:\Program Files\OpenRGB\OpenRGB.exe` and turns lighting on.
- [ ] **Step 5: Confirm the devices and the Node Pro:** the Lighting page lists every device, and the Node Pro shows Rainbow Wave. Optionally, with the user's yes, run one Direct-mode re-test on the Node Pro.
- [ ] **Step 6: Check the colours:** Set all colours to one colour, change one zone, use Undo, set brightness to 20%, then lights off and on. The user confirms what they see.
- [ ] **Step 7: Check crash survival:** end the window process in Task Manager. The lights stay, and restarting the test build reconnects. At most once.
- [ ] **Step 8: Check Quit:** Quit DeadWare. `DeadWare.Devices.exe` and OpenRGB both exit within 5 s.
- [ ] **Step 9: Restore:** the user restarts their installed DeadWare and DeadLight.
- [ ] **Step 10: Record the results** in the PR description and open the PR against `main`. Don't merge or tag without the user.

### Task 16: lock OpenRGB's SDK port (whole-branch review D15, owner's decision 2026-11-19)

**Goal:** only DeadWare's own lighting helper, and only while it runs with administrator rights, may connect to the OpenRGB it runs as administrator. OpenRGB 1.0rc3's SDK has no authentication, and its profile requests save, load and delete `.orp` files at any path the client names (`NetworkServer.cpp:881-930`, `ProfileManager.cpp:35-69`, `:436-446`). The design is the spec's "The SDK port lock" (Ruling BG). Nothing persistent is added to Windows: everything the lock adds goes when its process exits.

**Files:**
- Create: `src/DeadWare.Devices/OpenRgbPortLock.cs`, `tests/DeadWare.Devices.Tests/OpenRgbPortLockTests.cs`
- Modify: `src/DeadWare.Devices/OpenRgbEngine.cs`, `src/DeadWare.Devices/Program.cs`, `tests/DeadWare.Devices.Tests/OpenRgbEngineTests.cs`, `tests/DeadWare.Devices.Tests/ElevatedFactAttribute.cs`, `docs/user-guide.md`, `docs/releases/v0.19.0.md`

**Interfaces:**
- Produces:
  - `internal sealed class OpenRgbPortLock : IDisposable`, with a constructor `OpenRgbPortLock(int port, string appPath, string userSddl = AdministratorsOnly)`, `static OpenRgbPortLock? ForHost(bool privileged)` (null without administrator rights; otherwise port 6744, `Environment.ProcessPath` and `D:(A;;CC;;;BA)`), `string? Ensure()` (null once the filters are in place, else the refusal) and `static string RefusalText(int code, string message)`.
  - `OpenRgbEngine` gains `Func<string?>? portGuard`, consulted before every launch beside `folderCheck`.

- [ ] **Step 1: Write the failing engine tests** with the fake runner: a guard that returns a reason fails the engine with exactly that text and starts nothing; the guard is consulted before every launch, restarts included; a refusal is final, neither retried nor counted in the backoff; a guard that throws refuses with the lock's text; `ForHost(false)` is null.
- [ ] **Step 2: Pin the x64 layouts** of `FWPM_SESSION0`, `FWPM_SUBLAYER0`, `FWPM_FILTER0`, `FWPM_FILTER_CONDITION0`, `FWPM_ACTION0`, `FWP_CONDITION_VALUE0`, `FWP_VALUE0`, `FWP_BYTE_BLOB` and `FWPM_DISPLAY_DATA0` against `fwpmtypes.h` and `fwptypes.h` (sizes and offsets), and a 32-bit process's refusal.
- [ ] **Step 3: Write the elevated integration tests** (`[ElevatedFact]`, skipped without administrator rights; CI's runner is elevated), each with a `TcpListener` on 127.0.0.1 and an ephemeral port in place of OpenRGB: the lock permitting this test's own image connects; permitting `cmd.exe` refuses with WSAEACCES; permitting this image with a descriptor for an unrelated SID refuses; a dual-mode IPv6 connect to `::ffff:127.0.0.1` is refused; another port stays reachable; after Dispose the refused connect succeeds; a filter deleted behind the lock's back is added again by the next `Ensure`.
- [ ] **Step 4: Implement** the lock (P/Invoke `fwpuclnt.dll`, `advapi32` and `kernel32`; free what WFP and `ConvertStringSecurityDescriptorToSecurityDescriptorW` allocate), the engine's guard and the wiring in `Program.cs`, which holds the lock for the host's life.
- [ ] **Step 5: Mutation checks** on an elevated run: drop the user condition (the unrelated-SID test fails); permit by port only (the `cmd.exe` test fails); drop `CLEAR_ACTION_RIGHT` (record whether any test notices).
- [ ] **Step 6: Docs:** the guide's OpenRGB paragraph and one release-note line.
- [ ] **Step 7: Commit:** `fix(lighting): only the lighting helper may connect to the OpenRGB it runs as administrator`

# Lighting Step 4 (Mouse and Everyday) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** DeadWare lights the Aerox 9 on its wireless receiver with battery and sleep time, turns lights off by shortcut, idle, lock and the tray, and behaves across PC sleep and wake, so DeadLight is no longer needed day to day.

**Architecture:** Everything new runs in the helper (`DeadWare.Devices`). A long-lived watcher thread with a hidden window delivers shortcut, lock and sleep/wake signals; idle time and pointer position are polled on the existing run loop. The host paints with an "effective lights" value (the saved switch and no temporary reason). The receiver mouse is a second device source beside OpenRGB, behind a small transport interface, presented with the cable mouse under one fixed key.

**Tech Stack:** .NET 10, C#, WinUI 3 (code-built pages), Win32 through `DllImport`, HidSharp 2.6.4, OpenRGB.NET 3.1.1, xUnit 2.9.3, `FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-11-22-lighting-step4-mouse-and-everyday-design.md` (read all of it, including "Amendments after plan research", which win where they differ). Approved mockup: `docs/superpowers/specs/2026-11-22-lighting-step4-mockups/mouse-and-everyday.html`. Behavioural reference: `I:\DeadLight` (`electron/aerox-wireless.ts`, `electron/lighting-power.ts`, `shared/lighting-power.ts`, `tests/aerox-*.test.ts`, `tests/lighting-power.test.ts`), read-only.

**For the directing session.** Branch `worktree-lighting-step4`, worktree `I:\DeadWare\.claude\worktrees\lighting-step4`, already pushed with the design (`e5a8728` and later). Working files go in `.superpowers/sdd/2026-11-22-lighting-step4/` (`progress.md` with rulings, `gate.sh`, `implementer-rules.md`, `reviewer-rules.md`, an interface-drift table gated by grep; copy the 3b folder's files as the starting point: `I:\DeadWare\.claude\worktrees\lighting-step3b\.superpowers\sdd\2026-11-21-lighting-step3b\`). The owner chooses the build mode before any building; record the start time for `arc_cost.py`. Workers run no git; the controller verifies every claim and commits. The final review is the saved workflow `whole-branch-review` with pinned `base` (`2141e00`) and `head`, run from the top-model session, plus a second reviewer for changed test files and the guide. Do not push a PR, tag, or run anything on the owner's hardware; stop after the last task and report.

## Global Constraints

- Work only in the worktree. Never run git or dotnet in `I:\DeadWare` itself. Never launch DeadWare, the helper, OpenRGB or DeadLight. Never tag.
- Build: `dotnet build DeadWare.slnx -c Release` (0 errors, no new warnings). Tests: all three projects with `--filter "FullyQualifiedName!~MeasurementTests"`; both pass before every commit.
- Every new test is seen failing for its stated reason before the code that passes it, or carries mutation evidence in the report.
- `HostState` and `DeviceView` grow only by optional trailing members; pipe `Version` stays 1. A reply is written before the state its request caused: never raise a state inside `Apply`, set `raise=true`.
- Input rule: the shortcut is `RegisterHotKey` only; idle is `GetLastInputInfo` only; mouse use is `GetCursorPos` only. No new hook. No file in `DeadWare.Devices` or `DeadWare.Core` may contain, even in a comment or inside an identifier, any word on `KeyPressRuleTests`' forbidden list, nor `WH_MOUSE`.
- `IKeyPressSource` may be named only in `DeviceHost.cs`, `KeyPressGate.cs`, `KeyPressHook.cs`, `Program.cs`. New Win32 imports are `[DllImport] static extern` (not `LibraryImport`); window procedures are kept-alive delegate fields (pattern: `src/DeadWare.WinUI/Tray.cs:19,34`).
- No new timer in `DeviceHost`: new waits are timestamps from `time.GetTimestamp()` checked on the existing loop (the test harness steps fake time by watching for a timer armed for exactly `Tick` or `FastTick`).
- Lock order in `DeviceHost` stays `raiseGate` → `watchGate` → `gate`. Watcher-thread events only set flags and call `Wake()`; they never take a host lock.
- `lighting.json`: members at their defaults are not written; an untouched 0.21.0 file is written byte for byte as before.
- New Core types used by Devices, WinUI or tests are `public` (Core has no `InternalsVisibleTo`). Types in Devices are `internal sealed`.
- UI text exactly as listed in the spec and this plan, British spelling. Core/Devices code style: terse, brace-tight, no spaces after commas (as `LightingPaint.cs`). WinUI style: conventional spacing, XML doc comments.
- Logging never includes pointer coordinates or anything typed; a shortcut is logged as words ("Ctrl + Alt + L").
- New code goes in new files; `DeviceHost.cs` only wires (spec, "Where the code goes").

## Review Focus

1. **The saved shortcut is already owned by another program when the helper starts.** Expected: helper runs normally, state carries `ShortcutProblem`, nothing is unsaved. Pinned in Task 4.
2. **The shortcut is pressed several times before the next repaint, and once during an idle spell.** Expected: presses compose; the idle press only brings lights back. Pinned in Tasks 2 and 4.
3. **A 0.21.0 `lighting.json` that never touches step 4's settings.** Expected: written byte for byte as before, version unchanged. Pinned in Task 1.
4. **The receiver is unplugged, or a HID call never returns, mid-frame.** Expected: helper stays responsive, other devices keep painting, the mouse goes stale and is retried. Pinned in Task 6.
5. **Lights were off by the switch when the PC slept; or the PC was locked when it woke.** Expected: lights stay off after wake; no frame is written while suspended or waking. Pinned in Task 5.
6. **The cable is plugged in twice within a minute.** Expected: one hardware rescan request, not two. Pinned in Task 9.

## Rulings from the plan check (binding; each overrides its task's text where they differ)

The directing session copies the rulings tagged with a task into that task's brief.

- **P1 (Tasks 6, 7, 8) A silent mouse must never slow the other devices.** Painting runs under the host's lock on every 33 ms tick, and each receiver write waits up to 250 ms for a reply. So after any `NoReply` the helper sends **nothing** to the receiver (no frame of any kind, no battery request) until `mouse.Moved`, and then at most one attempt per `MoveWindow`. Host test in Task 7: receiver present, `Reply=_=>null`, Wave running → the keyboard still gets a frame on every fast tick and the transport sees one write in total, not one per tick.
- **P2 (Tasks 1, 2)** The three reply codes (`BadEveryday`, `BadSleep`, `ShortcutSlow`) and their `Explain` rows and test rows are added in Task 1, not Task 2.
- **P3 (Task 9)** After `session.Abort()` the same tick must return at once: no `service.Paint`, no preview (otherwise `DeviceHost.cs:395` throws into `FailSession` and the page says the connection was lost). Set the next connect time to now + `RescanSettle`, raise a state. Test additions: status never leaves `Running`, the log has no "session error", and `Aborts` is asserted as a delta (it is already above 0 after start). The rescan request is made outside `BoundedSession.Run`'s 3 s watchdog or with limits that sum well under it (connect 1 s, drain 1 s).
- **P4 (Task 5)** "Nothing is written while suspended or waking" also binds the three other paths that paint or connect: `Mutate` (`DeviceHost.cs:772-781`: save, do not paint), the `RescanRequest` (`:562`) and `OnEngineChanged` → `ConnectLocked` (`:527`). Test: `A_request_while_waking_saves_but_writes_nothing`.
- **P5 (Tasks 7, 8)** `DeviceHost` gets an optional `AeroxMouse? mouse=null` constructor parameter (the service is built inside the host, `DeviceHost.cs:121`). Every `AeroxMouse` and `AeroxBattery` call is made under `gate`. Task 7 wires `mouse.Tick()` into every loop turn and `mouse.Scan()` into slow work; Task 8 adds `mouse.Poll()`. `Woke` → `mouse.Reopen()` belongs to Task 7 only. Host test in Task 7: movement releases a held frame.
- **P6 (Tasks 7, 8)** Two more must-sends: the receiver port was (re)opened, and `Answering` went from false to true (a battery read can be what notices). Otherwise a mouse switched off and on keeps showing its firmware colours until a colour changes. A test for each.
- **P7 (Tasks 10, 11)** An absent mouse is drawn unlit in the desk picture: `MouseTexts.Unlit(DeviceView v)=>v.Mouse?.Connection==MouseConnections.None` (Task 10, tested), used where the page colours a part from `ZoneView.Color` (`LightingPage.cs:882`) (Task 11).
- **P8 (all tasks)** The names `RegisterHotKey`, `GetLastInputInfo` and `GetCursorPos` may appear only in their own file (`EverydayWatcher.cs`, `InputClock.cs`, `InputClock.cs`), comments included, in both scanned projects. No comment in Core or Devices may contain a forbidden substring (a comment naming WinUI's key-state call would contain one).
- **P9 (Tasks 2, 4)** The shortcut-during-idle rule uses time, not the last paint: the host remembers when an idle spell last ended, and a press within 2 s of that only brings the lights back (holding Ctrl or Alt ends the spell before the letter arrives, and a loop turn may fall in between). `LightsOff.ShortcutTarget(bool lightsOn,bool idleJustEnded)`; the host passes `reason==Idle || idle ended within 2 s`. Test in Task 4 with a loop turn between `Idle=0` and `Press()`.
- **P10 (Task 4)** The saved shortcut is registered in `RunAsync` before the start-up delay and before the start-up-failure branch (`DeviceHost.cs:249-257`), so it works during both; presses are counted from then and applied when the loop starts (after a start-up failure the loop never runs: a press is then applied by a small wait in that branch, saving `LightsOn` only). A `null` answer at start sets `ShortcutProblem` and is retried on slow work.
- **P11 (Task 7)** A listed device with no OpenRGB index must not reach `indexByKey[...]`: `LightingDeviceService.cs:94`, `:115` (`Shape`: use index -1) and `:163`.
- **P12 (Task 1)** In `Finish`, a newer file whose `Everyday` or `Mouse` this build refuses resets those two members first, like `Devices` (`LightingSettingsCodec.cs:28-35`). `NewerShape` removes the named members unconditionally; add the two names.
- **P13 (Task 4)** `Shown` is not cached: `LightingKeys.Remap` replaces tables on the live settings. Use `reason is null?settings:settings.WithLightsOff()` at each use (a shallow copy is cheap).
- **P14 (Task 4)** `SetEverydayRequest`: a null `Everyday` → `Fail(BadEveryday)`; if the save fails after a successful registration, the old shortcut is registered again; with no watcher or `Problem` set, a shortcut change is refused with the watcher's problem text and the other settings are still saved.
- **P15 (Task 3)** `UnregisterHotKey`'s false when nothing was registered is ignored. If `WTSRegisterSessionNotification` fails it is retried on a few later messages (a 5 s window timer on the watcher thread is fine; it is not in `DeviceHost`) and logged once. Suspend can arrive twice: raise once. A resume (wParam 7) with no suspend since the last completed wake is ignored unless it is 0x12.
- **P16 (Task 7)** Existing tests to move to the fixed key: `DeviceHostTests.cs:1689` (`MouseKey`, four uses) and `LightingDeviceServiceTests.cs:419-434` (a log line containing the key). `aerox9` sorts after every `SERIAL-…` key, so tests pinning device or arrange order may move; fix the expectation, not the order. The page's "N devices through OpenRGB" count (`LightingPage.cs:640`) leaves out a receiver or absent mouse (Task 11).
- **P17 (Task 5)** "wake repaint done: N devices" is logged after the first successful paint following the wait; if the session was lost across sleep, it is logged when the reconnect's first paint succeeds.

---

### Task 1: Settings for Everyday and the mouse, file version 4

**Files:**
- Create: `src/DeadWare.Core/Everyday.cs` (`Shortcut`, `EverydaySettings`, `MouseSettings`, `Shortcuts`, `Everydays`, `MouseSleep`)
- Modify: `src/DeadWare.Core/LightingModels.cs` (`LightingSettings`: `CurrentVersion=4`, `Everyday`, `Mouse`, `WithLightsOff()`), `src/DeadWare.Core/DeviceTuning.cs:82` (`WrittenVersion`), `src/DeadWare.Core/LightingSettingsCodec.cs` (`Finish`, `Values`, `NewerShape`), `src/DeadWare.Devices/DeviceHost.cs:132-138` (`Clone`)
- Test: `tests/DeadWare.Tests/EverydayTests.cs` (new), `tests/DeadWare.Tests/LightingSettingsCodecTests.cs`, `tests/DeadWare.Tests/DeviceTuningTests.cs`, `tests/DeadWare.Devices.Tests/DeviceHostTests.cs`, `tests/DeadWare.Devices.Tests/LightingStoreTests.cs`

**Interfaces (Produces):**

```csharp
public sealed record Shortcut(int Modifiers,int Key);          // Modifiers: Shortcuts.Alt|Ctrl|Shift|Win; Key: Windows virtual-key number
public sealed class EverydaySettings{public Shortcut? Shortcut{get;set;}=Shortcuts.Default;public bool IdleOff{get;set;}public int IdleMinutes{get;set;}=10;public bool LockOff{get;set;}}
public sealed class MouseSettings{public int? SleepMinutes{get;set;}}   // null = leave as the mouse is, 0 = never
public static class Shortcuts{
    public const int Alt=1,Ctrl=2,Shift=4,Win=8;                // the values Windows uses for a global shortcut's modifiers
    public static readonly Shortcut Default=new(Ctrl|Alt,0x4C); // Ctrl + Alt + L
    public const string InvalidText="Hold Ctrl, Alt or the Windows key together with another key.";
    public static bool Valid(Shortcut s);                       // a table key, no unknown modifier bit, at least one of Ctrl/Alt/Win
    public static string? KeyName(int key);                     // null when the key is not in the table
    public static string Text(Shortcut? s);                     // "Ctrl + Alt + L"; order Ctrl, Alt, Shift, Win, key; null → "None"
    public static string TakenText(Shortcut s);                 // "Another program already uses Ctrl + Alt + P. Choose different keys."
}
public static class Everydays{
    public static readonly int[] IdleChoices=[1,2,5,10,15,30,60];
    public static bool IsDefault(EverydaySettings e);
    public static EverydaySettings Clone(EverydaySettings e);
    public static string? Validate(EverydaySettings e);         // HostReplyCodes.BadEveryday or null
}
public static class MouseSleep{public static readonly int[] Choices=[0,1,2,3,5,10,15,20];public static bool Valid(int? minutes);}
// LightingSettings
[JsonIgnore] public EverydaySettings Everyday{get;set;}=new();
[JsonIgnore] public MouseSettings Mouse{get;set;}=new();
public LightingSettings WithLightsOff();   // a shallow copy sharing every table, LightsOn=false
```

**Behaviour:**
- Key table (own table, in Core): A to Z (0x41-0x5A, names "A".."Z"), 0 to 9 (0x30-0x39), F1 to F24 (0x70-0x87), Space 0x20, Insert 0x2D, Delete 0x2E, Home 0x24, End 0x23, Page Up 0x21, Page Down 0x22, Left 0x25, Up 0x26, Right 0x27, Down 0x28, Pause 0x13.
- `Everyday` and `Mouse` are written through `[JsonInclude,JsonPropertyName("Everyday")] internal EverydaySettings? WrittenEveryday` (null while `Everydays.IsDefault`) and `WrittenMouse` (null while `SleepMinutes` is null), the `WrittenDevices` pattern at `LightingModels.cs:39-40`; setters adopt `value??new()`. Inside a written `Everyday`, `Shortcut` is always written (null means no shortcut).
- `WrittenVersion`: a version above `CurrentVersion` is kept; else 4 while `!Everydays.IsDefault(s.Everyday)||s.Mouse.SleepMinutes is not null`; else the existing 3 / 2 / 1 rule.
- `Finish` gives null members defaults; `Values` calls `Everydays.Validate` and `MouseSleep.Valid`; `NewerShape` also removes `"Everyday"` and `"Mouse"` of a wrong JSON type from a newer file.
- `DeviceHost.Clone` copies both (deeply). Tests using 4 as "newer" move to 5 (`LightingSettingsCodecTests.cs:127,137,266,271,276,281,287,342,348,355,594`; `DeviceTuningTests.cs:71-72,155-156`; `DeviceHostTests.cs:527,543`; `LightingStoreTests.cs:60`; leave the `"Layout":{"Version":2` on codec line 281).

- [ ] **Step 1: Write the failing tests.** In `EverydayTests`: `The_default_shortcut_reads_Ctrl_Alt_L` (`Assert.Equal("Ctrl + Alt + L",Shortcuts.Text(Shortcuts.Default))`); `No_shortcut_reads_None`; `Modifiers_read_in_the_order_Ctrl_Alt_Shift_Win`; `[Theory] A_shortcut_needs_Ctrl_Alt_or_Win_and_a_table_key` (Shift+L invalid, L alone invalid, Ctrl+0x5B invalid, modifier bit 16 invalid, Win+F24 valid); `Taken_text_names_the_keys`; `[Theory] Idle_minutes_must_be_on_the_list` (0, 3, 61 → `BadEveryday`; each of `IdleChoices` → null); `[Theory] Sleep_minutes_must_be_on_the_list_or_unset` (null, 0, 20 valid; 4, 21, -1 invalid). In the codec tests: `Defaults_for_everyday_and_mouse_are_not_written` (JSON contains neither `"Everyday"` nor `"Mouse"`, `Version` as before); `A_0_21_file_is_written_exactly_as_before` (round-trip a version 3 fixture string byte for byte); `[Theory] A_changed_everyday_or_a_set_sleep_time_writes_version_4` (idle on; lock on; shortcut null; shortcut Ctrl+Alt+K; idle minutes 30 with idle off; sleep 0; sleep 5); `No_shortcut_survives_a_round_trip`; `A_version_5_file_is_newer_and_not_quarantined`; `A_newer_file_with_a_misshapen_everyday_is_still_read`; `WithLightsOff_shares_tables_and_leaves_the_original_on`. In `DeviceHostTests`: `Clone_keeps_everyday_and_mouse` (through a `SetBrightnessRequest` after loading a file with both set; the saved file still has both).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~EverydayTests|FullyQualifiedName~LightingSettingsCodec|FullyQualifiedName~DeviceTuning"`. Expected: the new tests fail to compile or fail.
- [ ] **Step 3: Implement** the types, the written-member rule, the version rule, `Finish`/`Values`/`NewerShape`, `Clone`, and move the "newer" tests to 5.
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 4: everyday and mouse settings, file version 4`.

### Task 2: Lights-off rule and protocol types

**Files:**
- Create: `src/DeadWare.Core/LightsOff.cs`
- Modify: `src/DeadWare.Core/DeviceHostProtocol.cs` (two requests, `MouseView`, trailing members, reply codes)
- Test: `tests/DeadWare.Tests/LightsOffTests.cs` (new), `tests/DeadWare.Tests/DeviceHostProtocolTests.cs`, `tests/DeadWare.Tests/HostReplyCodesTests.cs`

**Interfaces (Consumes):** Task 1 types. **(Produces):**

```csharp
public static class LightsOff{
    public const string Idle="idle",Locked="locked",Waking="waking";
    public static string? Reason(LightingSettings s,long idleMs,bool locked,bool waking);
    public static bool Effective(LightingSettings s,string? reason)=>s.LightsOn&&reason is null;
    public static bool ShortcutTarget(bool lightsOn,bool idleJustEnded);     // the new saved LightsOn after a shortcut press (ruling P9)
}
[JsonDerivedType(typeof(SetEverydayRequest),"setEveryday")]   public sealed record SetEverydayRequest(EverydaySettings Everyday):HostMessage;
[JsonDerivedType(typeof(SetMouseSleepRequest),"setMouseSleep")] public sealed record SetMouseSleepRequest(int? Minutes):HostMessage;
public sealed record MouseView(string Connection,int? Battery,bool? Charging,bool Stale,int? SleepMinutes,bool SleepSent);
public static class MouseConnections{public const string Cable="cable",Wireless="wireless",Receiver="receiver",None="none";}
// DeviceView: …,IReadOnlyList<int>? SkippedLayerLights=null,MouseView? Mouse=null
// HostState:  …,bool KeyHook=false,string? LightsOffReason=null,string? ShortcutProblem=null,string? EverydayProblem=null
// HostReplyCodes: BadEveryday="bad everyday" → "Those everyday settings aren't valid.";
//                 BadSleep="bad sleep time" → "That sleep time isn't one of the choices.";
//                 ShortcutSlow="shortcut slow" → "Windows didn't answer about the shortcut in time. Try again."
```

**Behaviour:**
- `Reason`: null when `!s.LightsOn`; else `Waking` if waking; else `Locked` if `locked&&s.Everyday.LockOff`; else `Idle` if `s.Everyday.IdleOff&&idleMs>=s.Everyday.IdleMinutes*60_000L`; else null.
- `ShortcutTarget`: `lightsOn&&idleJustEnded` → true (the press only ends the idle spell); otherwise `!lightsOn`. In the tests below read `ShortcutTarget(true,Idle)` as `ShortcutTarget(true,true)` and the locked/waking cases as `ShortcutTarget(true,false)`. Ported from `I:\DeadLight\electron\lighting-power.ts:62-70`; because the switch is saved here and the host applies each press before the next, successive presses compose without the stale-snapshot branch.

- [ ] **Step 1: Write the failing tests.** `LightsOffTests`: `Defaults_never_turn_lights_off`; `Waking_then_locked_then_idle_is_the_order` (all three true → `Waking`; locked+idle → `Locked`); `[Theory] Idle_starts_at_exactly_the_chosen_minutes` (2 min: 119_999 → null, 120_000 → `Idle`); `Lock_needs_its_rule_on`; `Idle_needs_its_rule_on`; `The_switch_off_gives_no_reason`; `A_press_during_an_idle_spell_only_brings_lights_back` (`ShortcutTarget(true,Idle)` is true); `A_press_while_locked_or_waking_flips_the_switch` (`ShortcutTarget(true,Locked)` false, `ShortcutTarget(true,Waking)` false); `Five_presses_alternate` (fold from true with reason null: false,true,false,true,false). Protocol tests: add both requests, a `DeviceView` with `Mouse` and a `HostState` with the three new members to `Messages()` and assert they round-trip; `A_state_without_the_new_members_reads_with_nulls` (a literal 0.21.0 state JSON). Reply-code rows for the three new codes.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~LightsOff|FullyQualifiedName~DeviceHostProtocol|FullyQualifiedName~HostReplyCodes"`. Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 4: lights-off rule, everyday and mouse requests, state members`.

### Task 3: The watcher thread and the input clock

**Files:**
- Create: `src/DeadWare.Devices/EverydaySignals.cs` (`IEverydaySignals`, `IInputClock`), `src/DeadWare.Devices/EverydayWatcher.cs` (`EverydayWatcher:IEverydaySignals`), `src/DeadWare.Devices/InputClock.cs` (`InputClock:IInputClock`)
- Modify: `tests/DeadWare.Devices.Tests/KeyPressRuleTests.cs` (per-file import table, mouse-hook ban)
- Test: `tests/DeadWare.Devices.Tests/EverydayWatcherTests.cs` (new), `KeyPressRuleTests.cs`

**Interfaces (Produces):**

```csharp
internal interface IEverydaySignals:IDisposable{
    event Action? ShortcutPressed;      // raised on the watcher thread
    event Action<bool>? LockChanged;    // true = locked
    event Action? Suspending;
    event Action? Resumed;
    string? Problem{get;}               // why shortcut, lock and sleep handling are unavailable, or null
    bool? SetShortcut(Shortcut? shortcut,TimeSpan wait);   // true = registered or cleared; false = Windows refused; null = no answer in time or unavailable
}
internal interface IInputClock{long IdleMs();(int X,int Y) Pointer();}
internal sealed class EverydayWatcher:IEverydaySignals{public EverydayWatcher(HostLog log);}
internal sealed class InputClock:IInputClock{}
```

**Behaviour:**
- `EverydayWatcher`: one background thread named "Everyday" (pattern `KeyPressHook.cs:52,89-110` for start, ready wait 2 s, and stop by posted quit with join limit 2 s). It registers a window class and creates a hidden, never-shown **top-level** window (not message-only), then `WTSRegisterSessionNotification(hwnd,0)` and `RegisterSuspendResumeNotification(hwnd,0)`. Message loop: `WM_HOTKEY` (0x0312) → `ShortcutPressed`; `WM_WTSSESSION_CHANGE` (0x02B1) with wParam 7 → `LockChanged(true)`, 8 → `LockChanged(false)`; `WM_POWERBROADCAST` (0x0218) with wParam 4 → `Suspending`, 7 or 0x12 → `Resumed`. `SetShortcut` posts a private message (`WM_APP+1`) to the window and waits on an event for the result; on the thread: `UnregisterHotKey(hwnd,1)`, then for non-null `RegisterHotKey(hwnd,1,modifiers|0x4000 /*MOD_NOREPEAT*/,key)`. If the class or window cannot be created: `Problem` is set to a sentence starting "Shortcut and lock are unavailable: ", the thread ends, `SetShortcut` returns null. `Dispose` unregisters and destroys on the thread.
- Imports allowed in `EverydayWatcher.cs` (names with a trailing `W` stripped, as the test compares): `RegisterClassEx`, `UnregisterClass`, `CreateWindowEx`, `DestroyWindow`, `DefWindowProc`, `GetMessage`, `DispatchMessage`, `PostMessage`, `PostQuitMessage`, `RegisterHotKey`, `UnregisterHotKey`, `WTSRegisterSessionNotification`, `WTSUnRegisterSessionNotification`, `RegisterSuspendResumeNotification`, `UnregisterSuspendResumeNotification`, `GetModuleHandle`. In `InputClock.cs`: `GetLastInputInfo`, `GetTickCount`, `GetCursorPos`. (`IdleMs` = `GetTickCount()` minus `LASTINPUTINFO.dwTime`, unsigned 32-bit arithmetic so the 49-day wrap is harmless.)
- `KeyPressRuleTests`: replace the single hard-wired pin (`:100-108`) with a table `file → exact import list` covering `KeyPressHook.cs` (its present seven), `EverydayWatcher.cs` and `InputClock.cs` (the lists above), asserted with the existing regex; add `"WH_MOUSE"` to a new `NoMouseHook` check over every scanned file; add assertions that `RegisterHotKey` appears only in `EverydayWatcher.cs`, and `GetLastInputInfo` and `GetCursorPos` only in `InputClock.cs`.

- [ ] **Step 1: Write the failing tests.** `KeyPressRuleTests`: `Each_pinned_file_imports_exactly_its_list`; `No_file_names_a_mouse_hook`; `The_shortcut_idle_and_pointer_calls_live_in_their_own_files`. `EverydayWatcherTests` (real window, no hotkey registered, safe on any PC): `It_starts_and_stops_without_a_problem` (`Problem` null; `Dispose` returns inside 5 s); `Clearing_the_shortcut_answers_true` (`SetShortcut(null,1 s)` is true); `After_dispose_it_answers_null`. `InputClockTests`: `Idle_time_is_not_negative_and_the_pointer_reads` (no exception, `IdleMs()>=0`).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~KeyPressRule|FullyQualifiedName~EverydayWatcher|FullyQualifiedName~InputClock"`. Expected: fail.
- [ ] **Step 3: Implement.** Unused struct fields need `#pragma warning disable CS0169,CS0649` as at `KeyPressHook.cs:138`.
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass. In the report, show mutation evidence for the rule test: adding one import to `InputClock.cs` fails `Each_pinned_file_imports_exactly_its_list`.
- [ ] **Step 5: Commit** `Lighting 4: everyday watcher thread and input clock, input rule pinned per file`.

### Task 4: Effective lights in the host: shortcut, idle, lock

**Files:**
- Create: `src/DeadWare.Devices/EverydayState.cs` (`internal sealed class EverydayState`: the flags and counters the signals set, and the rule's evaluation)
- Modify: `src/DeadWare.Devices/DeviceHost.cs` (constructor, `RunAsync`, `TickWork`, `BuildLocked`, `Watch`, `Apply`, preview), `src/DeadWare.Devices/Program.cs:63-64`
- Test: `tests/DeadWare.Devices.Tests/DeviceHostEverydayTests.cs` (new; reuse the harness of `DeviceHostTests` by making its `Build`/`Running`/`Send`/`AdvanceUntil` helpers reachable, e.g. a shared `partial class` or a base class: builder's choice), `tests/DeadWare.Devices.Tests/FakeEveryday.cs` (new: `FakeSignals:IEverydaySignals` with `Press()`, `Lock(bool)`, `Suspend()`, `Resume()`, `Refuse` (a `Func<Shortcut?,bool?>`), `Shortcuts` (list of what was set); `FakeInput:IInputClock` with settable `Idle` and `At`)

**Interfaces (Consumes):** Tasks 1-3. **(Produces):**

```csharp
public DeviceHost(…,IKeyPressSource? keys=null,IEverydaySignals? signals=null,IInputClock? input=null)
internal sealed class EverydayState{
    public void Press();public void SetLocked(bool locked);public void SetSuspended();public void SetResumed();   // called from the watcher thread; lock-free
    public int TakePresses();public bool Locked{get;}
}
// DeviceHost private: LightingSettings Shown  (settings when the reason is null, else a cached settings.WithLightsOff(); rebuilt when settings is adopted or the reason changes)
```

**Behaviour:**
- New private step `EverydayWork()` runs every loop turn in `RunAsync` before `TickWork` (after `:281`), so it is not behind `TickWork`'s early return (`:379`). Under `gate` it: takes pending presses and for each sets `settings.LightsOn=LightsOff.ShortcutTarget(settings.LightsOn,paintedReason)` through the same save path as `Mutate` (validate, save, adopt), logging `shortcut pressed: lights on|off|back from idle`; reads `input.IdleMs()` only while `settings.Everyday.IdleOff`; computes `reason=LightsOff.Reason(...)`; when the reason or `LightsOn` changed since the last turn: rebuilds `Shown`, calls `presses.Clear()`, sets a flag so `Raise()` runs after the lock, and logs `idle spell started (N min)` / `idle spell ended` / `locked` / `unlocked`.
- `paintedReason` is the reason in force at the last `service.Paint`. After a press that returned from idle, `paintedReason` is cleared at once so a second press toggles.
- Every paint, preview, hook and tick-rate read in the host uses `Shown` instead of `settings` (spec appendix; `:562` is the rescan request's paint and follows it too; `:564` keeps writing the saved value). `BuildLocked` passes `service.Views(Shown)`, `Clone(settings)` (the saved value), `LightsOffReason: reason`, `ShortcutProblem`, `EverydayProblem: signals?.Problem`.
- When effective lights turn true: `service.Reset()` is NOT called (that is for reconnects); instead the per-device diff is cleared the way `SetLightsOnRequest` already causes today (the builder confirms from `Mutate`'s paint that a lights-on transition repaints every device, and adds a `service.Repaint()` that clears `lastSent` only if it does not).
- `SetEverydayRequest` in `Apply`: `Everydays.Validate` → `Fail(BadEveryday)`; if the shortcut differs and `signals` is not null: `signals.SetShortcut(new,TimeSpan.FromSeconds(1))`; false → re-register the old one and `Fail(Shortcuts.TakenText(new))`; null → `Fail(ShortcutSlow)`; then `Mutate` to save. A successful set clears `ShortcutProblem`.
- At start (first loop turn, after `Initialize`): register the saved shortcut; false → `ShortcutProblem=Shortcuts.TakenText(saved)` and log `shortcut refused: Ctrl + Alt + L`; true → log `shortcut registered: …`.
- With `signals` and `input` null (every existing test) behaviour is exactly as today.
- `Program.cs`: `using IEverydaySignals signals=new EverydayWatcher(log);` beside `:63`, `new InputClock()`, both passed to the host.

- [ ] **Step 1: Write the failing tests** (`DeviceHostEverydayTests`): `A_press_turns_lights_off_and_saves_it` (state `Settings.LightsOn` false, `LightsOffReason` null, file on disk has `LightsOn` false, last frame black); `A_second_press_turns_them_on_again`; `Three_presses_before_a_tick_compose` (ends off); `Idle_turns_lights_off_without_touching_the_switch` (idle on, 1 min, `Idle=60_000` → state reason `"idle"`, `Settings.LightsOn` true, file unchanged, frame black); `Coming_back_from_idle_repaints` (`Idle=0` → reason null, frame has the saved colour); `A_press_during_an_idle_spell_only_brings_lights_back` (reason idle, then `Idle=0` and `Press()` in the same turn → on, switch still true); `Idle_is_not_read_while_its_rule_is_off` (`FakeInput` counts calls: 0); `Lock_turns_lights_off_only_with_its_rule_on`; `Unlock_brings_them_back`; `The_switch_off_shows_no_reason_while_locked`; `The_hook_is_removed_and_presses_cleared_while_idle` (with a reacting layer: `FakeKeys.Stops` rises, state `KeyHook` false; back → installed again); `No_preview_frame_is_sent_while_idle`; `A_refused_saved_shortcut_is_reported_and_the_helper_runs` (`Refuse=_=>false` → `ShortcutProblem` equals `Shortcuts.TakenText(Shortcuts.Default)`, status Running); `Set_everyday_saves_and_registers` (`FakeSignals.Shortcuts` ends with the new one; file has `Everyday`, version 4); `A_taken_shortcut_is_refused_and_the_old_one_restored` (reply not ok with `TakenText`; file unchanged; last two `Shortcuts` entries are new then old); `A_slow_answer_refuses_with_shortcut_slow`; `Bad_idle_minutes_are_refused`; `A_press_works_while_OpenRGB_is_not_running` (engine not started: `LightsOn` flips and saves); `Each_event_leaves_its_log_line` (`LogText()` contains the six lines above); `Without_signals_nothing_changes` (existing behaviour, `LightsOffReason` null).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~DeviceHostEveryday`. Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass, including all existing `DeviceHostTests`.
- [ ] **Step 5: Commit** `Lighting 4: shortcut, idle and lock in the helper with effective lights`.

### Task 5: Sleep and wake

**Files:**
- Modify: `src/DeadWare.Devices/EverydayState.cs`, `src/DeadWare.Devices/DeviceHost.cs`
- Test: `tests/DeadWare.Devices.Tests/DeviceHostEverydayTests.cs`

**Interfaces (Consumes):** Task 4. **(Produces):** `public static readonly TimeSpan WakeDelay=TimeSpan.FromSeconds(10);` on `DeviceHost`; `Action? Woke` hook point used by Task 8 (`internal event Action? Woke;` raised once when the wake delay ends, under no lock).

**Behaviour (spec "Sleep and wake", amendment 4):**
- `Suspending`: `quiet=true` (reason `Waking`), `StopWatching()`, `presses.Clear()`, log `suspend`. Nothing is written to any device, and the handler does no I/O.
- `Resumed`: record `wakeAt=time.GetTimestamp()` only if not already waking after a resume (a second signal restarts nothing), log `resume`, raise a state.
- While `quiet`: `TickWork` does not connect, rescan, paint or send previews; the state's reason is `Waking`.
- On the first loop turn at least `WakeDelay` after `wakeAt`: `quiet=false`; `service.Reset()`; `rescanPending=true`; raise `Woke`; re-evaluate the reason; the next `TickWork` rescans and paints; log `wake repaint done: N devices`.
- A `Resumed` with no `Suspending` before it (Modern Standby can do this) is handled the same way.

- [ ] **Step 1: Write the failing tests:** `Suspend_writes_nothing_and_shows_waking` (LED write count unchanged across several ticks; reason `"waking"`); `Nothing_is_written_until_ten_seconds_after_resume` (advance 9 s: no writes; advance past 10 s: every device repainted with its saved colour, including one whose colour did not change); `A_second_resume_signal_does_not_restart_the_wait`; `Lights_off_by_the_switch_stay_off_after_wake` (frames after wake are black; reason null); `Locked_at_wake_shows_locked_after_the_wait` (lock rule on, `Lock(true)` during sleep → after the wait reason `"locked"`); `The_hook_returns_after_wake_when_a_layer_needs_it`; `Suspend_and_wake_leave_their_log_lines`.
- [ ] **Step 2: Run** the filter of Task 4. Expected: the new tests fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 4: stop on sleep, repaint ten seconds after wake`.

### Task 6: The Aerox receiver driver

**Files:**
- Create: `src/DeadWare.Devices/AeroxTransport.cs` (`IAeroxTransport`, `IAeroxPort`), `src/DeadWare.Devices/AeroxDriver.cs`, `src/DeadWare.Devices/HidAeroxTransport.cs`
- Modify: `src/DeadWare.Devices/DeadWare.Devices.csproj` (add `<PackageReference Include="HidSharp" Version="2.6.4" />`)
- Test: `tests/DeadWare.Devices.Tests/AeroxDriverTests.cs` (new), `tests/DeadWare.Devices.Tests/FakeAerox.cs` (new: `FakeAeroxTransport` with `Products` (set of product ids), `Opens`, `Writes` (list of 64-byte arrays), `Reads` count, `Reply` (`Func<byte[],byte[]?>` from the last write), `Queue` (replies to hand out first), `HangWrite` (a `ManualResetEventSlim`), `ThrowOnWrite`)

**Interfaces (Produces):**

```csharp
internal interface IAeroxTransport{IReadOnlyCollection<int> Present();IAeroxPort Open(int product);}   // product ids (0x1858 receiver, 0x185A cable) whose interface has usage page 0xFFC0, usage 1
internal interface IAeroxPort:IDisposable{void Write(ReadOnlySpan<byte> packet);byte[]? Read(int limitMs);}   // packet: 64 bytes, [0]=0 (report id), [1]=opcode; Read: reply starting AT the opcode, null on timeout
internal enum AeroxResult{Sent,Busy,NoReply,Failed,Absent}
internal sealed class AeroxDriver:IDisposable{
    public const int Vendor=0x1038,Receiver=0x1858,Cable=0x185A,ReadLimitMs=250,MaxSkipped=16;
    public static readonly TimeSpan Deadline=TimeSpan.FromMilliseconds(1500);
    public AeroxDriver(IAeroxTransport transport,HostLog log,TimeSpan? deadline=null);
    public void Scan();                                   // re-reads Present(); a vanished receiver closes its port
    public bool ReceiverPresent{get;}public bool CablePresent{get;}
    public bool Answering{get;}                           // the last receiver exchange got a reply
    public bool SleepSent(int? minutes);                  // this value has been delivered on the current port
    public AeroxResult Paint(ReadOnlySpan<Rgb> four,int? sleepMinutes);
    public AeroxResult Battery(bool cable,out int percent,out bool charging);
    public void Close();                                  // wake: the next exchange reopens
    public static bool TryBattery(byte[]? reply,byte opcode,out int percent,out bool charging);
}
```

**Behaviour (spec "The mouse", ported from `aerox-wireless.ts`; offsets are DeadLight's):**
- `Paint` on the receiver, in order: if `sleepMinutes` is not null and not yet sent on this port: `[0,0x69,ms&255,(ms>>8)&255,(ms>>16)&255]` with `ms=minutes*60000`, and, once per changed value per driver lifetime, `[0,0x51,0]` whose missing reply is accepted; then `[0,0x63,10]`; zones 0-2 `[0,0x61,1,z,r,g,b]`; zone 3 `[0,0x66,1,0,r,g,b]`. Each write is 64 bytes and is followed by one `Read(250)`.
- `Battery`: request `[0,0xD2]` on the receiver, `[0,0x92]` on the cable (a short-lived port opened and disposed per reading). Skip up to 16 replies whose first byte is not the opcode, within the one 250 ms window, never resending. `TryBattery`: valid when `reply.Length>=2&&reply[0]==opcode` and `percent=((reply[1]&0x7F)-1)*5` is 0..100; `charging=(reply[1]&0x80)!=0`.
- One lock; a second caller gets `Busy` at once (no queue). Each exchange runs on a worker and is waited for `deadline`; on timeout the port is abandoned (never reused, never disposed on the caller's thread), the result is `Failed`, and further calls on a hung worker return `Busy`.
- A write or read that throws → port closed, `Failed`. A missing reply to a colour or battery write → `NoReply`, port stays open, `Answering=false`; logged once (`receiver: no reply (asleep or out of reach)`) and once when replies return.
- A frame that ends `Busy`, `NoReply` or `Failed` is not recorded as sent.
- Log lines: `receiver opened`, `receiver closed`, `receiver failed: <message>`, `mouse sleep time sent: N min`, `mouse sleep time saved`.
- `HidAeroxTransport`: HidSharp `DeviceList.Local.GetHidDevices(0x1038)`, product 0x1858 or 0x185A, usage page 0xFFC0 / usage 1 from the report descriptor (fallback: `mi_03` in `DevicePath`, case-insensitive). `Write` copies the packet into a buffer of `GetMaxOutputReportLength()`; `Read` uses `ReadTimeout=limitMs`, returns the bytes after the report id, and returns null on `TimeoutException`. Not unit-tested; hand trace in the report.

- [ ] **Step 1: Write the failing tests** (`AeroxDriverTests`; default fake reply `[0x61]`): `It_writes_brightness_then_four_zones_with_wireless_opcodes` (colour (2,30,140): first seven bytes of the five packets are `[0,0x63,10,0,0,0,0]`, `[0,0x61,1,0,2,30,140]`, `[0,0x61,1,1,2,30,140]`, `[0,0x61,1,2,2,30,140]`, `[0,0x66,1,0,2,30,140]`; each 64 long; five reads); `The_sleep_time_goes_first_in_little_endian_milliseconds` (5 → `[0,0x69,0xE0,0x93,0x04]`; 0 → `[0,0x69,0,0,0]`; 1 → `[0,0x69,0x60,0xEA,0x00]`); `The_save_goes_once_per_changed_value` (two paints, close, paint: one `0x51`; change to 2: a second); `The_sleep_time_is_sent_again_after_a_reopen`; `A_missing_save_reply_is_accepted`; `Null_sleep_minutes_sends_no_sleep_time`; `A_cable_only_mouse_is_not_painted` (products {0x185A} → `Absent`, no open); `No_reply_keeps_the_port_open_and_is_not_a_failure` (`Reply=_=>null` → `NoReply`, `Opens` stays 1 over three paints, log has the no-reply line once); `A_throwing_write_closes_the_port_and_the_next_paint_reopens`; `A_stuck_write_fails_at_the_deadline_and_later_calls_are_busy` (deadline 100 ms, `HangWrite` unset: `Failed`, then `Busy`; one write in total; the test returns inside 2 s); `An_unplugged_receiver_closes_its_port_on_scan`; `[Theory] Battery_decodes_five_percent_steps_and_charging` (`[0xD2,16]` → 75 false; `[0xD2,0x95]` → 100 true; `[0xD2,1]` → 0 false; `[0x92,0x94]` → 95 true); `[Theory] Bad_battery_replies_are_rejected` (null, `[0xD2]`, `[0xD2,0]`, `[0xD2,127]`, `[0x61,16]`); `Cable_battery_skips_lighting_acknowledgements` (queue `[0x21,0]`,`[0x23,0]`,`[0x92,0x94]` → 95 charging; 1 write, 3 reads); `Cable_battery_opens_and_closes_a_port_per_reading`; `A_battery_read_during_a_paint_is_busy`.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter FullyQualifiedName~AeroxDriver`. Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass. **Hand trace** `HidAeroxTransport` in the report with file:line for: interface choice, report-id handling on write and read, timeout → null.
- [ ] **Step 5: Commit** `Lighting 4: Aerox receiver driver on HidSharp behind a transport`.

### Task 7: One mouse, two ways in

**Files:**
- Create: `src/DeadWare.Core/AeroxIdentity.cs`, `src/DeadWare.Devices/AeroxMouse.cs`
- Modify: `src/DeadWare.Devices/LightingDeviceService.cs` (constructor, `Rescan`, `Paint`, `Views`, `Preview`), `src/DeadWare.Devices/DeviceHost.cs` (`RescanLocked` before `:483`, constructor), `src/DeadWare.Devices/Program.cs`
- Test: `tests/DeadWare.Tests/AeroxIdentityTests.cs` (new), `tests/DeadWare.Devices.Tests/AeroxMouseTests.cs` (new), `tests/DeadWare.Devices.Tests/LightingDeviceServiceTests.cs`, existing tests that use the cable mouse's key (search `SERIAL-5` and `FAKE-2` under `tests/`)

**Interfaces (Consumes):** Tasks 1, 2, 3 (`IInputClock`), 6. **(Produces):**

```csharp
public static class AeroxIdentity{
    public const string Key="aerox9",NamePrefix="SteelSeries Aerox 9",ReceiverName="SteelSeries Aerox 9 Wireless";
    public static readonly string[] Zones=["Front","Middle","Rear","Reactive"];
    public static bool Is(string deviceName);                       // StartsWith(NamePrefix,Ordinal)
    public static bool HasSaved(LightingSettings s);                // any zone colour, tuning, hardware mode or layout entry under Key
    public static bool Adopt(LightingSettings s,string? cableKey);  // the copy rule; true when anything was copied
    public static LightingDevice Receiver();                        // Key, ReceiverName, type 6, four one-light zones, modes ["Direct"], active 0
}
internal sealed class AeroxMouse{
    public static readonly TimeSpan MoveWindow=TimeSpan.FromSeconds(1);
    public AeroxMouse(AeroxDriver driver,IInputClock input,TimeProvider time,HostLog log);
    public void Tick();                       // samples the pointer; a changed position stamps "moved now"
    public bool Moved{get;}                   // the pointer moved within MoveWindow
    public void Scan();                       // slow work: driver.Scan()
    public bool ReceiverPresent{get;}public bool CablePresent{get;}public bool Seen{get;}   // Seen: either was present earlier in this run
    public void Must();                       // the next frame is a must-send
    public bool Paint(ReadOnlySpan<Rgb> four,int? sleepMinutes);   // true when the frame reached the mouse
    public void Reopen();                     // wake
}
// LightingDeviceService
public LightingDeviceService(IOpenRgbSession session,TimeProvider time,HostLog log,AeroxMouse? mouse=null)
public bool MouseSaved{get;set;}              // the host sets it from AeroxIdentity.HasSaved before each Rescan
public string? CableKey{get;}                 // the cable mouse's own serial|location key while OpenRGB lists it
public bool MouseAbsent{get;}
```

**Behaviour (spec "One identity", "The sleep gate", amendments 6 and 11):**
- `Rescan`: an OpenRGB controller with `AeroxIdentity.Is(name)` is listed under `AeroxIdentity.Key` (its real key kept in `CableKey`, its index in `indexByKey[Key]`), always, with or without `mouse`. If none is listed and `mouse?.ReceiverPresent` → append `AeroxIdentity.Receiver()`. Else if `mouse` is not null and (`mouse.Seen||MouseSaved`) → append `AeroxIdentity.Receiver()` and set `MouseAbsent`. `Shape` includes which of the three it is, so a change raises a state.
- `Paint` for `Key` with no OpenRGB index: skip when `MouseAbsent`; else `LightingPaint.Paint(...)` → four colours → send only when they differ from the last frame that reached the mouse or a must-send is pending. Gate: when `settings.Mouse.SleepMinutes` is above 0 and `driver.SleepSent(minutes)`, a frame that is not a must-send is held unless `mouse.Moved`. A held, busy, unanswered or failed frame does not advance "last sent"; a must-send stays pending until a frame reaches the mouse and is retried only when `mouse.Moved` after a `NoReply`.
- Must-sends are set by the host: effective lights changed (Task 4's transition), a `Mutate` that touched the mouse's colours or tuning (simplest: every successful `Mutate`), the receiver appearing in `Rescan`, and `Woke` (Task 5, which also calls `mouse.Reopen()`).
- Log `mouse: holding frames (still)` once when holding starts and `mouse: released (moved|must-send)` once when it ends.
- `Views`: the mouse's `DeviceView` is built by `LightingPaint.View` as for any device (`Kind` Direct); Task 8 adds `Mouse`. `Preview`/`PreviewLights`: an absent mouse contributes no colours.
- `AeroxIdentity.Adopt` (a copy, never a move; old entries stay): candidates are `cableKey` if not null, then every device key other than `Key` found in `ZoneColors` whose entries' `ZoneName`s are all within `Zones` and include `"Reactive"`, in ordinal key order. For zone colours: if no `ZoneColors` key starts with `Key+"#"`, copy the first candidate's entries to `LightingKeys.Zone(Key,i)`. For tuning: if `Devices` has no `Key`, copy (deep, `DeviceTunings.Clone` pattern) the first candidate that has one. For `HardwareModes`: same. Layout is not copied.
- `RescanLocked`: `service.MouseSaved=AeroxIdentity.HasSaved(settings)` before `service.Rescan()`; after it and before `LightingKeys.Remap` (`:483`): `if(AeroxIdentity.Adopt(settings,service.CableKey)) save=true` (joins the one `SaveQuietly()` at `:502`). Also once in `Initialize` with `cableKey` null.
- `Program.cs` builds `new AeroxMouse(new AeroxDriver(new HidAeroxTransport(),log),input,TimeProvider.System,log)` and passes it into the session factory's service; the builder threads it through however `LightingDeviceService` is constructed today.
- Existing tests that address the cable mouse by `SERIAL-5|…` move to `AeroxIdentity.Key`.

- [ ] **Step 1: Write the failing tests.** `AeroxIdentityTests`: `Both_names_are_the_mouse` ("SteelSeries Aerox 9 Wireless Wired", "SteelSeries Aerox 9 Wireless"); `Cable_colours_are_copied_and_the_old_entries_stay`; `Colours_already_under_the_fixed_key_are_not_overwritten`; `Tuning_is_copied_separately_from_colours` (aerox9 has colours but no tuning → only tuning copied); `Without_a_cable_key_the_zone_names_find_the_old_entry`; `A_keyboard_entry_is_never_mistaken_for_the_mouse`; `Two_old_entries_take_the_first_by_key`; `A_layout_point_alone_is_saved_but_blocks_no_copy`; `Adopt_twice_copies_once`. `LightingDeviceServiceTests`: `The_cable_mouse_is_listed_under_the_fixed_key` (and `CableKey` is `SERIAL-5|HID: \\?\HID#FAKE-2`); `With_no_cable_and_a_receiver_the_receiver_mouse_is_listed`; `With_both_only_the_cable_mouse_is_listed_and_the_receiver_gets_no_frame`; `With_neither_a_seen_or_saved_mouse_is_listed_absent_and_never_painted`; `With_neither_and_nothing_saved_no_mouse_is_listed`; `An_OpenRGB_hotplug_cannot_move_a_mouse_frame_onto_another_device` (reorder controllers; receiver frames still go to the fake transport only); `An_unchanged_colour_is_not_resent_to_the_receiver` (ten paints, five writes). `AeroxMouseTests` (fake clock, `FakeInput`): `With_no_sleep_time_frames_flow_while_still`; `With_never_frames_flow_while_still`; `With_a_sleep_time_a_still_mouse_gets_no_effect_frames` (150 changing frames, zero writes after the first delivery); `Moving_releases_frames_for_one_second`; `A_must_send_goes_through_while_still` (lights off → five writes carrying black); `A_held_frame_is_not_remembered_as_sent` (after release the current colours are written); `An_unanswered_must_send_waits_for_movement_not_a_timer`; `The_gate_needs_the_sleep_time_delivered_first` (sleep chosen but `SleepSent` false → frames flow, so the sleep time itself gets out); `Holding_and_release_are_logged_once`. Host: `The_cable_colours_are_adopted_before_the_layout_is_arranged` (file with colours under the old key; after start the state shows them on `aerox9` and the file holds both).
- [ ] **Step 2: Run** the three test projects filtered to `Aerox|LightingDeviceService`. Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 4: one mouse under a fixed key, receiver painting with the sleep gate`.

### Task 8: Battery, sleep time and the mouse view

**Files:**
- Create: `src/DeadWare.Devices/AeroxBattery.cs`
- Modify: `src/DeadWare.Devices/AeroxMouse.cs`, `src/DeadWare.Devices/LightingDeviceService.cs` (`Views`), `src/DeadWare.Devices/DeviceHost.cs` (`Apply`, slow work, `Woke`)
- Test: `tests/DeadWare.Devices.Tests/AeroxBatteryTests.cs` (new), `tests/DeadWare.Devices.Tests/DeviceHostEverydayTests.cs`

**Interfaces (Consumes):** Tasks 2, 6, 7. **(Produces):**

```csharp
internal sealed class AeroxBattery{
    public static readonly TimeSpan ReceiverEvery=TimeSpan.FromSeconds(60),CableEvery=TimeSpan.FromSeconds(10),StaleAfter=TimeSpan.FromSeconds(120);
    public AeroxBattery(AeroxDriver driver,TimeProvider time,HostLog log);
    public bool Poll(bool cable,bool receiver,bool moved);   // true when the published reading changed
    public int? Percent{get;}public bool? Charging{get;}public bool Stale{get;}
    public void Invalidate();
}
// AeroxMouse
public MouseView View(int? sleepMinutes);     // Connection per the spec's rule, battery from AeroxBattery
public bool Poll();                           // called every loop turn; true when the view changed
```

**Behaviour (spec "Battery", "Sleep time"):**
- Receiver: at most once per 60 s and only while `moved`; cable: every 10 s. `Busy` is skipped, not queued, and does not count as a reading or a failure. A reading older than 120 s, a failed or invalid read, and a change of `cable` make it stale (the last value is kept as "last known"). A receiver lighting failure does not invalidate a cable reading. Each successful reading logs `battery: cable|receiver N% charging|not charging`.
- `Connection`: `Cable` if the cable is present; else `None` if the receiver is absent; else `Wireless` if the reading is fresh and `driver.Answering`; else `Receiver`.
- `SetMouseSleepRequest` in `Apply`: `MouseSleep.Valid` else `Fail(BadSleep)`; `Mutate(s=>{s.Mouse.SleepMinutes=r.Minutes;…})`; a must-send follows so the value goes out with the next frame. `View.SleepSent` is `driver.SleepSent(minutes)` and false on the cable.
- `Views` sets `Mouse=mouse.View(settings.Mouse.SleepMinutes)` on the `aerox9` view (when `mouse` is null the member stays null, as in every existing test).
- The host calls `mouse.Poll()` each loop turn while lighting is running (not while quiet, blocked or OpenRGB is not running: spec "follows the helper's state as a whole") and raises a state when it returns true. `Woke` → `mouse.Reopen()`, battery `Invalidate()`.

- [ ] **Step 1: Write the failing tests.** `AeroxBatteryTests`: `The_receiver_is_asked_only_after_movement`; `The_receiver_is_asked_at_most_once_a_minute`; `A_still_mouse_is_never_asked_even_after_a_minute`; `The_cable_is_read_every_ten_seconds_without_movement`; `A_reading_goes_stale_after_two_minutes_and_keeps_its_value`; `[Theory] A_bad_reply_keeps_the_last_value_as_stale` (the five bad replies of Task 6); `Plugging_or_unplugging_the_cable_makes_it_stale`; `A_busy_driver_is_skipped_and_retried_next_turn`; `A_receiver_failure_does_not_spoil_a_cable_reading`; `Each_reading_is_logged`. `AeroxMouseTests`: `[Theory] The_connection_follows_the_rule` (cable; none; receiver with no reading; wireless with a fresh answered reading; receiver again once stale). Host: `Set_mouse_sleep_saves_writes_version_4_and_reaches_the_mouse` (fake transport sees `[0,0x69,…]` then `[0,0x51,0]`; state `Mouse.SleepSent` true); `A_sleep_time_not_on_the_list_is_refused`; `Leave_as_the_mouse_is_can_be_chosen_again` (null → file back to its earlier version, no further `0x69`); `On_the_cable_the_sleep_time_is_saved_and_waits` (`SleepSent` false); `A_battery_change_raises_a_state`; `The_mouse_is_not_polled_while_lighting_is_blocked`.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~Aerox|FullyQualifiedName~DeviceHostEveryday"`. Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 4: mouse battery, sleep time and the mouse view`.

### Task 9: Hardware rescan when the cable arrives

**Files:**
- Modify: `src/DeadWare.Devices/OpenRgbSession.cs` (`IOpenRgbSession`, `OpenRgbSession`), `src/DeadWare.Devices/BoundedSession.cs`, `src/DeadWare.Devices/AeroxMouse.cs`, `src/DeadWare.Devices/DeviceHost.cs`, `tests/DeadWare.Devices.Tests/FakeOpenRgbSession.cs`, any fake in `BoundedSessionTests.cs`
- Test: `tests/DeadWare.Devices.Tests/DeviceHostEverydayTests.cs`, `tests/DeadWare.Devices.Tests/OpenRgbSessionTests.cs` (new or existing)

**Interfaces (Produces):**

```csharp
// IOpenRgbSession
void RequestHardwareRescan();
// OpenRgbSession
internal static readonly byte[] RescanPacket=[0x4F,0x52,0x47,0x42,0,0,0,0,0x8C,0,0,0,0,0,0,0];
internal static void SendRescan(string host,int port,TimeSpan connectLimit,TimeSpan drainLimit);
// AeroxMouse
public static readonly TimeSpan RescanGap=TimeSpan.FromSeconds(60);
public bool CableArrived(bool cableListed);   // true once per arrival of the cable while OpenRGB does not list it, and never within RescanGap of the last true
// DeviceHost
public static readonly TimeSpan RescanSettle=TimeSpan.FromSeconds(4);
```

**Behaviour (amendments 1 and 2):**
- `SendRescan`: a new `TcpClient` to `127.0.0.1:6744` (connect limit 1 s), write the 16 bytes, `Shutdown(SocketShutdown.Send)`, read and discard until the server closes or 2 s pass, close. Any failure is thrown to the caller, which logs it; it never touches the main client.
- In the host's slow work, when `mouse.CableArrived(service.CableKey is not null)`: log `mouse cable seen: asking OpenRGB to look for hardware`; `session.RequestHardwareRescan()` (failure logged, then nothing more this arrival); then a deliberate reconnect: `session.Abort()`, `sessionOk=false`, `service.Reset()`, and no connect attempt before `RescanSettle` has passed (a timestamp checked in `TickWork`'s connect branch, alongside the existing backoff). The existing connect path then re-reads the list as devices return. When the cable device is first listed afterwards, log `mouse cable listed`.
- The cable leaving sends nothing. OpenRGB is never restarted here.

- [ ] **Step 1: Write the failing tests:** `The_rescan_packet_is_the_sixteen_bytes` (against a local `TcpListener` on an ephemeral port: received bytes equal `RescanPacket`, the client half-closes, and `SendRescan` returns inside 3 s whether the server closes at once or pushes 16 bytes first); `A_refused_connection_throws_inside_the_limit`; `The_cable_arriving_asks_once_and_reconnects` (fake session counts `RequestHardwareRescan` 1, `Aborts` 1, no `Connect` before 4 s, one after; caches forgotten: a device whose colour did not change is written again); `A_second_arrival_inside_a_minute_does_not_ask_again`; `An_arrival_OpenRGB_already_lists_does_not_ask`; `The_cable_leaving_does_not_ask`; `A_failed_request_is_logged_and_the_session_is_left_alone`; `BoundedSession_bounds_the_request`.
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Devices.Tests -c Release --filter "FullyQualifiedName~DeviceHostEveryday|FullyQualifiedName~OpenRgbSession|FullyQualifiedName~BoundedSession"`. Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass.
- [ ] **Step 5: Commit** `Lighting 4: one hardware rescan when the mouse cable arrives`.

### Task 10: Words for the window (Core) and the tray line

**Files:**
- Create: `src/DeadWare.Core/EverydayTexts.cs`, `src/DeadWare.Core/MouseTexts.cs`, `src/DeadWare.Core/TrayLights.cs`
- Modify: `src/DeadWare.WinUI/Tray.cs`, `src/DeadWare.WinUI/App.xaml.cs:88`
- Test: `tests/DeadWare.Tests/EverydayTextsTests.cs`, `tests/DeadWare.Tests/MouseTextsTests.cs`, `tests/DeadWare.Tests/TrayLightsTests.cs` (new)

**Interfaces (Consumes):** Tasks 1, 2. **(Produces):**

```csharp
public static class EverydayTexts{
    public const string Heading="Everyday",Sub="ways to turn the lights off without opening this page",
        ShortcutLabel="Lights on/off shortcut",Change="Change",Cancel="Cancel",NoShortcut="No shortcut",Recording="Press the new keys…",
        ShortcutHint="Works anywhere in Windows. Same as the Lights on switch.",NoShortcutHint="No shortcut. Use the switch or the tray instead.",
        IdleLabel="Off when the PC sits unused",IdleHint="Lights come back the moment you touch the mouse or keyboard.",
        LockLabel="Off when the PC is locked",LockRow="Lights off at the lock screen",LockHint="Lights come back when you sign back in.",
        Waking="Waking up… lights return in a few seconds.",Locked="Lights are off because the PC is locked. They come back when you sign in.";
    public static string Idle(int minutes);   // "Lights are off because the PC has sat unused for 10 minutes. They come back when you touch the mouse or keyboard." ("1 minute" for 1)
    public static string? ReasonLine(string? reason,int idleMinutes);
    public static bool Lit(HostState state)=>LightsOff.Effective(state.Settings,state.LightsOffReason);
    public static Shortcut? FromKeys(bool ctrl,bool alt,bool shift,bool win,int key,out string? error);   // null with InvalidText for an invalid or unknown key
}
public static class MouseTexts{
    public const string Heading="Mouse · SteelSeries Aerox 9",SharedColours="The same colours are used on the cable and wireless.",
        Battery="Battery",Sleep="Sleep",SleepRow="Mouse sleeps after",Leave="Leave as the mouse is",Never="Never",
        Note="While the mouse lies still its lights hold steady, so it can fall asleep and save battery. Moving it brings the effect back.";
    public static string SubLine(MouseView m);public static string BatteryLine(MouseView m);public static string Hint(MouseView m);
    public static string SleepChoice(int? minutes);public static string SleepStatus(MouseView m);public static bool ShowNote(MouseView m);public static bool Dimmed(MouseView m);
}
public static class TrayLights{
    public const string Off="Turn lights off",On="Turn lights on",Unavailable="Lights (not available)";
    public static (string Label,bool Enabled,bool Target) Line(bool lightingEnabled,HostState? state,string? clientProblem);
}
```

**Behaviour:**
- `MouseTexts` follows the spec's table exactly: `SubLine`: wireless `"Wireless · Battery 85%"`; cable `"On its cable · Battery 60% · Charging"` (`· Charging` only when charging; no battery part when unknown); receiver `"Receiver plugged in · mouse asleep or out of reach"`; none `"Not connected"`. `BatteryLine`: `"85%"`, `"85% · last known"` when stale, `"Battery unknown"` when null. `Hint`: wireless `"On battery. Read while you use the mouse."`; cable `"Charging. Read every 10 seconds on the cable."`, at 100% and charging `"Fully charged."`, not charging `"Not charging."`; receiver `"Last known reading. Move the mouse to refresh it."`; none `"Plug in the receiver or the cable."`. `SleepChoice`: `Leave`, `Never`, `"1 minute"`, `"N minutes"`. `SleepStatus`: null minutes `"DeadWare has not changed the mouse's own sleep time."`; sent `"Saved and sent to the mouse."`; cable `"Saved. It is sent over the wireless receiver, so it reaches the mouse next time you use it wireless."`; otherwise `"Saved. Waiting for the mouse."`. `ShowNote`: minutes above 0. `Dimmed`: connection none.
- `TrayLights.Line`: `(Unavailable,false,false)` when lighting is not enabled, `state` is null, `clientProblem` is not null, or `state.Status` is not `Running`; else `(Off,true,false)` when `state.Settings.LightsOn`, else `(On,true,true)`. The label follows the saved switch, not the effective value.
- `Tray`: `public Tray(Action open, Action quit, Func<(string Label, bool Enabled, bool Target)> lights, Action<bool> setLights)`; `private const uint LightsCommand = 3;`; menu order Show dashboard, the lights line (`MF_GRAYED` 0x1 when not enabled), separator, Quit DeadWare; a click calls `setLights(target)` with the target read when the menu was built. `App.xaml.cs` passes `() => TrayLights.Line(session.Settings.LightingEnabled, session.Lighting?.State, session.Lighting?.Problem)` and `on => _ = session.Lighting?.SendAsync(new SetLightsOnRequest(on), CancellationToken.None)`.

- [ ] **Step 1: Write the failing tests:** one `[Theory]` row per line of the spec's mouse table and each status sentence (`MouseTextsTests`); `EverydayTextsTests`: `Idle_names_the_minutes` (10 and 1), `[Theory] The_reason_line_follows_the_reason` (null → null; idle; locked; waking), `Lit_is_false_with_a_reason_or_the_switch_off`, `[Theory] Keys_become_a_shortcut_or_the_invalid_line` (Ctrl+Alt+K ok; Shift+K invalid; Ctrl+0x5B invalid); `TrayLightsTests`: `[Theory]` over the six cases above, plus `The_label_follows_the_switch_during_an_idle_spell` (LightsOn true, reason idle → `Off`, target false).
- [ ] **Step 2: Run** `dotnet test tests/DeadWare.Tests -c Release --filter "FullyQualifiedName~EverydayTexts|FullyQualifiedName~MouseTexts|FullyQualifiedName~TrayLights"`. Expected: fail.
- [ ] **Step 3: Implement** the three classes and the tray change.
- [ ] **Step 4: Run** the build and the three test projects. Expected: all pass. **Hand trace** in the report, with file:line: the menu is built with the three lines in order; the greyed flag; the click sends the target captured at build time; nothing runs when `Lighting` is null.
- [ ] **Step 5: Commit** `Lighting 4: window wording in Core and the tray lights line`.

### Task 11: The Everyday card, the reason line and the mouse card

**Files:**
- Create: `src/DeadWare.WinUI/EverydayCard.cs`
- Modify: `src/DeadWare.WinUI/LightingPage.cs` (constructor `:256-260` and `:329`, `ShowStatus` `:609-635`, `RunningText` `:638-645`, `ShowsFrames` `:447-448`, `ShowFrame` `:439`, `Render` `:597`, `ShowDesk` `:701`, the device card's heading for the mouse), `src/DeadWare.WinUI/DeviceSections.cs` (two sections, `Sections` list `:112`, `Update` `:127`)
- Test: none new (no window test project); hand trace.

**Interfaces (Consumes):** Tasks 1, 2, 10.

**Behaviour (spec "What the owner sees", amendments 8 and 9; the mockup is the picture):**
- `EverydayCard` (`internal sealed class`, built in code like `DeviceSections`): constructor `(Action<HostMessage, Action<Reply>?> send)`; `public UIElement Root`; `public void Update(HostState? state, bool connected)`. A `Pages.Card` with `EverydayTexts.Heading`, the muted sub-line, and three columns in a `Grid` that become three rows below 900 px wide. Column 1: the keys box (a focusable `Border` with a `TextBlock` showing `Shortcuts.Text(...)` or `Recording`), **Change**/**Cancel** and **No shortcut** buttons, the hint, and a caution line. While recording, the box handles `KeyDown` (Esc cancels; modifier-only keys are ignored; otherwise `EverydayTexts.FromKeys(...)` from `InputKeyboardSource.GetKeyStateForCurrentThread` for the four modifiers and `e.Key` as the key number) and sends `SetEverydayRequest` with a clone of the state's `Everyday` with the new shortcut; a failed reply's explained text goes on the caution line; `state.ShortcutProblem` and `state.EverydayProblem` show there too. Column 2: `"After"`, a `ComboBox` of `Everydays.IdleChoices`, `"minutes"`, a `ToggleSwitch`, the hint. Column 3: the row text, a `ToggleSwitch`, the hint. Every change sends the whole `EverydaySettings`. Controls are disabled when not connected; values come only from the state (no local copy that could drift), with the same "ignore the echo of my own change" handling the page's Lights on switch uses (`ApplyLightsOn`, `:1414-1420`).
- `LightingPage`: add `everyday.Root` to `onPanel` after `deskArea` (`:329`), in both picture and plain mode; hidden with the rest while lighting is off in Settings. A new caution `TextBlock reason` goes first in `onPanel` (before `alert`, `:256`) and is set in `ShowStatus` from `EverydayTexts.ReasonLine(state.LightsOffReason, state.Settings.Everyday.IdleMinutes)`; it does not count as an alert, so the running text still shows. Every read of `settings.LightsOn` used for the picture, frames and running text (`:439`, `:448`, `:597`, `:642`, `:701`) uses `EverydayTexts.Lit(state)`; where Core methods take settings (`ShowsLights`, `Animating`), pass `state.Settings.WithLightsOff()` when not lit. The header switch (`ApplyLightsOn`) keeps showing the saved `Settings.LightsOn`.
- `DeviceSections`: `batterySection` (label `MouseTexts.Battery`; a `ProgressBar` 0-100, greyed when stale or unknown; `BatteryLine`; muted `Hint`) and `sleepSection` (label `MouseTexts.Sleep`; row `SleepRow` with a `ComboBox` of `Leave`, then `MouseSleep.Choices` through `SleepChoice`; muted `SleepStatus`; the note box when `ShowNote`). Both shown only when `next.Mouse is not null`, placed first in `Sections` (so after the zone rows, before Brightness). A choice sends `SetMouseSleepRequest`. For the mouse the card heading is `MouseTexts.Heading` and its sub-line `MouseTexts.SubLine`; the muted line `SharedColours` sits under the zone rows; when `Dimmed`, the zone rows and both sections are at 0.38 opacity but stay usable (settings are saved for when it returns).

- [ ] **Step 1: Implement** `EverydayCard` and the page changes.
- [ ] **Step 2: Implement** the two mouse sections and the mouse heading.
- [ ] **Step 3: Run** the build and the three test projects. Expected: all pass, no new warnings.
- [ ] **Step 4: Hand trace** in the report, with file:line for each: (a) every string on the card comes from `EverydayTexts`/`Shortcuts`/`MouseTexts` (grep shows no literal copy of those sentences in WinUI); (b) recording: Esc, modifier-only, invalid, valid, refused reply; (c) the five `LightsOn` reads now use `Lit`; (d) the header switch still reads the saved value; (e) the reason line is not counted in `alerts`; (f) the card is hidden when lighting is disabled and disabled when not connected; (g) the two sections show only for a view with `Mouse`; (h) no control keeps its own copy of a setting.
- [ ] **Step 5: Commit** `Lighting 4: Everyday card, reason line, mouse battery and sleep sections`.

### Task 12: Guide, release notes, version

**Files:**
- Modify: `Directory.Build.props` (`0.22.0`), `docs/user-guide.md` (`## Lighting`, from line 233; tray text at 99-101), `docs/superpowers/specs/2026-11-15-lighting-design.md` (one dated note at line 240 that the product IDs are receiver `1858`, cable `185A`, and at line 196 that battery travels in the state, not a `BatteryChanged` event)
- Create: `docs/releases/v0.22.0.md` (form of `docs/releases/v0.21.0.md`)

**Behaviour:** plain words for the owner; UI names in bold exactly as on screen. The guide gains `### Everyday: shortcut, idle and lock` and `### The mouse: battery and sleep`, each with bold-led paragraphs, covering: the three ways to flip the one switch; idle and lock are temporary and the yellow line says why; the front fans may stay lit; one card for cable and wireless and where the colours came from; battery is read while the mouse is used; what the sleep time does and that lights hold steady while the mouse lies still; plugging in the cable makes all lights blink for a few seconds; the PC's sleep and wake; **Going back** to 0.21.0 (settings from this step make the file version 4, which 0.21.0 reads but will not save over; the cable mouse's old colours are still there). Release notes: `## 0.22.0 — Mouse and everyday`, bullets `- **Bold lead.** text`, ending with "Nothing changes until you use it" and "Going back to 0.21.0". No `CHANGELOG.md` entry (amendment 10).

- [ ] **Step 1: Write** the guide sections and the release notes; bump the version; add the two dated notes to the parent design.
- [ ] **Step 2: Run** the build and the three test projects (the version is read by tests and the updater). Expected: all pass.
- [ ] **Step 3: Check** every UI name in the docs against `EverydayTexts`, `MouseTexts`, `TrayLights` by grep; list the matches in the report.
- [ ] **Step 4: Commit** `docs: 0.22.0 guide and release notes for the mouse and everyday`.

## After the tasks

1. Whole-branch review (`whole-branch-review`, `base` `2141e00`, pinned `head`, the spec path, `controllerModel`), run from the top-model design session, plus a second reviewer for the changed test files and the guide. A verdict of INCOMPLETE is not a pass. After a fix wave, review the fix range only.
2. Push and open a draft PR to `main`; CI proves the helper still publishes as a single file with HidSharp folded in.
3. The owner's hardware check: the seven items in the spec (six in "Hardware check" and amendment 3), each approved one at a time, through owner-run scripts in the pattern of 3b's `hw-launch.ps1`, `hw-snap.ps1`, `hw-restore.ps1`. Report per item what the log confirms and what it does not.
4. Arc cost: `uv run python scripts/arc_cost.py --since <start> --until <end>` in claude-memory-compiler; keep the output with the arc's working files.
5. No tag without asking the owner.

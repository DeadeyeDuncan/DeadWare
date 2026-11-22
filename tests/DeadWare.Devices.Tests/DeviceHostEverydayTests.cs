using System.Runtime.CompilerServices;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// The harness of DeviceHostTests, reachable from DeviceHostEverydayTests, which runs one of these per test.
public sealed partial class DeviceHostTests
{
    internal string LightingPath=>lightingPath;
    internal Clock Time=>time;
    internal FakeOpenRgbSession Session=>session;
    internal DeviceHost BuildHost(IEverydaySignals? signals,IInputClock? input,bool startup=false,string? startupFailure=null,IKeyPressSource? keys=null,LightingStore? store=null)
        =>Build(startup:startup,startupFailure:startupFailure,keys:keys,signals:signals,input:input,store:store);
    internal void StartHost(DeviceHost h)=>Start(h);
    internal Task ElapseBy(TimeSpan delay)=>Elapse(delay);
    internal Task<TimeSpan> StepUntil(Func<bool> cond,TimeSpan? tick=null,[CallerArgumentExpression(nameof(cond))] string what="")=>AdvanceUntil(cond,tick:tick,what:what);
    internal Task StepFor(TimeSpan span,TimeSpan tick)=>RunFor(span,tick);
    internal static Task<Reply> SendTo(DeviceHost h,HostMessage m)=>Send(h,m);
    internal Rgb[]? Last(int index)=>session.Leds.LastOrDefault(l=>l.Index==index).Colors;
    internal int ApexIndex=>Apex;
    internal string[] Lines(string contains)=>LogLines(contains);
    internal void BlockLighting(){lock(conflicts)conflicts.Add(DeadLight);}
    internal void NameKeys()=>NameApexKeys();
    internal static DeviceLayer RippleLayer()=>Ripple();
    internal static string ApexDevice=>ApexKey;
    internal static void TapA(FakeKeys k)=>Tap(k,ScanA);
    internal List<(DateTimeOffset At,Dictionary<string,string> Colors)> PreviewsOf(DeviceHost h)=>Previews(h);
    internal int Scans=>Volatile.Read(ref scans);
    // Fix round 1: parks the host loop in its next conflict scan (slow work) until released, so presses pile up for one loop turn.
    Hold? everydayHold;
    internal void ParkScans(){everydayHold=NewHold();Volatile.Write(ref scanHold,everydayHold);}
    internal bool ScansParked=>everydayHold?.Parked==true;
    internal void ReleaseScans(){Volatile.Write(ref scanHold,null);everydayHold?.Release();}
}

// Lighting 4, Task 4: effective lights in the host. The shortcut flips the saved switch; idle and lock turn the lights off for a while and
// leave the switch alone; setEveryday saves the rules and registers the shortcut. All against fakes.
public sealed class DeviceHostEverydayTests:IAsyncLifetime
{
    readonly DeviceHostTests t=new();
    readonly FakeSignals signals=new();
    readonly FakeInput input=new();
    static readonly Rgb SteelBlue=new(0x46,0x82,0xB4);
    static readonly Shortcut CtrlShiftK=new(Shortcuts.Ctrl|Shortcuts.Shift,0x4B);
    public Task InitializeAsync()=>t.InitializeAsync();
    public Task DisposeAsync()=>t.DisposeAsync();

    void SaveFirst(Action<EverydaySettings> rules){var s=new LightingSettings();rules(s.Everyday);new LightingStore(t.LightingPath).Save(s);}
    string? FileText()=>File.Exists(t.LightingPath)?File.ReadAllText(t.LightingPath):null;
    LightingSettings Saved()=>new LightingStore(t.LightingPath).Load().Settings;
    async Task<DeviceHost> Running(IKeyPressSource? keys=null,LightingStore? store=null)
    {
        var h=t.BuildHost(signals,input,keys:keys,store:store);t.StartHost(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        return h;
    }
    bool All(int index,Rgb c)=>t.Last(index) is{Length:>0} l&&l.All(x=>x==c);
    async Task Idle(DeviceHost h,TimeSpan? tick=null){input.Idle=60_000;await t.StepUntil(()=>h.State.LightsOffReason==LightsOff.Idle,tick:tick);}

    [Fact]
    public async Task A_press_turns_lights_off_and_saves_it()
    {
        var h=await Running();
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.Null(h.State.LightsOffReason);
        Assert.False(Saved().LightsOn);
        await t.StepUntil(()=>All(0,Rgb.Black));
    }

    [Fact]
    public async Task A_second_press_turns_them_on_again()
    {
        var h=await Running();
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        await t.StepUntil(()=>All(0,Rgb.Black));
        signals.Press();
        await Until(()=>h.State.Settings.LightsOn);
        Assert.True(Saved().LightsOn);
        await t.StepUntil(()=>All(0,SteelBlue));
    }

    [Fact]
    public async Task Three_presses_before_a_tick_compose()
    {
        var h=await Running();
        signals.Press();signals.Press();signals.Press();
        await Until(()=>t.Lines("shortcut pressed: ").Length==3);
        Assert.False(h.State.Settings.LightsOn);
        Assert.False(Saved().LightsOn);
    }

    [Fact]
    public async Task Idle_turns_lights_off_without_touching_the_switch()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await t.StepUntil(()=>All(0,SteelBlue));
        var file=FileText();
        input.Idle=59_999;
        await t.StepFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.Null(h.State.LightsOffReason);
        await Idle(h);
        Assert.True(h.State.Settings.LightsOn);
        await t.StepUntil(()=>All(0,Rgb.Black));
        Assert.Equal(file,FileText());
        Assert.True(Saved().LightsOn);
    }

    [Fact]
    public async Task Coming_back_from_idle_repaints()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        var lit=h.State.Devices.Select(v=>v.Mode).ToList();
        await Idle(h);
        await t.StepUntil(()=>All(0,Rgb.Black));
        var idle=h.State.Devices.Select(v=>v.Mode).ToList();
        Assert.NotEqual(lit,idle); // a hardware-mode device shows its lights-off mode while idle
        input.Idle=0;
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await t.StepUntil(()=>All(0,SteelBlue));
        Assert.All(h.State.Devices[0].Zones,z=>Assert.Equal("#4682B4",z.Color));
        Assert.Equal(lit,h.State.Devices.Select(v=>v.Mode));
        Assert.True((await DeviceHostTests.SendTo(h,new SetLightsOnRequest(false))).Ok);
        Assert.Equal(idle,h.State.Devices.Select(v=>v.Mode)); // the same as the switch off
    }

    // A change while idle is saved and shown in the state, but the paint that follows it is dark, as are a rescan's.
    [Fact]
    public async Task A_change_or_rescan_while_idle_paints_dark()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await Idle(h);
        await t.StepUntil(()=>All(0,Rgb.Black));
        await t.StepFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        t.Time.Advance(TimeSpan.FromMilliseconds(250)); // past every device's frame cap, short of the next tick, so the change's own paint is sent
        var from=t.Session.LedCount;
        Assert.True((await DeviceHostTests.SendTo(h,new SetAllColorsRequest("#FF0000"))).Ok);
        Assert.True((await DeviceHostTests.SendTo(h,new RescanRequest())).Ok);
        Assert.Equal("#FF0000",Saved().DefaultColor);
        t.Time.Advance(TimeSpan.FromMilliseconds(250)); // the rest of the way to the tick the loop was waiting on
        await t.StepFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.All(t.Session.Leds.Skip(from),w=>Assert.All(w.Colors,c=>Assert.Equal(Rgb.Black,c)));
    }

    [Fact]
    public async Task A_press_during_an_idle_spell_only_brings_lights_back()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await Idle(h);
        input.Idle=0;signals.Press(); // the same loop turn
        await Until(()=>t.Lines("shortcut pressed: back from idle").Length==1);
        Assert.True(h.State.Settings.LightsOn);
        Assert.Null(h.State.LightsOffReason);
        Assert.True(Saved().LightsOn);
        await t.StepUntil(()=>All(0,SteelBlue));
        signals.Press(); // the spell's end is used up: this one toggles
        await Until(()=>!h.State.Settings.LightsOn);
    }

    // Ruling P9: Ctrl or Alt held ends the spell a loop turn before the letter arrives.
    [Fact]
    public async Task A_press_soon_after_an_idle_spell_ends_only_brings_lights_back()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await Idle(h);
        input.Idle=0;
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await t.StepFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        signals.Press();
        await Until(()=>t.Lines("shortcut pressed: ").Length==1);
        Assert.Single(t.Lines("shortcut pressed: back from idle"));
        Assert.True(h.State.Settings.LightsOn);
    }

    [Fact]
    public async Task A_press_well_after_an_idle_spell_ends_toggles()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await Idle(h);
        input.Idle=0;
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await t.StepFor(TimeSpan.FromSeconds(3),DeviceHost.Tick);
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.Single(t.Lines("shortcut pressed: lights off"));
    }

    [Fact]
    public async Task Idle_is_not_read_while_its_rule_is_off()
    {
        var h=await Running();
        input.Idle=3_600_000;
        await t.StepFor(TimeSpan.FromSeconds(6),DeviceHost.Tick);
        Assert.Equal(0,input.IdleCalls);
        Assert.Null(h.State.LightsOffReason);
    }

    [Fact]
    public async Task Lock_turns_lights_off_only_with_its_rule_on()
    {
        var h=await Running();
        signals.Lock(true);
        await Until(()=>t.Lines(" locked").Length==1);
        Assert.Null(h.State.LightsOffReason);
        Assert.True((await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){LockOff=true}))).Ok);
        Assert.Equal(LightsOff.Locked,h.State.LightsOffReason);
        Assert.True(h.State.Settings.LightsOn);
        await t.StepUntil(()=>All(0,Rgb.Black));
    }

    [Fact]
    public async Task Unlock_brings_them_back()
    {
        SaveFirst(e=>e.LockOff=true);
        var h=await Running();
        signals.Lock(true);
        await Until(()=>h.State.LightsOffReason==LightsOff.Locked);
        await t.StepUntil(()=>All(0,Rgb.Black));
        signals.Lock(false);
        await Until(()=>h.State.LightsOffReason is null);
        await t.StepUntil(()=>All(0,SteelBlue));
        Assert.True(Saved().LightsOn);
    }

    [Fact]
    public async Task The_switch_off_shows_no_reason_while_locked()
    {
        SaveFirst(e=>e.LockOff=true);
        var h=await Running();
        signals.Lock(true);
        await Until(()=>h.State.LightsOffReason==LightsOff.Locked);
        Assert.True((await DeviceHostTests.SendTo(h,new SetLightsOnRequest(false))).Ok);
        Assert.Null(h.State.LightsOffReason);
        Assert.False(h.State.Settings.LightsOn);
    }

    [Fact]
    public async Task The_hook_is_removed_and_presses_cleared_while_idle()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        t.NameKeys();
        var keys=new DeviceHostTests.FakeKeys(t.Time);
        var h=await Running(keys);
        Assert.True((await DeviceHostTests.SendTo(h,new SetDeviceLayersRequest(DeviceHostTests.ApexDevice,[DeviceHostTests.RippleLayer()]))).Ok);
        Assert.True(keys.Installed);Assert.True(h.State.KeyHook);
        DeviceHostTests.TapA(keys);
        Assert.Single(keys.Gate!.Live());
        await Idle(h,DeviceHost.FastTick); // the press keeps the fast tick
        await Until(()=>keys.Stops>=1&&!h.State.KeyHook);
        Assert.False(keys.Installed);
        Assert.Empty(keys.Gate.Live());
        input.Idle=0;
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await Until(()=>keys.Installed&&h.State.KeyHook);
        Assert.Equal(2,keys.Starts);
    }

    [Fact]
    public async Task No_preview_frame_is_sent_while_idle()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await Idle(h);
        Assert.True((await DeviceHostTests.SendTo(h,new SetEffectRequest("Wave",null))).Ok);
        var seen=t.PreviewsOf(h);
        Assert.True((await DeviceHostTests.SendTo(h,new SubscribePreviewRequest(true))).Ok);
        await t.StepFor(TimeSpan.FromSeconds(1),DeviceHost.Tick); // the tick stays slow while idle, or this stalls
        Assert.False(t.Time.Waiting(DeviceHost.FastTick));
        lock(seen)Assert.Empty(seen);
        input.Idle=0;
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await t.StepUntil(()=>{lock(seen)return seen.Count>=1;},tick:DeviceHost.FastTick);
    }

    [Fact]
    public async Task A_refused_saved_shortcut_is_reported_and_the_helper_runs()
    {
        signals.Refuse=_=>false;
        var h=await Running();
        Assert.Equal(Shortcuts.TakenText(Shortcuts.Default),h.State.ShortcutProblem);
        Assert.Equal(HostStatus.Running,h.State.Status);
        Assert.Equal([Shortcuts.Default],signals.Shortcuts);
        Assert.Single(t.Lines("shortcut refused: Ctrl + Alt + L"));
    }

    [Fact]
    public async Task A_slow_answer_at_start_is_reported_and_tried_again_on_slow_work()
    {
        signals.Refuse=_=>null;
        var h=await Running();
        Assert.Equal(HostReplyCodes.Explain(HostReplyCodes.ShortcutSlow),h.State.ShortcutProblem);
        signals.Refuse=null;
        await t.StepUntil(()=>h.State.ShortcutProblem is null);
        Assert.Equal([Shortcuts.Default,Shortcuts.Default],signals.Shortcuts);
        Assert.Equal([DeviceHost.ShortcutWait,DeviceHost.RetryWait],signals.Waits); // the retry holds the lock only briefly
        Assert.Single(t.Lines("shortcut registered: Ctrl + Alt + L"));
    }

    [Fact]
    public async Task Set_everyday_saves_and_registers()
    {
        var h=await Running();
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK,IdleOff=true,IdleMinutes=5,LockOff=true}){Id=4});
        Assert.True(reply.Ok);Assert.Equal(4,reply.Id);
        Assert.Equal(CtrlShiftK,signals.Shortcuts[^1]);
        var e=Saved().Everyday;
        Assert.Equal((CtrlShiftK,true,5,true),(e.Shortcut,e.IdleOff,e.IdleMinutes,e.LockOff));
        Assert.Equal(CtrlShiftK,h.State.Settings.Everyday.Shortcut);
        var text=FileText()!;
        Assert.Contains("\"Everyday\"",text);
        Assert.Contains("\"Version\": 4",text);
    }

    [Fact]
    public async Task A_successful_set_clears_the_shortcut_problem()
    {
        signals.Refuse=s=>s==Shortcuts.Default?false:true;
        var h=await Running();
        Assert.NotNull(h.State.ShortcutProblem);
        Assert.True((await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK}))).Ok);
        Assert.Null(h.State.ShortcutProblem);
    }

    [Fact]
    public async Task A_taken_shortcut_is_refused_and_the_old_one_restored()
    {
        var h=await Running();
        signals.Refuse=s=>s==CtrlShiftK?false:true;
        var file=FileText();
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK,IdleOff=true}));
        Assert.False(reply.Ok);
        Assert.Equal(Shortcuts.TakenText(CtrlShiftK),reply.Error);
        Assert.Equal(file,FileText());
        Assert.False(h.State.Settings.Everyday.IdleOff);
        var set=signals.Shortcuts;
        Assert.Equal([CtrlShiftK,Shortcuts.Default],set.TakeLast(2));
    }

    [Fact]
    public async Task A_slow_answer_refuses_with_shortcut_slow()
    {
        var h=await Running();
        signals.Refuse=s=>s==CtrlShiftK?null:true;
        var file=FileText();
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK}));
        Assert.False(reply.Ok);
        Assert.Equal(HostReplyCodes.ShortcutSlow,reply.Error);
        Assert.Equal(file,FileText());
        Assert.Equal(Shortcuts.Default,h.State.Settings.Everyday.Shortcut);
        Assert.Equal([Shortcuts.Default,CtrlShiftK],signals.Shortcuts); // the watcher withdrew the unanswered request, so the old one is not asked for again
    }

    // Ruling P14: the old shortcut is registered again when the save fails after the new one was registered.
    [Fact]
    public async Task A_failed_save_puts_the_old_shortcut_back()
    {
        var h=await Running(store:new LightingStore(t.LightingPath,write:(_,_)=>throw new IOException("the disk is full")));
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK}));
        Assert.False(reply.Ok);
        Assert.Equal(HostReplyCodes.CouldntSave,reply.Error);
        Assert.Equal([CtrlShiftK,Shortcuts.Default],signals.Shortcuts.TakeLast(2));
        Assert.Equal(Shortcuts.Default,h.State.Settings.Everyday.Shortcut);
    }

    // Ruling P14: with the watcher unavailable a shortcut change is refused in its words, and the other rules are still saved.
    [Fact]
    public async Task A_watcher_problem_refuses_the_shortcut_and_saves_the_rest()
    {
        signals.Trouble="Shortcut and lock are unavailable: the window couldn't be made.";
        var h=await Running();
        Assert.Empty(signals.Shortcuts);
        Assert.Equal(signals.Trouble,h.State.EverydayProblem);
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK,IdleOff=true}));
        Assert.False(reply.Ok);
        Assert.Equal(signals.Trouble,reply.Error);
        Assert.Empty(signals.Shortcuts);
        Assert.True(Saved().Everyday.IdleOff);
        Assert.Equal(Shortcuts.Default,Saved().Everyday.Shortcut);
        Assert.True(h.State.Settings.Everyday.IdleOff);
    }

    // Fix round 1, I1: while the saved shortcut is taken, the same shortcut is only tried again; the other rules are saved and the reply is
    // ok. Once another program lets it go, the next try clears the problem.
    [Fact]
    public async Task The_same_shortcut_while_it_is_taken_still_saves_the_other_rules()
    {
        signals.Refuse=_=>false;
        var h=await Running();
        Assert.Equal(Shortcuts.TakenText(Shortcuts.Default),h.State.ShortcutProblem);
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){IdleOff=true}));
        Assert.True(reply.Ok);
        Assert.True(Saved().Everyday.IdleOff);
        Assert.Equal(Shortcuts.TakenText(Shortcuts.Default),h.State.ShortcutProblem);
        signals.Refuse=null;
        Assert.True((await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){IdleOff=true,LockOff=true}))).Ok);
        Assert.Null(h.State.ShortcutProblem);
        Assert.True(Saved().Everyday.LockOff);
        Assert.Equal([Shortcuts.Default,Shortcuts.Default,Shortcuts.Default],signals.Shortcuts);
    }

    [Fact]
    public async Task The_same_shortcut_while_unanswered_still_saves_the_other_rules()
    {
        signals.Refuse=_=>null;
        var h=await Running();
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){LockOff=true}));
        Assert.True(reply.Ok);
        Assert.True(Saved().Everyday.LockOff);
        Assert.Equal(HostReplyCodes.Explain(HostReplyCodes.ShortcutSlow),h.State.ShortcutProblem);
    }

    // Fix round 1, M1: when the old shortcut is refused too after a new one was refused, the state says so.
    [Fact]
    public async Task An_old_shortcut_refused_again_is_reported()
    {
        var h=await Running();
        Assert.Null(h.State.ShortcutProblem);
        signals.Refuse=_=>false;
        var states=new List<HostState>();
        h.StateChanged+=s=>{lock(states)states.Add(s);};
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK}));
        Assert.Equal(Shortcuts.TakenText(CtrlShiftK),reply.Error);
        Assert.Equal(Shortcuts.TakenText(Shortcuts.Default),h.State.ShortcutProblem);
        lock(states)Assert.Equal(Shortcuts.TakenText(Shortcuts.Default),states[^1].ShortcutProblem); // raised, so the card shows it
    }

    [Fact]
    public async Task An_old_shortcut_unanswered_after_a_failed_save_is_reported()
    {
        var h=await Running(store:new LightingStore(t.LightingPath,write:(_,_)=>throw new IOException("the disk is full")));
        signals.Refuse=s=>s==CtrlShiftK?true:null;
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK}));
        Assert.Equal(HostReplyCodes.CouldntSave,reply.Error);
        Assert.Equal(HostReplyCodes.Explain(HostReplyCodes.ShortcutSlow),h.State.ShortcutProblem);
    }

    // Fix round 1, M5: two presses taken in one loop turn during an idle spell: the first only brings the lights back, the second turns the
    // switch off. The loop is parked in its slow work while both arrive.
    [Fact]
    public async Task Two_presses_in_one_turn_during_an_idle_spell_bring_back_then_turn_off()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;});
        var h=await Running();
        await Idle(h);
        t.ParkScans();
        await t.StepUntil(()=>t.ScansParked);
        signals.Press();signals.Press();
        Assert.True(h.State.Settings.LightsOn); // the loop is parked, so neither press is taken yet
        t.ReleaseScans();
        await Until(()=>t.Lines("shortcut pressed: ").Length==2);
        var pressed=t.Lines("shortcut pressed: ").Select(l=>l[l.IndexOf("shortcut pressed: ",StringComparison.Ordinal)..].TrimEnd('\r')).ToArray();
        Assert.Equal(["shortcut pressed: back from idle","shortcut pressed: lights off"],pressed);
        Assert.False(h.State.Settings.LightsOn);
        Assert.False(Saved().LightsOn);
    }

    [Fact]
    public async Task Bad_idle_minutes_are_refused()
    {
        var h=await Running();
        var file=FileText();
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){IdleOff=true,IdleMinutes=7}));
        Assert.False(reply.Ok);Assert.Equal(HostReplyCodes.BadEveryday,reply.Error);
        var none=await DeviceHostTests.SendTo(h,new SetEverydayRequest(null!));
        Assert.False(none.Ok);Assert.Equal(HostReplyCodes.BadEveryday,none.Error);
        Assert.Equal(file,FileText());
        Assert.Equal(10,h.State.Settings.Everyday.IdleMinutes);
    }

    [Fact]
    public async Task A_press_works_while_OpenRGB_is_not_running()
    {
        t.BlockLighting();
        var h=t.BuildHost(signals,input);t.StartHost(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.False(Saved().LightsOn);
    }

    // Amendment 5, ruling P10: registered before the start-up delay; a press during it is applied when the loop starts.
    [Fact]
    public async Task A_press_during_the_start_up_delay_is_applied_when_the_loop_starts()
    {
        var h=t.BuildHost(signals,input,startup:true);t.StartHost(h);
        await Until(()=>signals.Shortcuts.Count==1);
        signals.Press();
        await Task.Delay(100);
        Assert.True(h.State.Settings.LightsOn);
        Assert.Equal(0,t.Scans);
        await t.ElapseBy(DeviceHost.StartupDelay);
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.False(Saved().LightsOn);
    }

    // Ruling P10: after a start-up failure the loop never runs, and a press still flips and saves the switch.
    [Fact]
    public async Task A_press_after_a_start_up_failure_flips_and_saves_the_switch()
    {
        var h=t.BuildHost(signals,input,startupFailure:"The folder isn't protected.");t.StartHost(h);
        await Until(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Equal([Shortcuts.Default],signals.Shortcuts);
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.False(Saved().LightsOn);
        Assert.Equal(HostStatus.OpenRgbFailed,h.State.Status);
    }

    [Fact]
    public async Task Each_event_leaves_its_log_line()
    {
        SaveFirst(e=>{e.IdleOff=true;e.IdleMinutes=1;e.LockOff=true;});
        var h=await Running();
        await Idle(h);
        input.Idle=0;
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        signals.Lock(true);
        await Until(()=>h.State.LightsOffReason==LightsOff.Locked);
        signals.Lock(false);
        await Until(()=>h.State.LightsOffReason is null);
        await t.StepFor(TimeSpan.FromSeconds(3),DeviceHost.Tick); // past the idle spell's end, so the press toggles
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        signals.Press();
        await Until(()=>h.State.Settings.LightsOn);
        Assert.Single(t.Lines("shortcut registered: Ctrl + Alt + L"));
        Assert.Single(t.Lines("idle spell started (1 min)"));
        Assert.Single(t.Lines("idle spell ended"));
        Assert.Single(t.Lines(" locked"));
        Assert.Single(t.Lines(" unlocked"));
        Assert.Single(t.Lines("shortcut pressed: lights off"));
        Assert.Single(t.Lines("shortcut pressed: lights on"));
    }

    [Fact]
    public async Task Without_signals_nothing_changes()
    {
        var h=t.BuildHost(null,null);t.StartHost(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Null(h.State.LightsOffReason);
        Assert.Null(h.State.ShortcutProblem);
        Assert.Null(h.State.EverydayProblem);
        Assert.Empty(t.Lines("shortcut"));
        Assert.True((await DeviceHostTests.SendTo(h,new SetLightsOnRequest(false))).Ok);
        Assert.Null(h.State.LightsOffReason);
        await t.StepUntil(()=>All(0,Rgb.Black));
        // setEveryday still saves its rules; a shortcut change has no watcher to register it with, so it is refused
        Assert.True((await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){IdleOff=true}))).Ok);
        Assert.True(Saved().Everyday.IdleOff);
        var reply=await DeviceHostTests.SendTo(h,new SetEverydayRequest(new(){Shortcut=CtrlShiftK,IdleOff=true}));
        Assert.False(reply.Ok);
        Assert.Equal(Shortcuts.Default,Saved().Everyday.Shortcut);
    }
}

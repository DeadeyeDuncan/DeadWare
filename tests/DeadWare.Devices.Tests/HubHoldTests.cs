using DeadWare.Core;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5, Task 9: when the hub may be held. One row per rule, in order; each row also breaks the rules after it, so the first failing
// condition must give the reason.
public sealed class HubHoldTests
{
    static readonly LightingConflict ICue=new(ConflictKind.ICue,"iCUE is running.",true),
        Service=new(ConflictKind.CorsairService,"A Corsair device service is running.",true),
        Legacy=new(ConflictKind.LegacyCorsairService,"The Corsair service is running.",false),
        DeadLight=new(ConflictKind.DeadLight,"DeadLight is running.",true),
        Foreign=new(ConflictKind.ForeignOpenRgb,"Another OpenRGB is running.",true);

    // All well: mode driven, lighting running and connected once, nothing found, nothing listed.
    static HubFacts Well=>new("driven",false,false,true,true,HostStatus.Running,[],false,false,false);

    static HubFacts Facts(string row)=>row switch
    {
        "openrgb"=>Well with{Mode="openrgb",Stopping=true,Quiet=true,Conflicts=[ICue]},
        "stopped"=>Well with{Mode="stopped",Stopping=true,Conflicts=[ICue]},
        "stopping"=>Well with{Stopping=true,Quiet=true,Conflicts=[ICue]},
        "quiet"=>Well with{Quiet=true,Conflicts=[ICue],ScanFailed=true},
        "icue"=>Well with{Conflicts=[ICue],ScanFailed=true,EngineActive=false},
        "icue with deadlight"=>Well with{Conflicts=[DeadLight,ICue]},
        "service"=>Well with{Conflicts=[Foreign,Service,DeadLight]},
        "legacy service"=>Well with{Conflicts=[Foreign,Legacy]},
        "deadlight"=>Well with{Conflicts=[Foreign,DeadLight],ScanFailed=true},
        "other openrgb"=>Well with{Conflicts=[Foreign],ScanFailed=true},
        "scan failed"=>Well with{ScanFailed=true,ConnectedOnce=false,LaunchedWithHub=true},
        "engine not active"=>Well with{EngineActive=false,ConnectedOnce=false},
        "blocked"=>Well with{Status=HostStatus.Blocked,ConnectedOnce=false},
        "openrgb missing"=>Well with{Status=HostStatus.OpenRgbMissing,LaunchedWithHub=true},
        "openrgb failed"=>Well with{Status=HostStatus.OpenRgbFailed,OpenRgbListsHub=true},
        "never connected"=>Well with{ConnectedOnce=false,Status=HostStatus.Starting,LaunchedWithHub=true,OpenRgbListsHub=true},
        "launched with the hub, nothing listed"=>Well with{LaunchedWithHub=true},
        "launched with the hub, restart spent"=>Well with{LaunchedWithHub=true,RestartSpent=true},
        "listed"=>Well with{OpenRgbListsHub=true},
        "listed, restart spent"=>Well with{OpenRgbListsHub=true,RestartSpent=true},
        "waiting"=>Well with{Status=HostStatus.Waiting},
        "starting"=>Well with{Status=HostStatus.Starting},
        "running"=>Well,
        "running, restart spent"=>Well with{RestartSpent=true},
        _=>throw new ArgumentException(row),
    };

    [Theory]
    [InlineData("openrgb","off",null)]
    [InlineData("stopped","off",null)]
    [InlineData("stopping","noLighting",null)]
    [InlineData("quiet","waking",null)]
    [InlineData("icue","icue",null)]
    [InlineData("icue with deadlight","icue",null)]
    [InlineData("service","service",null)]
    [InlineData("legacy service","service",null)]
    [InlineData("deadlight","deadlight",null)]
    [InlineData("other openrgb","otherOpenRgb",null)]
    [InlineData("scan failed","noLighting",null)]
    [InlineData("engine not active","noLighting",null)]
    [InlineData("blocked","noLighting",null)]
    [InlineData("openrgb missing","noLighting",null)]
    [InlineData("openrgb failed","noLighting",null)]
    [InlineData("never connected","starting",null)]
    [InlineData("launched with the hub, nothing listed","openRgbHasIt",null)]
    [InlineData("launched with the hub, restart spent","openRgbHasIt","restartSpent")]
    [InlineData("listed","openRgbHasIt",null)]
    [InlineData("listed, restart spent","openRgbHasIt","restartSpent")]
    [InlineData("waiting",null,null)]
    [InlineData("starting",null,null)]
    [InlineData("running",null,null)]
    [InlineData("running, restart spent",null,null)]
    public void The_first_failing_condition_gives_the_reason(string row,string? reason,string? detail)
        =>Assert.Equal((reason,detail),HubHold.Why(Facts(row)));
}

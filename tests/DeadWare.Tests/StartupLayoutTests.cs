using System.Xml.Linq;
using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;

public class StartupLayoutTests
{
    [Fact] public void BatteryHistoryWindowAndRecordingChoiceSurviveSettingsRoundtrip()
    {
        var settings=SettingsCodec.Read("""{"OpenWindows":["batteryhistory","widget"],"BatteryRecording":false}""").Settings;
        var roundtrip=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Contains("batteryhistory",roundtrip.OpenWindows);
        Assert.False(roundtrip.BatteryRecording);
        Assert.True(new UserSettings().BatteryRecording);
    }
    [Fact] public void OldPlacementHistoryDoesNotReopenClosedWindows()
    {
        var settings=SettingsCodec.Read("""{"WindowPlacementFormat":"physical-pixels-v1","Windows":{"cpu":{"Left":10,"Top":10,"Width":600,"Height":400}}}""").Settings;
        Assert.Empty(settings.OpenWindows);Assert.True(settings.RestoreOpenWindows);
    }
    [Fact] public void OpenWindowsRoundtripFiltersUnknownNullAndDuplicateIds()
    {
        var read=SettingsCodec.Read("""{"OpenWindows":["cpu","cpu",null,"arbitrary","widget","GPU","network"],"RestoreOpenWindows":false}""");
        var roundtrip=SettingsCodec.Read(SettingsCodec.Write(read.Settings)).Settings;
        Assert.Equal(new[]{"cpu","widget","network"},roundtrip.OpenWindows);
        Assert.False(roundtrip.RestoreOpenWindows);
        Assert.Empty(SettingsCodec.Read("""{"OpenWindows":null}""").Settings.OpenWindows);
    }
    [Fact] public void ExplicitCloseIsForgottenButMainShutdownKeepsRemainingWindows()
    {
        var state=new DetachedWindowState(["cpu","widget"]);
        state.Close("cpu");state.Open("network");state.Open("network");state.Open("unknown");
        state.BeginShutdown();state.Close("widget");state.Close("network");
        Assert.Equal(new[]{"widget","network"},state.Remembered);
        state.Open("gpu");Assert.Equal(new[]{"widget","network"},state.Remembered);
    }
    [Fact] public async Task FinalSnapshotCannotBeOverwrittenByAnOlderQueuedSave()
    {
        string directory=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(directory,"settings.json");
        try
        {
            var writer=new SettingsSnapshotWriter(path);
            writer.Save(1,"old");writer.Save(3,"final");
            await Task.Run(()=>writer.Save(2,"queued before shutdown"));
            Assert.Equal("final",File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    private const string Sid="S-1-5-21-123-456-789-1001";
    [Fact] public void StartupTaskUsesInteractiveCurrentUserAndEscapesExecutablePath()
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps & Tools\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root=xml.Root!;
        Assert.Equal(Sid,root.Element(n+"Principals")!.Element(n+"Principal")!.Element(n+"UserId")!.Value);
        Assert.Equal("InteractiveToken",root.Descendants(n+"LogonType").Single().Value);
        Assert.Equal("HighestAvailable",root.Descendants(n+"RunLevel").Single().Value);
        Assert.Equal(Sid,root.Descendants(n+"LogonTrigger").Single().Element(n+"UserId")!.Value);
        Assert.Equal(@"C:\Apps & Tools\DeadWare.exe",root.Descendants(n+"Command").Single().Value);
        Assert.Equal("--startup",root.Descendants(n+"Arguments").Single().Value);
        Assert.Equal("IgnoreNew",root.Descendants(n+"MultipleInstancesPolicy").Single().Value);
        Assert.Equal("PT0S",root.Element(n+"Settings")!.Element(n+"ExecutionTimeLimit")!.Value);
        Assert.Equal("false",root.Descendants(n+"DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false",root.Descendants(n+"StopIfGoingOnBatteries").Single().Value);
    }
    [Fact] public void UnownedOrDifferentUserTaskMustNotBeReplacedOrDeleted()
    {
        string xml=StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe");
        Assert.True(StartupTaskPolicy.IsOwned(xml,Sid));
        Assert.False(StartupTaskPolicy.IsOwned(xml,"S-1-5-21-123-456-789-1002"));
        Assert.False(StartupTaskPolicy.IsOwned(xml.Replace(StartupTaskPolicy.Marker,"Some other task"),Sid));
        Assert.False(StartupTaskPolicy.IsOwned("not XML",Sid));
        Assert.Throws<InvalidOperationException>(()=>StartupTaskPolicy.RequireSameUser(Sid,"S-1-5-21-123-456-789-1002"));
    }
    [Fact] public void MovedExecutableAndChangedSchedulerPolicyRequireRepair()
    {
        string xml=StartupTaskPolicy.CreateXml(Sid,@"C:\Old\DeadWare.exe");
        Assert.True(StartupTaskPolicy.IsCurrent(xml,Sid,@"C:\Old\DeadWare.exe"));
        Assert.False(StartupTaskPolicy.IsCurrent(xml,Sid,@"D:\New\DeadWare.exe"));
        Assert.False(StartupTaskPolicy.IsCurrent(xml.Replace("PT0S","PT72H"),Sid,@"C:\Old\DeadWare.exe"));
        Assert.False(StartupTaskPolicy.IsCurrent(xml.Replace("HighestAvailable","LeastPrivilege"),Sid,@"C:\Old\DeadWare.exe"));
    }
    [Fact] public void TriggerTimeLimitsOrExpiryCannotSilentlyStopStartup()
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        var trigger=xml.Descendants(n+"LogonTrigger").Single();
        trigger.Element(n+"ExecutionTimeLimit")!.Value="PT72H";
        Assert.False(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe"));
        trigger.Element(n+"ExecutionTimeLimit")!.Value="PT0S";
        trigger.Add(new XElement(n+"EndBoundary","2020-01-01T00:00:00"));
        Assert.False(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe"));
    }
    [Theory]
    [InlineData("<Interval>PT30M</Interval><Duration>PT1H</Duration><StopAtDurationEnd>true</StopAtDurationEnd>")]
    [InlineData("<Interval>PT30M</Interval><StopAtDurationEnd>false</StopAtDurationEnd>")]
    [InlineData("<Duration>PT1H</Duration><StopAtDurationEnd>true</StopAtDurationEnd>")]
    public void RepeatingLogonTriggerRequiresRepair(string repetition)
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(n+"LogonTrigger").Single().AddFirst(XElement.Parse($"<Repetition xmlns=\"{n}\">{repetition}</Repetition>"));
        Assert.False(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe"));
    }
    [Fact] public void InactiveRepetitionDefaultsDoNotRequireRepair()
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(n+"LogonTrigger").Single().AddFirst(new XElement(n+"Repetition",new XElement(n+"StopAtDurationEnd","false")));
        Assert.True(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe"));
    }
    [Theory]
    [InlineData(Sid,true)]
    [InlineData("S-1-5-21-123-456-789-1002",false)]
    [InlineData(null,false)]
    public void NamedLogonTriggerMustResolveToExpectedSid(string? resolvedSid,bool expected)
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(n+"LogonTrigger").Single().Element(n+"UserId")!.Value=@"MACHINE\alice";
        string? Resolve(string name)=>name==@"MACHINE\alice"?resolvedSid:null;
        Assert.Equal(expected,StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe",Resolve));
    }
    [Fact] public void NamedTriggerFailsClosedWithoutResolverOrWhenResolutionThrows()
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(n+"LogonTrigger").Single().Element(n+"UserId")!.Value=@"MACHINE\alice";
        Assert.False(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe"));
        Assert.False(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe",_=>throw new InvalidOperationException("Identity resolution unavailable.")));
    }
    [Fact] public void AccountNameResolutionDoesNotWeakenPrincipalOwnership()
    {
        var xml=XDocument.Parse(StartupTaskPolicy.CreateXml(Sid,@"C:\Apps\DeadWare.exe"));
        XNamespace n="http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(n+"Principal").Single().Element(n+"UserId")!.Value=@"MACHINE\alice";
        Assert.False(StartupTaskPolicy.IsOwned(xml.ToString(),Sid));
        Assert.False(StartupTaskPolicy.IsCurrent(xml.ToString(),Sid,@"C:\Apps\DeadWare.exe",_=>Sid));
    }
}

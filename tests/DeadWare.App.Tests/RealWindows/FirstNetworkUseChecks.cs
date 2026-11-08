using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// First network use on GitHub's elevated Windows runner (spec, First network use alerts → Tests → Real Windows): real ETW capture
/// feeds the alert service, and this process sends one UDP datagram to TEST-NET-1. A check, not a measurement: when capture is
/// unavailable it records that and passes. It records only whether capture ran and how long the episode took, never an app name,
/// address or path.
/// </summary>
[Collection("RealWindows")]
public sealed class FirstNetworkUseChecks
{
    private const string Machine="real-windows";
    private static readonly TimeSpan Wait=TimeSpan.FromSeconds(30);

    [ElevatedGitHubFact] public async Task First_network_use_sees_this_process_send_one_udp_datagram()
    {
        string root=Path.Combine(Path.GetTempPath(),$"deadware-first-use-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string alertPath=Path.Combine(root,"alerts.db");
        // Both seed markers and the learning dates lie in the past, so a new app alerts at once and no history is read for a seed.
        var past=UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-10));
        var seeded=new AlertStore(alertPath);seeded.Initialize();
        seeded.Append(Machine,[],DateTimeOffset.UtcNow,new FirstUseChanges([],[],new FirstUseMetadata(past,past,past,past.AddDays(3))));
        var usage=new UsageHistoryService(Path.Combine(root,"usage-history.db"),Machine);
        var alerts=new AlertService(alertPath,Machine,usage,()=>null,flushInterval:TimeSpan.FromSeconds(1));
        alerts.Configure([AlertRules.DefaultFirstNetworkUse(DateTimeOffset.UtcNow)],false);
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));
        var available=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleAvailable+=reading=>
        {
            if(reading.Connections is {Available:true})available.TrySetResult();
            alerts.TryRecordNetworkUse(reading);
        };
        string image=AppKey.Of(Path.GetFileName(Environment.ProcessPath!));
        try
        {
            alerts.Start();collector.Start();
            if(await Task.WhenAny(available.Task,Task.Delay(Wait))!=available.Task)
            {
                MeasurementRecorder.Write("first-network-use",new{capture="unavailable",seconds=(double?)null});
                return;
            }
            var sent=Stopwatch.StartNew();
            using(var udp=new UdpClient(AddressFamily.InterNetwork))udp.Send([0],1,new IPEndPoint(IPAddress.Parse("192.0.2.1"),9));
            AlertEpisode[] mine=[];
            while(sent.Elapsed<Wait)
            {
                var log=await alerts.ReadEpisodesAsync(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
                mine=[..log.Episodes.Where(e=>e.Kind==AlertKind.FirstNetworkUse&&e.Application is {} name&&AppKey.Of(name)==image)];
                if(mine.Length>0)break;
                await Task.Delay(250);
            }
            double? seconds=mine.Length>0?Math.Round(sent.Elapsed.TotalSeconds,1):null;
            MeasurementRecorder.Write("first-network-use",new{capture="available",seconds});
            Assert.Single(mine);
            Assert.False(alerts.Failed);
        }
        finally
        {
            await collector.DisposeAsync();
            await alerts.StopAsync();await usage.StopAsync();
            try {Directory.Delete(root,true);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {}
        }
    }
}

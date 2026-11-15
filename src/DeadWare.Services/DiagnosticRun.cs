using System.Text.Json;
using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>
/// The <c>--diagnose output.json [--samples n] [--no-hardware] [--no-etw] [--no-processes]</c> run: a short collection written
/// to a JSON report, with no window. Shared by both front ends so the report is the same from either.
/// </summary>
internal static class DiagnosticRun
{
    public static CollectorOptions Options(string[] args,bool elevated)
    {
        bool processes=!args.Contains("--no-processes"),etw=!args.Contains("--no-etw");
        return new(HardwareEnabled:!args.Contains("--no-hardware"),EtwEnabled:etw,DiagnosticTimings:true,ProcessMonitoring:processes,ProcessDiskTracing:ProcessFeatures.DiskColumns&&elevated&&processes&&etw);
    }
    public static string Report(string version,string status,string? stage,int deadlineSeconds,CollectorOptions options,IReadOnlyList<Reading> samples)=>
        JsonSerializer.Serialize(new{Version=version,Status=status,Stage=stage,CollectionDeadlineSeconds=deadlineSeconds,ShutdownDeadlineSeconds=3,Options=options,Samples=samples},new JsonSerializerOptions{WriteIndented=true});
    /// <summary>Collects the requested samples and writes the report; returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args,string version)
    {
        Collector? collector=null;string? output=null;int exit=0;string status="Starting";
        var readings=new List<Reading>();int deadlineSeconds=15;
        var options=Options(args,Collector.IsElevated);
        async Task WriteReport()
        {
            if(output is null)return;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var text=Report(version,status,collector?.Stage,deadlineSeconds,options,readings);
            string temporary=output+".tmp";await File.WriteAllTextAsync(temporary,text);File.Move(temporary,output,true);
        }
        try
        {
            int index=Array.IndexOf(args,"--diagnose");
            if(index+1>=args.Length)throw new ArgumentException("Provide an output file after --diagnose.");
            output=Path.GetFullPath(args[index+1]);
            int sampleIndex=Array.IndexOf(args,"--samples");
            int count=sampleIndex>=0&&sampleIndex+1<args.Length?int.Parse(args[sampleIndex+1]):3;
            if(count is <1 or >60)throw new ArgumentException("Samples must be between 1 and 60.");
            deadlineSeconds=Math.Max(15,count+10);
            collector=new(options);
            // An initial report survives an external watchdog exit during blocked hardware discovery.
            await WriteReport();
            var channel=Channel.CreateUnbounded<Reading>(new(){SingleReader=true,SingleWriter=true});
            collector.SampleAvailable+=reading=>channel.Writer.TryWrite(reading);
            collector.Start();
            _ = collector.Completion.ContinueWith(task=>channel.Writer.TryComplete(task.Exception),TaskScheduler.Default);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(deadlineSeconds));
            while(readings.Count<count)
            {
                readings.Add(await channel.Reader.ReadAsync(deadline.Token));status="Collecting";await WriteReport();
            }
            status="Completed";
        }
        catch(OperationCanceledException) {status="Diagnostic collection deadline exceeded";exit=1;}
        catch(Exception e) {status=$"Diagnostic failed ({e.GetType().Name})";exit=1;}
        finally
        {
            // Write partial results before cleanup so a native hardware hang cannot discard evidence.
            try {await WriteReport();}catch(Exception){exit=1;}
            if(collector is not null)
            {
                try {await collector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));}
                catch(TimeoutException) {status+="; provider shutdown deadline exceeded";exit=1;}
                catch(Exception e) {status+=$"; shutdown failed ({e.GetType().Name})";exit=1;}
            }
            try {await WriteReport();}catch(Exception){exit=1;}
        }
        return exit;
    }
}

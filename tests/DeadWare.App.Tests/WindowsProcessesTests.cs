using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class WindowsProcessesTests
{
    private const string Mismatch="Process scan unavailable (NTSTATUS 0xC0000004)";
    // 32 MiB is 33,554,432 bytes: 33,300,000 + 256 KiB passes it, the largest allowed request lands on it exactly.
    [Fact] public void Buffer_grows_to_returned_plus_256_KiB_and_stops_at_32_MiB()
    {
        Assert.Equal(2_000_000+262_144,WindowsProcesses.NextBufferSize(2_000_000));
        Assert.Null(WindowsProcesses.NextBufferSize(33_300_000));
        Assert.Equal(WindowsProcesses.MaximumBufferBytes,WindowsProcesses.NextBufferSize(WindowsProcesses.MaximumBufferBytes-WindowsProcesses.GrowthBytes));
        Assert.Null(WindowsProcesses.NextBufferSize(WindowsProcesses.MaximumBufferBytes-WindowsProcesses.GrowthBytes+1));
    }
    [Fact] public void Failure_retries_at_most_once_a_minute()
    {
        Assert.False(WindowsProcesses.ShouldAttempt(30,0));Assert.True(WindowsProcesses.ShouldAttempt(60,0));Assert.True(WindowsProcesses.ShouldAttempt(0,null));
    }
    [Fact] public void Status_names_the_ntstatus_in_hex()=>Assert.Equal(Mismatch,WindowsProcesses.FailureStatus(0xC0000004));
    [Fact] public void Status_appears_only_after_60_s_of_continuous_failure()
    {
        const string f=Mismatch;
        Assert.Equal("",WindowsProcesses.PacedStatus(59.9,0,f));Assert.Equal(f,WindowsProcesses.PacedStatus(60,0,f));Assert.Equal(f,WindowsProcesses.PacedStatus(500,0,f));
        Assert.Equal("",WindowsProcesses.PacedStatus(500,null,f));Assert.Equal("",WindowsProcesses.PacedStatus(130,100,f));
    }

    /// <summary>Scripted native query: each call answers with the next (status, returned) pair, the last one repeating.</summary>
    private sealed class ScriptedQuery(params (uint Status,int Returned)[] script)
    {
        public List<int> Lengths {get;}=[];
        public (uint Status,int Returned)[] Script {get;set;}=script;
        public uint Query(byte[] buffer,out int returned)
        {
            Lengths.Add(buffer.Length);
            var (status,size)=Script[Math.Min(Lengths.Count-1,Script.Length-1)];
            // A lone PID 0 entry (all zero) parses to an empty scan, so success needs no real process layout.
            if(status==0)buffer.AsSpan(0,size).Clear();
            returned=size;return status;
        }
    }
    private const int IdleOnly=0x100;

    [Fact] public void Length_mismatch_grows_the_buffer_and_retries_in_the_same_scan()
    {
        var q=new ScriptedQuery((0xC0000004,2_000_000),(0,IdleOnly));
        var source=new WindowsProcesses(q.Query);
        var scan=source.Scan(5,1);
        Assert.NotNull(scan);
        Assert.Equal(new[]{WindowsProcesses.InitialBufferBytes,2_000_000+WindowsProcesses.GrowthBytes},q.Lengths);
        Assert.Equal(2_000_000+WindowsProcesses.GrowthBytes,scan!.BufferBytes);
        Assert.Equal(5,scan.MonotonicSeconds);Assert.Equal(1,scan.PollingIntervalSeconds);Assert.Empty(scan.Processes);
        Assert.Equal("",source.Status);Assert.Equal("",source.Failure);
        // The grown buffer is reused by the next scan.
        Assert.NotNull(source.Scan(6,1));Assert.Equal(2_000_000+WindowsProcesses.GrowthBytes,q.Lengths[^1]);
    }
    [Fact] public void Three_retries_then_the_scan_fails_with_the_ntstatus()
    {
        var q=new ScriptedQuery((0xC0000004,2_000_000));
        var source=new WindowsProcesses(q.Query);
        Assert.Null(source.Scan(0,1));
        Assert.Equal(1+WindowsProcesses.MaximumRetries,q.Lengths.Count);
        Assert.Equal(Mismatch,source.Failure);
        Assert.Equal("",source.Status);
    }
    [Fact] public void A_failed_scan_waits_a_minute_and_its_status_is_paced()
    {
        var q=new ScriptedQuery((0xC0000001,0));
        var source=new WindowsProcesses(q.Query);
        Assert.Null(source.Scan(0,1));Assert.Single(q.Lengths);
        Assert.Equal("Process scan unavailable (NTSTATUS 0xC0000001)",source.Failure);Assert.Equal("",source.Status);
        Assert.Null(source.Scan(30,1));Assert.Single(q.Lengths);
        Assert.Equal("Process scan unavailable (NTSTATUS 0xC0000001)",source.Failure);Assert.Equal("",source.Status);
        Assert.Null(source.Scan(60,1));Assert.Equal(2,q.Lengths.Count);
        Assert.Equal("Process scan unavailable (NTSTATUS 0xC0000001)",source.Status);
        q.Script=[(0,IdleOnly)];
        Assert.Null(source.Scan(90,1));Assert.Equal(2,q.Lengths.Count);
        Assert.NotNull(source.Scan(120,1));
        Assert.Equal("",source.Status);Assert.Equal("",source.Failure);
    }
    [Fact] public void A_malformed_buffer_fails_with_the_parser_reason()
    {
        var q=new ScriptedQuery((0,IdleOnly-1));
        var source=new WindowsProcesses(q.Query);
        Assert.Null(source.Scan(0,1));
        Assert.Equal("Process scan unavailable (entry does not fit)",source.Failure);
    }
    [Fact] public void Processor_count_is_positive()=>Assert.True(new WindowsProcesses().ProcessorCount>0);
}

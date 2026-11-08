using System.Globalization;
using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class TrafficStripTests
{
    static DateTimeOffset T(int h,int m,int s=0)=>new(2026,11,7,h,m,s,TimeSpan.Zero);
    static TrafficStripSlot S(DateTimeOffset start,DateTimeOffset end,long seconds,bool incomplete=false,long[]? received=null,long[]? sent=null)=>
        new(start,end,TrafficStrips.MinuteStarts(start,end),seconds,incomplete,received??[0],sent??[0]);

    [Fact] public void Connections_bucket_minutes_keep_at_most_1500_whole_minute_slots(){ var a=T(0,0);
        foreach(var (span,b,count) in new[]{(TimeSpan.FromMinutes(5),1L,5),(TimeSpan.FromDays(1),1L,1440),(TimeSpan.FromHours(25),1L,1500),(TimeSpan.FromDays(2),2L,1440),
            (TimeSpan.FromDays(7),10L,1008),(TimeSpan.FromDays(30),30L,1440),(TimeSpan.FromDays(31),30L,1488),(TimeSpan.FromDays(40),39L,1477)})
        {Assert.Equal(b,TrafficStrips.ConnectionsBucketMinutes(a,a+span));Assert.Equal(count,TrafficStrips.ConnectionsSlotCount(a,a+span));}
        var from=T(10,0,20);var to=T(12,0,30);Assert.Equal(T(10,1),TrafficStrips.ConnectionsSlot(from,to,0).Start);
        Assert.Equal(to,TrafficStrips.ConnectionsSlot(from,to,TrafficStrips.ConnectionsSlotCount(from,to)-1).End);}
    [Fact] public void Connections_slot_rejects_an_index_outside_the_slot_count(){
        var from=T(10,0,20);var to=T(10,3);Assert.Equal(2,TrafficStrips.ConnectionsSlotCount(from,to));
        Assert.Equal((T(10,2),to),TrafficStrips.ConnectionsSlot(from,to,1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>TrafficStrips.ConnectionsSlot(from,to,-1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>TrafficStrips.ConnectionsSlot(from,to,2));}
    [Fact] public void Connections_slot_count_is_zero_without_a_whole_minute_and_no_slot_is_valid(){
        foreach(var (from,to) in new[]{(T(10,0),T(10,0)),(T(10,0,10),T(10,0,50)),(T(10,1),T(10,0))})
        {Assert.Equal(0,TrafficStrips.ConnectionsSlotCount(from,to));Assert.Throws<ArgumentOutOfRangeException>(()=>TrafficStrips.ConnectionsSlot(from,to,0));}}
    [Fact] public void Is_full_counts_the_whole_minutes_that_start_in_the_slot(){
        Assert.True(TrafficStrips.IsFull(S(T(10,0),T(10,1),60)));Assert.False(TrafficStrips.IsFull(S(T(10,0),T(10,1),59)));
        Assert.True(TrafficStrips.IsFull(S(T(10,0),T(10,10),570)));Assert.False(TrafficStrips.IsFull(S(T(10,0),T(10,10),569)));
        var six=(T(10,0,10),T(10,0,10).AddMilliseconds(403_200));Assert.Equal(6,TrafficStrips.MinuteStarts(six.Item1,six.Item2));
        Assert.True(TrafficStrips.IsFull(S(six.Item1,six.Item2,360)));Assert.False(TrafficStrips.IsFull(S(six.Item1,six.Item2,300)));
        var seven=(T(10,0),T(10,0).AddMilliseconds(403_200));Assert.Equal(7,TrafficStrips.MinuteStarts(seven.Item1,seven.Item2));
        Assert.True(TrafficStrips.IsFull(S(seven.Item1,seven.Item2,420)));Assert.False(TrafficStrips.IsFull(S(seven.Item1,seven.Item2,360)));
        Assert.False(TrafficStrips.IsFull(S(T(10,0),T(10,1),60,incomplete:true)));}
    [Fact] public void Due_until_keeps_unsaved_minutes_out_of_the_gaps(){ var delay=TimeSpan.FromSeconds(16);
        Assert.True(TrafficStrips.DueUntil(T(11,0),T(10,0,10),delay)<T(10,0));Assert.Equal(T(10,0),TrafficStrips.DueUntil(T(11,0),T(10,0,16),delay));Assert.Equal(T(11,0),TrafficStrips.DueUntil(T(11,0),T(12,0),delay));}
    [Fact] public void Rate_divides_measure_bytes_by_recorded_seconds_and_is_null_without_recorded_time(){ var s=S(T(10,0),T(10,1),10,received:[100,0,50],sent:[20,30,0]);
        Assert.Equal(12,TrafficStrips.Rate(s,0,TrafficMeasure.Total));Assert.Equal(10,TrafficStrips.Rate(s,0,TrafficMeasure.Received));Assert.Equal(2,TrafficStrips.Rate(s,0,TrafficMeasure.Sent));
        Assert.Equal(5,TrafficStrips.Rate(s,2,TrafficMeasure.Total));Assert.Null(TrafficStrips.Rate(s with{RecordedSeconds=0},0,TrafficMeasure.Total));
        Assert.Equal(long.MaxValue,TrafficStrips.Bytes(S(T(10,0),T(10,1),60,received:[long.MaxValue],sent:[1]),0,TrafficMeasure.Total));}
    [Fact] public void Slot_at_finds_the_listed_slot_holding_a_time_and_null_in_gaps(){
        var strip=new TrafficStrip(T(10,0),T(10,3),60_000,TrafficMeasure.Total,[],0,0,0,[S(T(10,0),T(10,1),60),S(T(10,2),T(10,3),60)]);
        Assert.Equal(0,TrafficStrips.SlotAt(strip,T(10,0,30)));Assert.Null(TrafficStrips.SlotAt(strip,T(10,1,30)));Assert.Equal(1,TrafficStrips.SlotAt(strip,T(10,2)));Assert.Null(TrafficStrips.SlotAt(strip,T(10,3)));
        Assert.Null(TrafficStrips.SlotAt(strip,T(9,59,59)));
        Assert.Null(TrafficStrips.SlotAt(strip with{Slots=[]},T(10,0,30)));}
    [Fact] public void Connections_target_uses_whole_minutes_and_clips_to_thirty_days(){ var now=T(12,0,30);
        Assert.Equal((T(11,1),T(11,5)),TrafficStrips.ConnectionsTarget(T(11,0,20),T(11,5),now));
        Assert.Equal((TrafficStrips.CeilingMinute(now.AddDays(-30)),TrafficStrips.CeilingMinute(now.AddDays(-29))),TrafficStrips.ConnectionsTarget(now.AddDays(-31),now.AddDays(-29),now));
        Assert.Null(TrafficStrips.ConnectionsTarget(now.AddDays(-40),now.AddDays(-31),now));
        Assert.Equal((T(10,1),T(10,3)),TrafficStrips.ConnectionsTarget(T(10,0,30),T(10,2,54),now));Assert.Equal((T(10,0),T(10,3)),TrafficStrips.ConnectionsTarget(T(10,0),T(10,2,24),now));
        Assert.Null(TrafficStrips.ConnectionsTarget(T(10,0,10),T(10,0,50),now));}
    [Fact] public void Slot_length_text_uses_minutes_under_an_hour(){ var previous=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
            Assert.Equal(["1-minute","3.2-minute","30-minute","1.6-hour"],new long[]{60_000,192_000,1_800_000,5_760_000}.Select(TrafficStrips.SlotLengthText));}
        finally{CultureInfo.CurrentCulture=previous;}}
    [Fact] public void Application_key_normalizes_then_upper_cases_and_is_idempotent(){ var longName=new string('a',300);
        Assert.Equal(ProcessGrouping.Key(" chrome.exe"),ProcessGrouping.Key("Chrome.EXE"));Assert.Equal(ProcessGrouping.Key("Chrome.EXE"),ProcessGrouping.Key("chr\u0001ome.exe"));
        Assert.Equal("UNKNOWN APPLICATION",ProcessGrouping.Key(""));Assert.Equal("UNKNOWN APPLICATION",ProcessGrouping.Key(null!));Assert.Equal(new string('A',256),ProcessGrouping.Key(longName));
        foreach(var x in new[]{" chrome.exe","Chrome.EXE","chr\u0001ome.exe","",longName})Assert.Equal(ProcessGrouping.Key(x),ProcessGrouping.Key(ProcessGrouping.Key(x)));}
}

using System.Buffers.Binary;
using System.Text;
using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessSnapshotParserTests
{
    const ulong Base=0x10_0000;
    static ScanEntryLayout Entry(int pid,string name,int threads=1,long create=0,long user=0,long kernel=0,long ws=0,long read=0,long write=0,int session=0)=>new(pid,name,threads,create,user,kernel,ws,read,write,session);
    static IReadOnlyList<ProcessScanEntry> Parse(ScanBuffer b){Assert.True(ProcessSnapshotParser.TryParse(b,Base,null,out var e,out _,out var error),error);return e;}
    static void AssertFails(ScanBuffer b,string reason,IReadOnlyDictionary<(int,long),string>? known=null){
        Assert.False(ProcessSnapshotParser.TryParse(b,Base,known,out var e,out _,out var error));Assert.Equal(reason,error);Assert.Empty(e);}

    [Fact] public void Reads_every_field_at_its_x64_offset(){ var b=ScanBuffer.Of(Entry(4,"System",threads:3,create:100,user:7,kernel:9,ws:4096,read:11,write:12,session:0));
        Assert.True(ProcessSnapshotParser.TryParse(b,Base,null,out var e,out int threads,out _));var s=Assert.Single(e);
        Assert.Equal((4,100+ProcessSnapshotParser.GenerationOffset,"System",0,3,4096L,9L,7L,11L,12L),(s.Pid,s.Generation,s.Name,s.SessionId,s.Threads,s.PrivateWorkingSet,s.KernelTime,s.UserTime,s.ReadTransfer,s.WriteTransfer));Assert.Equal(3,threads);Assert.Equal(16L,s.CpuTime);}
    [Fact] public void Idle_entry_with_empty_name_passes_checks_and_is_dropped(){ Assert.True(ProcessSnapshotParser.TryParse(ScanBuffer.Of(Entry(0,"",threads:8),Entry(4,"System")),Base,null,out var e,out int t,out _));
        Assert.Equal([4],e.Select(x=>x.Pid));Assert.Equal(9,t);}
    [Fact] public void Zero_next_offset_ends_the_walk()=>Assert.Equal(2,Parse(ScanBuffer.Of(Entry(4,"System"),Entry(8,"a.exe"))).Count);
    [Fact] public void Last_entry_whose_threads_overrun_the_buffer_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System",threads:1000)).Truncate(0x100+0x50),"entry does not fit");
    [Fact] public void Next_offset_smaller_than_the_entry_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System",threads:2),Entry(8,"a.exe")).WithNext(0,0x100),"bad next offset");
    [Fact] public void Next_offset_past_the_buffer_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System")).WithNext(0,1<<20),"bad next offset");
    [Fact] public void Odd_name_length_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System")).WithNameLength(0,11),"odd name length");
    [Fact] public void Name_outside_the_buffer_fails()=>AssertFails(ScanBuffer.Of(Entry(4,"System")).WithNamePointer(0,Base+(1UL<<30)),"name outside buffer");
    [Fact] public void More_than_65536_entries_fails()=>AssertFails(ScanBuffer.Of(Enumerable.Range(1,65537).Select(i=>Entry(i*4,"")).ToArray()),"too many entries");
    [Fact] public void Known_names_are_reused_without_decoding(){ var gen=100+ProcessSnapshotParser.GenerationOffset;
        Assert.True(ProcessSnapshotParser.TryParse(ScanBuffer.Of(Entry(8,"fresh.exe",create:100)),Base,new Dictionary<(int,long),string>{[(8,gen)]="cached.exe"},out var e,out _,out var error),error);Assert.Equal("cached.exe",Assert.Single(e).Name);}
    [Fact] public void A_known_name_is_still_checked_against_the_buffer()=>AssertFails(ScanBuffer.Of(Entry(8,"fresh.exe",create:100)).WithNamePointer(0,Base+(1UL<<30)),
        "name outside buffer",new Dictionary<(int,long),string>{[(8,100+ProcessSnapshotParser.GenerationOffset)]="cached.exe"});

    sealed record ScanEntryLayout(int Pid,string Name,int Threads,long Create,long User,long Kernel,long Ws,long Read,long Write,int Session);
    // Lays out x64 SYSTEM_PROCESS_INFORMATION entries: fixed part, thread entries, then the UTF-16 name; pointers are Base + offset.
    sealed class ScanBuffer
    {
        byte[] data;
        readonly List<int> starts;
        ScanBuffer(byte[] data,List<int> starts){this.data=data;this.starts=starts;}
        public static implicit operator ReadOnlySpan<byte>(ScanBuffer b)=>b.data;
        public static ScanBuffer Of(params ScanEntryLayout[] entries)
        {
            var sizes=entries.Select(e=>0x100+e.Threads*0x50+Encoding.Unicode.GetByteCount(e.Name)).ToArray();
            var data=new byte[sizes.Sum()];var starts=new List<int>();int at=0;
            for(int i=0;i<entries.Length;i++)
            {
                var e=entries[i];var s=data.AsSpan(at);starts.Add(at);
                int nameAt=at+0x100+e.Threads*0x50,nameBytes=Encoding.Unicode.GetByteCount(e.Name);
                BinaryPrimitives.WriteUInt32LittleEndian(s,i==entries.Length-1?0u:(uint)sizes[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(s[4..],(uint)e.Threads);
                BinaryPrimitives.WriteInt64LittleEndian(s[0x08..],e.Ws);
                BinaryPrimitives.WriteInt64LittleEndian(s[0x20..],e.Create);
                BinaryPrimitives.WriteInt64LittleEndian(s[0x28..],e.User);
                BinaryPrimitives.WriteInt64LittleEndian(s[0x30..],e.Kernel);
                BinaryPrimitives.WriteUInt16LittleEndian(s[0x38..],(ushort)nameBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(s[0x40..],nameBytes==0?0:Base+(ulong)nameAt);
                BinaryPrimitives.WriteUInt64LittleEndian(s[0x50..],(ulong)e.Pid);
                BinaryPrimitives.WriteUInt32LittleEndian(s[0x64..],(uint)e.Session);
                BinaryPrimitives.WriteInt64LittleEndian(s[0xE8..],e.Read);
                BinaryPrimitives.WriteInt64LittleEndian(s[0xF0..],e.Write);
                Encoding.Unicode.GetBytes(e.Name,data.AsSpan(nameAt));
                at+=sizes[i];
            }
            return new(data,starts);
        }
        public ScanBuffer Truncate(int length){data=data[..length];return this;}
        public ScanBuffer WithNext(int entry,uint next){BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(starts[entry]),next);return this;}
        public ScanBuffer WithNameLength(int entry,ushort length){BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(starts[entry]+0x38),length);return this;}
        public ScanBuffer WithNamePointer(int entry,ulong pointer){BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(starts[entry]+0x40),pointer);return this;}
    }
}

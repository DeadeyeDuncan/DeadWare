using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace DeadWare.Core;
/// <summary>Parses an x64 SYSTEM_PROCESS_INFORMATION buffer (NtQuerySystemInformation class 5) into scan entries.</summary>
public static class ProcessSnapshotParser
{
    public const int FixedSize=0x100,ThreadEntrySize=0x50,MaximumEntries=65536;
    /// <summary>Adds to a FILETIME create time to give DateTime ticks (the 1601 to 0001 offset).</summary>
    public const long GenerationOffset=504911232000000000;
    public static bool TryParse(ReadOnlySpan<byte> buffer,ulong bufferAddress,IReadOnlyDictionary<(int Pid,long Generation),string>? knownNames,out IReadOnlyList<ProcessScanEntry> entries,out int threads,out string? error)
    {
        var list=new List<ProcessScanEntry>();
        int total=0,count=0;long offset=0;
        while(true)
        {
            if(count>=MaximumEntries)return Fail("too many entries",out entries,out threads,out error);
            var entry=buffer[(int)offset..];
            if(entry.Length<FixedSize)return Fail("entry does not fit",out entries,out threads,out error);
            uint next=BinaryPrimitives.ReadUInt32LittleEndian(entry);
            uint entryThreads=BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            long size=FixedSize+(long)entryThreads*ThreadEntrySize;
            if(size>entry.Length)return Fail("entry does not fit",out entries,out threads,out error);
            if(next!=0&&(next<size||next>entry.Length))return Fail("bad next offset",out entries,out threads,out error);
            ushort nameLength=BinaryPrimitives.ReadUInt16LittleEndian(entry[0x38..]);
            if((nameLength&1)!=0)return Fail("odd name length",out entries,out threads,out error);
            ulong namePointer=BinaryPrimitives.ReadUInt64LittleEndian(entry[0x40..]);
            int pid=(int)BinaryPrimitives.ReadUInt64LittleEndian(entry[0x50..]);
            long generation=BinaryPrimitives.ReadInt64LittleEndian(entry[0x20..])+GenerationOffset;
            ReadOnlySpan<byte> nameBytes=default;
            if(nameLength>0)
            {
                ulong relative=namePointer-bufferAddress;
                if(namePointer<bufferAddress||relative>(ulong)buffer.Length||(ulong)buffer.Length-relative<nameLength)return Fail("name outside buffer",out entries,out threads,out error);
                nameBytes=buffer.Slice((int)relative,nameLength);
            }
            total+=(int)entryThreads;count++;
            if(pid!=0)
            {
                if(knownNames is null||!knownNames.TryGetValue((pid,generation),out var name))name=nameBytes.IsEmpty?"":new string(MemoryMarshal.Cast<byte,char>(nameBytes));
                list.Add(new ProcessScanEntry(pid,generation,name,(int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x64..]),(int)entryThreads,BinaryPrimitives.ReadInt64LittleEndian(entry[0x08..]),BinaryPrimitives.ReadInt64LittleEndian(entry[0x30..]),BinaryPrimitives.ReadInt64LittleEndian(entry[0x28..]),BinaryPrimitives.ReadInt64LittleEndian(entry[0xE8..]),BinaryPrimitives.ReadInt64LittleEndian(entry[0xF0..])));
            }
            if(next==0)break;
            offset+=next;
        }
        entries=list;threads=total;error=null;return true;
    }
    static bool Fail(string reason,out IReadOnlyList<ProcessScanEntry> entries,out int threads,out string? error){entries=[];threads=0;error=reason;return false;}
}

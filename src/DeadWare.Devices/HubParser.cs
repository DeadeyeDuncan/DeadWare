namespace DeadWare.Devices;

internal enum HubStatus{Ok,WrongMode,Other}
internal readonly record struct HubValue(bool Available,short Raw);
internal sealed record HubChannel(int Channel,byte Type,byte Model);
internal sealed record HubList(IReadOnlyList<HubChannel> Occupied,bool Overflow,int Used);

// Reads the hub's replies (the 512 bytes after the report id). Every offset is bounded by reply.Length, which can be less than 512.
internal static class HubParser
{
    public const int ReplyLength=512,MaxEntries=168;

    public static bool Echoes(ReadOnlySpan<byte> reply,byte opcode)=>reply.Length>=3&&reply[2]==opcode;
    public static HubStatus Status(ReadOnlySpan<byte> reply)=>reply.Length<4?HubStatus.Other:reply[3] switch{0x00=>HubStatus.Ok,0x03=>HubStatus.WrongMode,_=>HubStatus.Other};

    // Checks the data type only: not the status, not the opcode, and an open's leftover bytes can pass it. The worker must require Echoes(0x08), IsData and Status==Ok together.
    public static bool IsData(ReadOnlySpan<byte> reply,HubEndpoint ep)
    {
        if(reply.Length<7) return false;
        byte type=ep switch{HubEndpoint.Devices=>0x21,HubEndpoint.Temperatures=>0x10,HubEndpoint.Speeds=>0x25,_=>0};
        return type!=0&&reply[4]==type&&reply[5]==0x00;
    }

    public static string? Firmware(ReadOnlySpan<byte> reply)=>reply.Length<8?null:$"{reply[4]}.{reply[5]}.{reply[6]|(reply[7]<<8)}";

    // Records from [7], channel 1 first; 8 bytes (type at +2, model at +3, id length at +7) then the id, which is skipped and never kept. Id length 0 is an
    // empty channel. A record that would run past the reply sets Overflow.
    public static HubList? Devices(ReadOnlySpan<byte> reply)
    {
        if(reply.Length<7) return null;
        int last=reply[6],at=7;
        var occupied=new List<HubChannel>();
        bool overflow=false;
        for(int ch=1;ch<=last;ch++)
        {
            if(at+8>reply.Length){overflow=true;break;}
            int idLength=reply[at+7],end=at+8+idLength;
            if(end>reply.Length){overflow=true;break;}
            if(idLength>0) occupied.Add(new HubChannel(ch,reply[at+2],reply[at+3]));
            at=end;
        }
        return new HubList(occupied,overflow,at);
    }

    // Entry i at 7+3i: a status byte (00 available) and a 16-bit little-endian value kept signed (right for temperatures; a speed reads it unsigned).
    public static HubValue[]? Values(ReadOnlySpan<byte> reply)
    {
        if(reply.Length<7) return null;
        int count=reply[6];
        if(7+3*count>reply.Length) return null;
        var values=new HubValue[count];
        for(int i=0;i<count;i++)
        {
            int o=7+3*i;
            values[i]=new HubValue(reply[o]==0,(short)(reply[o+1]|(reply[o+2]<<8)));
        }
        return values;
    }
}

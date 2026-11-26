using DeadWare.Core;
namespace DeadWare.Devices;

// The readable endpoints of the iCUE LINK hub. There is no member for 0x18 (the speed endpoint), and no factory below takes any other byte.
internal enum HubEndpoint:byte{Devices=0x36,Temperatures=0x21,Speeds=0x17}

// One 513-byte output report to the hub: report id 0, then 00 01, then the command from [3], zero-padded. The constructor is private, a frame's bytes
// cannot change once it is built and Bytes hands out a copy, so the array behind it never leaves the type. The factories below are the whole list of what DeadWare says to the hub:
// the hub also carries the pump and the radiator fans, and no speed command can be built.
internal sealed class HubFrame
{
    public const int Length=513,ChunkBytes=508;
    const byte ColourEndpoint=0x22;
    readonly byte[] bytes;

    HubFrame(ReadOnlySpan<byte> command)
    {
        bytes=new byte[Length];
        bytes[2]=1;
        command.CopyTo(bytes.AsSpan(3));
    }

    // A copy: the frame's own array is never handed out, not even through MemoryMarshal.TryGetArray.
    public ReadOnlyMemory<byte> Bytes=>bytes.ToArray();
    // The reply echoes this.
    public byte Opcode=>bytes[3];

    public static HubFrame Firmware()=>new([0x02,0x13]);
    public static HubFrame SoftwareMode()=>new([0x01,0x03,0x00,0x02]);
    public static HubFrame HardwareMode()=>new([0x01,0x03,0x00,0x01]);
    public static HubFrame Close(HubEndpoint ep)=>new([0x05,0x01,0x01,Checked(ep)]);
    public static HubFrame Open(HubEndpoint ep)=>new([0x0D,0x01,Checked(ep)]);
    public static HubFrame Read()=>new([0x08,0x01]);
    public static HubFrame CloseColours()=>new([0x05,0x01,0x01,ColourEndpoint]);
    public static HubFrame OpenColours()=>new([0x0D,0x00,ColourEndpoint]);

    // The payload [n+2 low, n+2 high, 00, 00, 12, 00, R,G,B...] (n = 3 x LEDs) in chunks of at most ChunkBytes: the first as 06 00 <chunk>, each later one
    // as 07 00 <chunk>. No LEDs, no frames.
    public static IReadOnlyList<HubFrame> Colours(ReadOnlySpan<Rgb> leds)
    {
        if(leds.Length==0) return [];
        int n=3*leds.Length;
        var payload=new byte[6+n];
        payload[0]=(byte)((n+2)&0xFF);payload[1]=(byte)((n+2)>>8);payload[4]=0x12;
        for(int i=0;i<leds.Length;i++){payload[6+3*i]=leds[i].R;payload[7+3*i]=leds[i].G;payload[8+3*i]=leds[i].B;}
        var frames=new List<HubFrame>();
        for(int at=0;at<payload.Length;at+=ChunkBytes)
        {
            var chunk=payload.AsSpan(at,Math.Min(ChunkBytes,payload.Length-at));
            var command=new byte[2+chunk.Length];
            command[0]=at==0?(byte)0x06:(byte)0x07;
            chunk.CopyTo(command.AsSpan(2));
            frames.Add(new HubFrame(command));
        }
        return frames;
    }

    // Any byte casts to the enum, so the factory itself checks.
    static byte Checked(HubEndpoint ep)=>ep is HubEndpoint.Devices or HubEndpoint.Temperatures or HubEndpoint.Speeds?(byte)ep:throw new ArgumentOutOfRangeException(nameof(ep));
}

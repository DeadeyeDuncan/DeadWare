using System.Text;
namespace DeadWare.Devices.Tests;

// Lighting 5: the hub's replies as the notes describe them (the 2026-11-25 reading), built in code from literal bytes. The ids are synthetic: 26 ASCII
// characters each, nothing a real hub reported. A reply is the 512 bytes after the report id.
internal static class HubFixtures
{
    public const string IdA="0100DEADWARE0000000000000A",IdB="0100DEADWARE0000000000000B",IdC="0100DEADWARE0000000000000C",IdD="0100DEADWARE0000000000000D";

    // "00 00 08 00" -> bytes
    public static byte[] Hex(string hex)=>hex.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(h=>Convert.ToByte(h,16)).ToArray();
    static byte[] Id(string id)=>Encoding.ASCII.GetBytes(id);
    static byte[] Zeros(int count)=>new byte[count];

    // The pieces, laid end to end (what runs past length is cut off), then zeros to length.
    public static byte[] Reply(int length,params byte[][] pieces)
    {
        var reply=new byte[length];
        int at=0;
        foreach(var piece in pieces){if(at<length) Array.Copy(piece,0,reply,at,Math.Min(piece.Length,length-at));at+=piece.Length;}
        return reply;
    }

    // Firmware 4.1.656: [4] 04 major, [5] 01 minor, [6..7] 90 02 patch little-endian (0x0290).
    public static byte[] Firmware()=>Reply(512,Hex("00 00 02 00 04 01 90 02"));

    // Last channel 0D (13). Channels 1 to 3: a fan (type 0F, model 00) with a 26-byte id (1A). Channels 4 to 12: empty, 8 zero bytes each. Channel 13: type 11,
    // model 02 (a pump). 7 + 3 x 34 + 9 x 8 + 34 = 215.
    public static byte[] DeviceList()=>Reply(512,
        Hex("00 00 08 00 21 00 0D"),
        Hex("00 00 0F 00 00 00 05 1A"),Id(IdA),
        Hex("00 00 0F 00 00 00 05 1A"),Id(IdB),
        Hex("00 00 0F 00 00 00 05 1A"),Id(IdC),
        Zeros(72),
        Hex("00 00 11 02 00 00 05 1A"),Id(IdD));

    // 0x34 (52) entries of three bytes. Entry 0 (the hub): 00 51 01 = 337. Entries 1 to 12: 01 00 00 (unavailable). Entry 13: 00 B5 01 = 437.
    // Entries 14 to 51: unavailable.
    public static byte[] Temperatures()=>Reply(512,
        Hex("00 00 08 00 10 00 34"),
        Hex("00 51 01"),
        Entries(12,"01 00 00"),
        Hex("00 B5 01"),
        Entries(38,"01 00 00"));

    // 1A (26) entries. Entry 0: unavailable. Entries 1 to 3 (the fans): 00 7C 06 = 1660, 00 76 06 = 1654, 00 7F 06 = 1663. Entries 4 to 12: unavailable.
    // Entry 13 (the pump): 00 D6 0A = 2774. Entries 14 to 25: unavailable.
    public static byte[] Speeds()=>Reply(512,
        Hex("00 00 08 00 25 00 1A"),
        Hex("01 00 00"),
        Hex("00 7C 06"),Hex("00 76 06"),Hex("00 7F 06"),
        Entries(9,"01 00 00"),
        Hex("00 D6 0A"),
        Entries(12,"01 00 00"));

    static byte[] Entries(int count,string entry)=>Enumerable.Range(0,count).SelectMany(_=>Hex(entry)).ToArray();

    // A values reply (temperatures: type 10 00; speeds: 25 00) of the given length and count, every entry available with value 0x0001.
    public static byte[] Values(int length,int count,string type="10 00")=>Reply(length,Hex("00 00 08 00 "+type+" "+count.ToString("X2")),Entries(count,"00 01 00"));

    // The reply to a command that is not a read: 00 00 <opcode> <status>, then what was left of the reply before from its offset 4 on.
    public static byte[] Ack(byte opcode,byte status=0,byte[]? leftovers=null)
    {
        var reply=new byte[512];
        reply[2]=opcode;reply[3]=status;
        if(leftovers is not null) Array.Copy(leftovers,4,reply,4,Math.Min(leftovers.Length,512)-4);
        return reply;
    }
}

namespace DeadWare.Devices;

// Lighting 4: the Aerox 9's HID link, behind an interface so the driver is tested against a fake. Byte offsets are DeadLight's: a packet
// is 64 bytes with the report id 0 at [0] and the opcode at [1]; a reply starts AT the opcode (the HidSharp adapter strips the report id).
internal interface IAeroxTransport
{
    // Product ids (0x1858 receiver, 0x185A cable) whose interface has usage page 0xFFC0, usage 1.
    IReadOnlyCollection<int> Present();
    IAeroxPort Open(int product);
}

internal interface IAeroxPort:IDisposable
{
    void Write(ReadOnlySpan<byte> packet);
    // The reply starting at the opcode, or null when nothing came within limitMs.
    byte[]? Read(int limitMs);
}

internal enum AeroxResult{Sent,Busy,NoReply,Failed,Absent}

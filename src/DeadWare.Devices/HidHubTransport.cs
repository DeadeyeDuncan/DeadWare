using HidSharp;

namespace DeadWare.Devices;

// Lighting 5: the iCUE LINK hub through HidSharp, the only HidSharp code for the hub. The interface is the one whose report descriptor lists
// usage page 0xFF42, usage 1; a device whose descriptor cannot be read is skipped, never chosen by its path, so the hub's interface 1 is never
// opened. The open is HidSharp's default, which is shared (never exclusive), with the write limit set to 250 ms and the read limit to each read's
// own (HidSharp's default for both is 3 s). HidSharp puts the report id at [0] of every report: a frame's [0] is already the report id 0, and a
// reply loses it here, so the worker sees the notes' offsets. Not unit-tested: it opens real hardware.
internal sealed class HidHubTransport:IHubTransport
{
    public const int Vendor=0x1B1C,Product=0x0C3F,UsagePage=0xFF42,Usage=1,LimitMs=250;
    const uint VendorUsage=(uint)UsagePage<<16|(uint)Usage;                  // 0xFF420001

    public int Count()=>Hubs().Count;

    // Exactly one hub or nothing: with two listed, neither is opened.
    public IHubPort Open()
    {
        var hubs=Hubs();
        if(hubs.Count!=1) throw new IOException(hubs.Count==0?"no iCUE LINK hub is connected":"more than one iCUE LINK hub is listed");
        var stream=hubs[0].Open();
        try{stream.WriteTimeout=LimitMs;stream.ReadTimeout=LimitMs;}
        catch{stream.Dispose();throw;}
        return new Port(stream);
    }

    static List<HidDevice> Hubs()=>DeviceList.Local.GetHidDevices(Vendor,Product).Where(VendorInterface).ToList();

    static bool VendorInterface(HidDevice d)
    {
        try{return d.GetReportDescriptor().DeviceItems.Any(i=>i.Usages.GetAllValues().Contains(VendorUsage));}
        catch{return false;}
    }

    // The two report buffers below and HubFrame's own are the only 513-byte buffers in src. Dispose may come from another thread: HidSharp then
    // ends a blocked call at once with ObjectDisposedException.
    sealed class Port(HidStream stream):IHubPort
    {
        readonly byte[] output=new byte[HubFrame.Length],input=new byte[HubFrame.Length];

        // The frame is copied whole into this port's own buffer, report id 0 at [0]. Bytes hands out a copy, so it is read once.
        public void Write(HubFrame frame)
        {
            var bytes=frame.Bytes.Span;
            if(bytes.Length!=output.Length) throw new ArgumentException("a hub frame is "+output.Length+" bytes",nameof(frame));
            bytes.CopyTo(output);
            stream.Write(output);
        }

        // The report without its report id (512 bytes); null when nothing came within the limit. HidSharp has no read that does not wait, so the
        // worker's drain reads pass 1 ms and each empty one ends in a TimeoutException here: that costs CPU only.
        public byte[]? Read(int limitMs)
        {
            stream.ReadTimeout=Math.Max(1,limitMs);
            int n;
            try{n=stream.Read(input);}
            catch(TimeoutException){return null;}
            return n>1?input[1..n]:null;
        }

        public void Dispose()=>stream.Dispose();
    }
}

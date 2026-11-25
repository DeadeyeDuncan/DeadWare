using HidSharp;

namespace DeadWare.Devices;

// Lighting 4: the Aerox 9's vendor interface through HidSharp. HidSharp has no interface number, so the interface is the one whose report
// descriptor has a top-level collection with usage page 0xFFC0, usage 1; a device whose descriptor cannot be read is taken by "mi_03" in
// its path. HidSharp puts the report id at [0] of every report, as the driver's packets already do; replies lose it here, so the driver
// sees DeadLight's offsets (a reply starts at the opcode). Not unit-tested: it opens real hardware.
internal sealed class HidAeroxTransport:IAeroxTransport
{
    const uint VendorUsage=0xFFC00001;                    // usage page 0xFFC0, usage 1

    public IReadOnlyCollection<int> Present()=>Devices().Select(d=>d.ProductID).Distinct().ToList();

    public IAeroxPort Open(int product)
    {
        var device=Devices().FirstOrDefault(d=>d.ProductID==product)??throw new IOException($"the Aerox device {product:X4} is not connected");
        return new Port(device.Open(),device.GetMaxInputReportLength(),device.GetMaxOutputReportLength());
    }

    static IEnumerable<HidDevice> Devices()=>DeviceList.Local.GetHidDevices(AeroxDriver.Vendor)
        .Where(d=>(d.ProductID==AeroxDriver.Receiver||d.ProductID==AeroxDriver.Cable)&&VendorInterface(d));

    static bool VendorInterface(HidDevice d)
    {
        try{return d.GetReportDescriptor().DeviceItems.Any(i=>i.Usages.GetAllValues().Contains(VendorUsage));}
        catch{return d.DevicePath.Contains("mi_03",StringComparison.OrdinalIgnoreCase);}
    }

    sealed class Port(HidStream stream,int inLength,int outLength):IAeroxPort
    {
        // The packet's [0] is the report id 0; the rest of the output report stays zero.
        public void Write(ReadOnlySpan<byte> packet)
        {
            var buffer=new byte[outLength];
            packet[..Math.Min(packet.Length,buffer.Length)].CopyTo(buffer);
            stream.Write(buffer);
        }

        // The input report without its report id; null when nothing came within the limit.
        public byte[]? Read(int limitMs)
        {
            var buffer=new byte[inLength];
            int n;
            stream.ReadTimeout=limitMs;
            try{n=stream.Read(buffer);}
            catch(TimeoutException){return null;}
            return n>1?buffer[1..n]:null;
        }

        public void Dispose()=>stream.Dispose();
    }
}

namespace DeadWare.Devices.Tests;

// Lighting 4: the Aerox receiver and cable as the driver sees them. Every port shares the fake's records. Reads hand out Queue first, then
// Reply applied to the last write (default: [0x61], an acknowledgement). HangWrite, when set to an unset event, holds each write after it
// is recorded until the event is set; a test that hangs a write must set the event in its cleanup.
sealed class FakeAeroxTransport:IAeroxTransport
{
    readonly object sync=new();
    readonly List<byte[]> writes=new();
    readonly List<int> opened=new(),limits=new();
    int closes,reads;
    byte[] last=[];
    public HashSet<int> Products=new();
    public Queue<byte[]?> Queue=new();
    public volatile Func<byte[],byte[]?> Reply=_=>[0x61];
    public volatile ManualResetEventSlim? HangWrite;
    public volatile bool ThrowOnWrite;
    public int Opens{get{lock(sync)return opened.Count;}}
    public List<int> Opened{get{lock(sync)return opened.ToList();}}
    public int Closes{get{lock(sync)return closes;}}
    public int Reads{get{lock(sync)return reads;}}
    public List<int> Limits{get{lock(sync)return limits.ToList();}}
    public List<byte[]> Writes{get{lock(sync)return writes.ToList();}}
    public int Count(byte opcode)=>Writes.Count(w=>w[1]==opcode);
    public IReadOnlyCollection<int> Present(){lock(sync)return Products.ToList();}
    public IAeroxPort Open(int product){lock(sync)opened.Add(product);return new Port(this);}

    sealed class Port(FakeAeroxTransport t):IAeroxPort
    {
        bool disposed;
        public void Write(ReadOnlySpan<byte> packet)
        {
            var copy=packet.ToArray();
            lock(t.sync){t.writes.Add(copy);t.last=copy;}
            t.HangWrite?.Wait();
            if(t.ThrowOnWrite) throw new IOException("the device was removed");
        }
        public byte[]? Read(int limitMs)
        {
            byte[] last;
            lock(t.sync)
            {
                t.reads++;t.limits.Add(limitMs);
                if(t.Queue.Count>0) return t.Queue.Dequeue();
                last=t.last;
            }
            return t.Reply(last);
        }
        public void Dispose(){lock(t.sync){if(disposed) return;disposed=true;t.closes++;}}
    }
}

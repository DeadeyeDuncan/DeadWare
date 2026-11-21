using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 3b, the key-press rule: where key presses come from. Start takes the gate itself, not a delegate, so a scan code can go to
// nothing but KeyPressGate.OnKey. Start returns false when the source can't watch; Installed is false from the moment Stop is called.
internal interface IKeyPressSource:IDisposable{bool Installed{get;}bool Start(KeyPressGate gate);void Stop();}

// Turns a key event into "light pressed" records for the keyboards prepared for it, and remembers nothing else but which of those keys are
// held (each with when it was last seen), so a held key counts once and one whose release was missed counts again HeldMs after its last
// repeat. A key with no light on any prepared keyboard is dropped before anything about it is kept. Live presses last LifeMs, at most
// MaxLive of them. One private lock, held only for the bookkeeping; OnKey and the pressed callback never take any lock of the host's.
internal sealed class KeyPressGate
{
    public const int MaxLive=32,LifeMs=3000,HeldMs=2000;
    readonly Func<double> nowMs;
    readonly Action? pressed;
    readonly object sync=new();
    readonly Dictionary<(int Scan,bool Extended),double> held=new();
    readonly List<LightPress> live=new();
    IReadOnlyList<(string DeviceKey,IReadOnlyList<string> LightNames)> keyboards=[];
    // generation: changes with every Prepare, so a lookup made against an older table adds nothing.
    int generation;
    public KeyPressGate(Func<double> nowMs,Action? pressed=null){this.nowMs=nowMs;this.pressed=pressed;}

    // Replaces the table. Held keys that have no light in the new one are forgotten (a key with no light is never remembered), and so are
    // presses for keyboards no longer in it; an empty table forgets every press and held key.
    public void Prepare(IReadOnlyList<(string DeviceKey,IReadOnlyList<string> LightNames)> keyboards)
    {
        var next=keyboards.Select(k=>(k.DeviceKey,(IReadOnlyList<string>)k.LightNames.ToArray())).ToArray();
        lock(sync)
        {
            this.keyboards=next;generation++;
            if(next.Length==0){held.Clear();live.Clear();return;}
            foreach(var k in held.Keys.Where(k=>!next.Any(b=>KeyPressTable.LightsFor(b.Item2,k.Scan,k.Extended).Count>0)).ToList()) held.Remove(k);
            live.RemoveAll(p=>!next.Any(b=>b.DeviceKey==p.DeviceKey));
        }
    }

    public void OnKey(int scan,bool extended,bool sentBySoftware,bool up)
    {
        if(sentBySoftware) return;
        try
        {
            IReadOnlyList<(string DeviceKey,IReadOnlyList<string> LightNames)> table;int seen;
            lock(sync){table=keyboards;seen=generation;}
            List<(string,IReadOnlyList<int>)>? found=null;
            foreach(var (key,names) in table){var lights=KeyPressTable.LightsFor(names,scan,extended);if(lights.Count>0)(found??=new()).Add((key,lights));}
            if(found is null) return;
            lock(sync)
            {
                if(seen!=generation) return;
                var id=(scan,extended);
                if(up){held.Remove(id);return;}
                var now=nowMs();
                if(held.TryGetValue(id,out var last)&&now-last<HeldMs){held[id]=now;return;}
                held[id]=now;
                foreach(var (key,lights) in found) live.Add(new(key,lights,now));
                Trim(now);
            }
            try{pressed?.Invoke();}catch{}
        }
        catch{}
    }

    void Trim(double now)
    {
        live.RemoveAll(p=>now-p.AtMs>=LifeMs);
        if(live.Count>MaxLive) live.RemoveRange(0,live.Count-MaxLive);
    }

    // Presses younger than LifeMs, at most the newest MaxLive, oldest first.
    public IReadOnlyList<LightPress> Live()
    {
        lock(sync)
        {
            if(live.Count==0) return [];
            Trim(nowMs());
            return live.ToArray();
        }
    }
    public void Released(){lock(sync)held.Clear();}
    public void Clear(){lock(sync){held.Clear();live.Clear();}}
}

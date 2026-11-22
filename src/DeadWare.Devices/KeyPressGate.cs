using System.Collections.ObjectModel;
using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 3b, the key-press rule: where key presses come from. Start takes the gate itself, not a delegate, so a scan code can go to
// nothing but KeyPressGate.OnKey. Start returns false (or throws, with Windows' reason) when the source can't watch; Installed is false
// from the moment Stop is called.
internal interface IKeyPressSource:IDisposable{bool Installed{get;}bool Start(KeyPressGate gate);void Stop();}

// Turns a key event into "light pressed" records for the keyboards prepared for it, and remembers nothing else but which of those keys are
// held (each with when it was last seen), so a held key counts once and one whose release was missed counts again HeldMs after its last
// repeat; a held key not seen for HeldMs is forgotten. A key with no light on any prepared keyboard is dropped before anything about it
// is kept. Live presses last LifeMs, at most MaxLive of them. The table is built once, in Prepare (spec point 2, "a table prepared
// beforehand"): OnKey does one dictionary lookup, with no name matching and no allocation for a key that has no light. One private lock,
// held only for that lookup and the bookkeeping; OnKey and the pressed callback never take any lock of the host's.
internal sealed class KeyPressGate
{
    public const int MaxLive=32,LifeMs=3000,HeldMs=2000;
    readonly Func<double> nowMs;
    readonly Action? pressed;
    readonly object sync=new();
    readonly Dictionary<(int Scan,bool Extended),double> held=new();
    readonly List<LightPress> live=new();
    // keyboards: what the table was built from, to tell an unchanged table. table: (scan, extended) to the lights it drives on each keyboard.
    (string DeviceKey,string[] LightNames)[] keyboards=[];
    Dictionary<(int Scan,bool Extended),(string DeviceKey,ReadOnlyCollection<int> Lights)[]> table=new();
    public KeyPressGate(Func<double> nowMs,Action? pressed=null){this.nowMs=nowMs;this.pressed=pressed;}

    // Replaces the table. The same keyboards with the same names in the same order change nothing, so a state raised while someone types
    // drops no key event and forgets no held key. Otherwise held keys that have no light in the new table are forgotten (a key with no light
    // is never remembered), and so are presses for keyboards no longer in it or whose light names changed (wbr-1 D3: their light numbers
    // are the old order's); an empty table forgets every press and held key.
    public void Prepare(IReadOnlyList<(string DeviceKey,IReadOnlyList<string> LightNames)> keyboards)
    {
        var next=keyboards.Select(k=>(k.DeviceKey,k.LightNames.ToArray())).ToArray();
        lock(sync)if(Same(next,this.keyboards)) return;
        var built=Build(next);
        lock(sync)
        {
            var old=this.keyboards;
            this.keyboards=next;table=built;
            if(next.Length==0){held.Clear();live.Clear();return;}
            foreach(var k in held.Keys.Where(k=>!built.ContainsKey(k)).ToList()) held.Remove(k);
            live.RemoveAll(p=>!(Names(next,p.DeviceKey) is{} now&&Names(old,p.DeviceKey) is{} was&&now.AsSpan().SequenceEqual(was)));
        }
    }
    static string[]? Names((string DeviceKey,string[] LightNames)[] keyboards,string key){foreach(var k in keyboards) if(k.DeviceKey==key) return k.LightNames;return null;}
    static bool Same((string DeviceKey,string[] LightNames)[] a,(string DeviceKey,string[] LightNames)[] b)
        =>a.Length==b.Length&&a.Zip(b).All(x=>x.First.DeviceKey==x.Second.DeviceKey&&x.First.LightNames.AsSpan().SequenceEqual(x.Second.LightNames));
    // Every scan code some light is named for, looked up both plain and extended through KeyPressTable.LightsFor, so the table matches
    // exactly what LightsFor would give for that key.
    static Dictionary<(int,bool),(string,ReadOnlyCollection<int>)[]> Build((string DeviceKey,string[] LightNames)[] keyboards)
    {
        var r=new Dictionary<(int,bool),List<(string,ReadOnlyCollection<int>)>>();
        foreach(var (key,names) in keyboards)
            foreach(var scan in names.Select(n=>KeyPressTable.CodeFor(n)?.Scan).OfType<int>().Distinct())
                foreach(var ext in new[]{false,true})
                    if(KeyPressTable.LightsFor(names,scan,ext) is{Count:>0} lights)
                        (r.TryGetValue((scan,ext),out var l)?l:r[(scan,ext)]=new()).Add((key,lights.ToArray().AsReadOnly()));
        return r.ToDictionary(e=>e.Key,e=>e.Value.ToArray());
    }

    public void OnKey(int scan,bool extended,bool sentBySoftware,bool up)
    {
        if(sentBySoftware) return;
        try
        {
            lock(sync)
            {
                if(!table.TryGetValue((scan,extended),out var found)) return;
                var id=(scan,extended);
                var now=nowMs();
                Forget(now);
                if(up){held.Remove(id);return;}
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
        Forget(now);
    }
    // wbr-1 D9: a held key not seen for HeldMs is forgotten (it would count again anyway), so a missed release is kept no longer than
    // that. Removing while enumerating a Dictionary is allowed and allocates nothing, which the hook's thread needs.
    void Forget(double now)
    {
        foreach(var (k,last) in held) if(now-last>=HeldMs) held.Remove(k);
    }

    // Presses younger than LifeMs, at most the newest MaxLive, oldest first. Every read also forgets stale held keys, so the paint loop,
    // which reads on every tick, ages them out with no key event.
    public IReadOnlyList<LightPress> Live()
    {
        lock(sync)
        {
            if(live.Count==0&&held.Count==0) return [];
            Trim(nowMs());
            return live.Count==0?[]:live.ToArray();
        }
    }
    // How many keys are remembered as held; for tests.
    internal int HeldCount{get{lock(sync)return held.Count;}}
    public void Released(){lock(sync)held.Clear();}
    public void Clear(){lock(sync){held.Clear();live.Clear();}}
}

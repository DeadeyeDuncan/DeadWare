namespace DeadWare.Core;

// A key's place in key units (a standard key is 1 high): which light, where, how big, and the short text drawn on it.
public sealed record KeyShape(int Light,double X,double Y,double W,double H,string Label);
// Neighbouring shapes touch: any gap is applied by whoever draws them. Standard is true for the built-in full-size layout.
public sealed record KeyShapeSet(double Width,double Height,IReadOnlyList<KeyShape> Keys,bool Standard);

// The built-in standard full-size keyboard layout, matched to a keyboard's lights by name (the names and spellings KeyPressTable knows).
public static class KeyShapes
{
    private const double LayoutWidth=22.5,LayoutHeight=6.25,ExtraY=6.75;
    private const int MinStandardKeys=8;   // fewer recognised lights than this is not a full-size board, whatever they are called
    private const string RightSlot="rslot";

    private static readonly Dictionary<string,string> Short=new(StringComparer.Ordinal)
    {
        ["escape"]="Esc",["backspace"]="Bksp",["caps lock"]="Caps",["left shift"]="Shift",["right shift"]="Shift",["left control"]="Ctrl",["right control"]="Ctrl",
        ["left ctrl"]="Ctrl",["right ctrl"]="Ctrl",["left windows"]="Win",["right windows"]="Win",["left alt"]="Alt",["right alt"]="Alt",["print screen"]="PrtSc",
        ["scroll lock"]="ScrLk",["pause/break"]="Pause",["pause break"]="Pause",["insert"]="Ins",["delete"]="Del",["page up"]="PgUp",["page down"]="PgDn",
        ["up arrow"]="↑",["down arrow"]="↓",["left arrow"]="←",["right arrow"]="→",["number pad enter"]="Ent",["num lock"]="Num",["\\ (ansi)"]="\\",["\\ (iso)"]="\\",
    };

    private static string Id(int scan,bool ext=false)=>ext?scan.ToString("X2")+"e":scan.ToString("X2");

    // The slot a light's name belongs to: its scan code and extended flag, or the right-hand Windows/Fn slot. Null when the name is not in the layout.
    private static string? SlotOf(string name)
    {
        var n=KeyPressTable.Normalize(name);
        if(n.Length==0) return null;
        if(n=="right fn"||n=="fn") return RightSlot;
        if(KeyPressTable.CodeFor(n) is not var (scan,ext)) return null;
        return scan==0x5C&&ext?RightSlot:Id(scan,ext);
    }

    private static Dictionary<string,(double X,double Y,double W,double H)> Layout(bool iso)
    {
        var d=new Dictionary<string,(double X,double Y,double W,double H)>();
        void Put(string id,double x,double y,double w=1,double h=1)=>d[id]=(x,y,w,h);
        const double R0=0,R1=1.25,R2=2.25,R3=3.25,R4=4.25,R5=5.25;
        Put(Id(0x01),0,R0);
        for(var i=0;i<12;i++) Put(Id(i<10?0x3B+i:0x57+i-10),(i/4==0?2:i/4==1?6.5:11)+i%4,R0);
        Put(Id(0x37,true),15.25,R0);Put(Id(0x46),16.25,R0);Put(Id(0x45),17.25,R0);
        Put(Id(0x29),0,R1);for(var i=0;i<10;i++) Put(Id(i<9?2+i:0x0B),1+i,R1);Put(Id(0x0C),11,R1);Put(Id(0x0D),12,R1);Put(Id(0x0E),13,R1,2);
        Put(Id(0x52,true),15.25,R1);Put(Id(0x47,true),16.25,R1);Put(Id(0x49,true),17.25,R1);
        Put(Id(0x45,true),18.5,R1);Put(Id(0x35,true),19.5,R1);Put(Id(0x37),20.5,R1);Put(Id(0x4A),21.5,R1);
        Put(Id(0x0F),0,R2,1.5);
        var top=new[]{0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x1B};
        for(var i=0;i<top.Length;i++) Put(Id(top[i]),1.5+i,R2);
        if(!iso) Put(Id(0x2B),13.5,R2,1.5);
        Put(Id(0x53,true),15.25,R2);Put(Id(0x4F,true),16.25,R2);Put(Id(0x51,true),17.25,R2);
        Put(Id(0x47),18.5,R2);Put(Id(0x48),19.5,R2);Put(Id(0x49),20.5,R2);Put(Id(0x4E),21.5,R2,1,2);
        Put(Id(0x3A),0,R3,1.75);
        var mid=new[]{0x1E,0x1F,0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28};
        for(var i=0;i<mid.Length;i++) Put(Id(mid[i]),1.75+i,R3);
        if(iso){Put(Id(0x2B),12.75,R3);Put(Id(0x1C),13.75,R3,1.25);}else Put(Id(0x1C),12.75,R3,2.25);
        Put(Id(0x4B),18.5,R3);Put(Id(0x4C),19.5,R3);Put(Id(0x4D),20.5,R3);
        if(iso){Put(Id(0x2A),0,R4,1.25);Put(Id(0x56),1.25,R4);}else Put(Id(0x2A),0,R4,2.25);
        var bot=new[]{0x2C,0x2D,0x2E,0x2F,0x30,0x31,0x32,0x33,0x34,0x35};
        for(var i=0;i<bot.Length;i++) Put(Id(bot[i]),2.25+i,R4);
        Put(Id(0x36),12.25,R4,2.75);
        Put(Id(0x48,true),16.25,R4);
        Put(Id(0x4F),18.5,R4);Put(Id(0x50),19.5,R4);Put(Id(0x51),20.5,R4);Put(Id(0x1C,true),21.5,R4,1,2);
        Put(Id(0x1D),0,R5,1.25);Put(Id(0x5B,true),1.25,R5,1.25);Put(Id(0x38),2.5,R5,1.25);Put(Id(0x39),3.75,R5,6.25);
        Put(Id(0x38,true),10,R5,1.25);Put(RightSlot,11.25,R5,1.25);Put(Id(0x5D,true),12.5,R5,1.25);Put(Id(0x1D,true),13.75,R5,1.25);
        Put(Id(0x4B,true),15.25,R5);Put(Id(0x50,true),16.25,R5);Put(Id(0x4D,true),17.25,R5);
        Put(Id(0x52),18.5,R5,2);Put(Id(0x53),20.5,R5);
        return d;
    }

    public static KeyShapeSet? Of(KeyMapView map)
    {
        var names=map.Names;
        var iso=names.Any(n=>{var k=KeyPressTable.Normalize(n);return k=="\\ (iso)"||k=="#";});
        var slots=Layout(iso);
        var slotOf=new string?[names.Count];var known=0;
        for(var i=0;i<names.Count;i++){var s=SlotOf(names[i]);if(s is not null&&slots.ContainsKey(s)){slotOf[i]=s;known++;}}
        if(known>=MinStandardKeys&&known*2>=names.Count) return Standard(map,slots,slotOf);
        return FromPositions(map);
    }

    private static KeyShapeSet Standard(KeyMapView map,Dictionary<string,(double X,double Y,double W,double H)> slots,string?[] slotOf)
    {
        var keys=new List<KeyShape>();var taken=new HashSet<string>(StringComparer.Ordinal);var extra=new List<int>();
        for(var i=0;i<slotOf.Length;i++)
        {
            if(slotOf[i] is string s&&taken.Add(s)){var r=slots[s];keys.Add(new(i,r.X,r.Y,r.W,r.H,Label(map.Names[i])));}
            else if(KeyPressTable.Normalize(map.Names[i]).Length>0) extra.Add(i);
        }
        double x=0,y=ExtraY;
        foreach(var i in extra)
        {
            if(x+1>LayoutWidth+1e-9){x=0;y+=1;}
            keys.Add(new(i,x,y,1,1,Label(map.Names[i])));x+=1;
        }
        keys.Sort((a,b)=>a.Light.CompareTo(b.Light));
        return new(LayoutWidth,extra.Count==0?LayoutHeight:y+1,keys,true);
    }

    // The geometry DeskPictures.KeyRects uses for unnamed maps, in key units: a key is 1 wide and 1 high, rows 1.25 apart, the board as wide as one key over the map's key width.
    private static KeyShapeSet? FromPositions(KeyMapView map)
    {
        int n=Math.Min(map.Names.Count,Math.Min(map.X.Count,map.Y.Count));
        bool Has(int i)=>map.X[i] is double x&&map.Y[i] is double y&&double.IsFinite(x)&&double.IsFinite(y);
        var rows=new HashSet<double>();
        for(var i=0;i<n;i++) if(Has(i)) rows.Add(map.Y[i]!.Value);
        if(rows.Count==0) return null;
        double kw=double.IsFinite(map.KeyWidth)?Math.Max(0.01,map.KeyWidth):0.01,width=Math.Max(1,1/kw),height=Math.Max(1,rows.Count*1.25);
        var keys=new List<KeyShape>();
        for(var i=0;i<n;i++)
        {
            if(!Has(i)) continue;
            double cx=0.5+map.X[i]!.Value*(width-1),cy=0.5+map.Y[i]!.Value*(height-1);
            keys.Add(new(i,Math.Clamp(cx-0.5,0,width-1),Math.Clamp(cy-0.5,0,height-1),1,1,Label(map.Names[i])));
        }
        return new(width,height,keys,false);
    }

    public static string Label(string lightName)
    {
        var t=(lightName??"").Trim();
        if(t.StartsWith("key:",StringComparison.OrdinalIgnoreCase)) t=t[4..].Trim();
        var n=t.ToLowerInvariant();
        if(Short.TryGetValue(n,out var s)) return s;
        foreach(var p in new[]{"number pad ","numpad ","num "}) if(n.StartsWith(p,StringComparison.Ordinal)&&n.Length>p.Length) return t[p.Length..];
        return t.Length>5?t[..5]:t;
    }

    public static int? Hit(KeyShapeSet set,double x,double y)
    {
        foreach(var k in set.Keys) if(x>=k.X&&x<k.X+k.W&&y>=k.Y&&y<k.Y+k.H) return k.Light;
        return null;
    }

    public static IReadOnlyList<int> Group(KeyMapView map,string group)
    {
        if(group=="all") return Of(map)?.Keys.Select(k=>k.Light).ToList()??[];
        var r=new List<int>();
        for(var i=0;i<map.Names.Count;i++)
        {
            var n=KeyPressTable.Normalize(map.Names[i]);
            bool inGroup;
            if(group=="wasd") inGroup=n is "w" or "a" or "s" or "d";
            else if(KeyPressTable.CodeFor(n) is var (scan,ext))
                inGroup=group switch
                {
                    "arrows"=>ext&&scan is 0x48 or 0x4B or 0x4D or 0x50,
                    "numbers"=>!ext&&scan>=0x02&&scan<=0x0B,
                    "fkeys"=>!ext&&(scan>=0x3B&&scan<=0x44||scan is 0x57 or 0x58),
                    "numpad"=>ext?scan is 0x1C or 0x35 or 0x45:scan is 0x52 or 0x4F or 0x50 or 0x51 or 0x4B or 0x4C or 0x4D or 0x47 or 0x48 or 0x49 or 0x53 or 0x4A or 0x4E or 0x37,
                    _=>false,
                };
            else inGroup=false;
            if(inGroup) r.Add(i);
        }
        return r;
    }
}

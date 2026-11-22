namespace DeadWare.Core;

// From a key's Windows set-1 scan code (plus the extended flag) to the light names OpenRGB gives keyboard lights. Scan codes only:
// the table never sees what a key types.
public static class KeyPressTable
{
    private static readonly Dictionary<string,(int Scan,bool? Extended)> Names=Build();

    // Trimmed, ignoring case, without a leading "Key: ". Key shapes use the same matching.
    public static string Normalize(string name)
    {
        var n=(name??"").Trim().ToLowerInvariant();
        if(n.StartsWith("key:",StringComparison.Ordinal)) n=n[4..].Trim();
        return n;
    }

    public static (int Scan,bool Extended)? CodeFor(string lightName)
        => Names.TryGetValue(Normalize(lightName),out var c)?(c.Scan,c.Extended??false):null;

    public static IReadOnlyList<int> LightsFor(IReadOnlyList<string> lightNames,int scan,bool extended)
    {
        var r=new List<int>();
        for(var i=0;i<lightNames.Count;i++)
            if(Names.TryGetValue(Normalize(lightNames[i]),out var c)&&c.Scan==scan&&(c.Extended is null||c.Extended==extended)) r.Add(i);
        return r;
    }

    public static IReadOnlyList<string> Unrecognised(IReadOnlyList<string> lightNames)
        => lightNames.Where(n=>!Names.ContainsKey(Normalize(n))).ToList();

    private static Dictionary<string,(int,bool?)> Build()
    {
        var d=new Dictionary<string,(int,bool?)>();
        void Add(int scan,bool? ext,params string[] names){foreach(var n in names) d[n.ToLowerInvariant()]=(scan,ext);}
        var letters=new (string,int)[]{("A",0x1E),("B",0x30),("C",0x2E),("D",0x20),("E",0x12),("F",0x21),("G",0x22),("H",0x23),("I",0x17),("J",0x24),("K",0x25),("L",0x26),("M",0x32),
            ("N",0x31),("O",0x18),("P",0x19),("Q",0x10),("R",0x13),("S",0x1F),("T",0x14),("U",0x16),("V",0x2F),("W",0x11),("X",0x2D),("Y",0x15),("Z",0x2C)};
        foreach(var (n,s) in letters) Add(s,false,n);
        for(var i=1;i<=9;i++) Add(i+1,false,i.ToString());
        Add(0x0B,false,"0");
        for(var i=1;i<=12;i++) Add(i<=10?0x3A+i:0x57+i-11,false,"F"+i);
        Add(0x01,false,"Escape","Esc");Add(0x29,false,"Backquote","Grave","`");
        Add(0x0C,false,"Minus","-");Add(0x0D,false,"Equals","=");Add(0x0E,false,"Backspace");
        Add(0x0F,false,"Tab");Add(0x3A,false,"Caps Lock","CapsLock");Add(0x1C,false,"Enter","Return");
        Add(0x1A,false,"Left Bracket","[");Add(0x1B,false,"Right Bracket","]");Add(0x2B,false,"Backslash","\\","\\ (ANSI)");
        Add(0x56,false,"\\ (ISO)");
        Add(0x27,false,";","Semicolon");Add(0x28,false,"Apostrophe","Quote","'");
        Add(0x33,false,"Comma",",");Add(0x34,false,"Period",".");Add(0x35,false,"Slash","/");
        Add(0x39,false,"Space","Spacebar");Add(0x2A,false,"Left Shift");Add(0x36,null,"Right Shift");
        Add(0x1D,false,"Left Control","Left Ctrl");Add(0x1D,true,"Right Control","Right Ctrl");
        Add(0x38,false,"Left Alt");Add(0x38,true,"Right Alt");
        Add(0x5B,true,"Left Windows","Left Win","Left Meta");Add(0x5C,true,"Right Windows","Right Win","Right Meta");
        Add(0x5D,true,"Menu","Application");Add(0x37,true,"Print Screen");Add(0x46,false,"Scroll Lock");Add(0x45,false,"Pause","Pause Break","Pause/Break");
        Add(0x52,true,"Insert");Add(0x53,true,"Delete");Add(0x47,true,"Home");Add(0x4F,true,"End");
        Add(0x49,true,"Page Up");Add(0x51,true,"Page Down");
        Add(0x48,true,"Up","Up Arrow");Add(0x4B,true,"Left","Left Arrow");Add(0x4D,true,"Right","Right Arrow");Add(0x50,true,"Down","Down Arrow");
        Add(0x45,true,"Num Lock");Add(0x35,true,"Num /","Numpad Divide","Number Pad /");Add(0x37,false,"Num *","Numpad Multiply","Number Pad *");
        Add(0x4A,false,"Num -","Numpad Subtract","Number Pad -");Add(0x4E,false,"Num +","Numpad Add","Number Pad +");Add(0x1C,true,"Num Enter","Numpad Enter","Number Pad Enter");
        var pad=new (int,int)[]{(0,0x52),(1,0x4F),(2,0x50),(3,0x51),(4,0x4B),(5,0x4C),(6,0x4D),(7,0x47),(8,0x48),(9,0x49)};
        foreach(var (digit,s) in pad) Add(s,false,$"Num {digit}",$"Numpad {digit}",$"Number Pad {digit}");
        Add(0x53,false,"Num .","Numpad Decimal","Number Pad .");
        // "#" is the ISO key beside Enter: the same position as ANSI backslash, and a keyboard has only one of the two.
        Add(0x2B,false,"#");
        return d;
    }
}

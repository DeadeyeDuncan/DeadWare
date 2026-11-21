using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class KeyShapesTests
{
    static readonly string[] Ansi=
    [
        "Key: Escape",..Enumerable.Range(1,12).Select(i=>"Key: F"+i),"Key: Print Screen","Key: Scroll Lock","Key: Pause/Break",
        "Key: `",..Enumerable.Range(1,9).Select(i=>"Key: "+i),"Key: 0","Key: -","Key: =","Key: Backspace","Key: Insert","Key: Home","Key: Page Up",
        "Key: Num Lock","Key: Number Pad /","Key: Number Pad *","Key: Number Pad -",
        "Key: Tab",..("QWERTYUIOP".Select(c=>"Key: "+c)),"Key: [","Key: ]","Key: \\ (ANSI)","Key: Delete","Key: End","Key: Page Down",
        "Key: Number Pad 7","Key: Number Pad 8","Key: Number Pad 9","Key: Number Pad +",
        "Key: Caps Lock",..("ASDFGHJKL".Select(c=>"Key: "+c)),"Key: ;","Key: '","Key: Enter","Key: Number Pad 4","Key: Number Pad 5","Key: Number Pad 6",
        "Key: Left Shift",..("ZXCVBNM".Select(c=>"Key: "+c)),"Key: ,","Key: .","Key: /","Key: Right Shift","Key: Up Arrow",
        "Key: Number Pad 1","Key: Number Pad 2","Key: Number Pad 3","Key: Number Pad Enter",
        "Key: Left Control","Key: Left Windows","Key: Left Alt","Key: Space","Key: Right Alt","Key: Right Windows","Key: Menu","Key: Right Control",
        "Key: Left Arrow","Key: Down Arrow","Key: Right Arrow","Key: Number Pad 0","Key: Number Pad .",
    ];
    static KeyMapView Named(IEnumerable<string> names){var l=names.ToList();return new(l,l.Select(_=>(double?)null).ToList(),l.Select(_=>(double?)null).ToList(),0.05);}
    static KeyShape Of(KeyShapeSet s,KeyMapView m,string name)=>s.Keys.Single(k=>k.Light==m.Names.ToList().IndexOf(name));
    static bool Overlap(KeyShape a,KeyShape b)=>a.X<b.X+b.W-1e-9&&b.X<a.X+a.W-1e-9&&a.Y<b.Y+b.H-1e-9&&b.Y<a.Y+a.H-1e-9;

    [Fact] public void A_full_ANSI_board_is_standard_and_no_keys_overlap()
    {
        var m=Named(Ansi);Assert.Equal(104,Ansi.Length);
        var s=KeyShapes.Of(m)!;
        Assert.True(s.Standard);Assert.Equal(104,s.Keys.Count);Assert.Equal(22.5,s.Width);Assert.Equal(6.25,s.Height);
        for(var i=0;i<s.Keys.Count;i++) for(var j=i+1;j<s.Keys.Count;j++)
            Assert.False(Overlap(s.Keys[i],s.Keys[j]),$"{m.Names[s.Keys[i].Light]} overlaps {m.Names[s.Keys[j].Light]}");
        foreach(var k in s.Keys) Assert.True(k.X>=0&&k.Y>=0&&k.X+k.W<=22.5+1e-9&&k.Y+k.H<=6.25+1e-9,m.Names[k.Light]);
        Assert.Equal(6.25,Of(s,m,"Key: Space").W);
        Assert.Equal(2,Of(s,m,"Key: Number Pad +").H);Assert.Equal(2,Of(s,m,"Key: Number Pad Enter").H);
        Assert.Equal(1,Of(s,m,"Key: A").H);Assert.Equal(2,Of(s,m,"Key: Backspace").W);Assert.Equal(2.25,Of(s,m,"Key: Enter").W);
    }
    [Fact] public void An_ISO_board_has_the_short_shift_and_the_extra_key()
    {
        var names=Ansi.Where(n=>n!="Key: \\ (ANSI)").Concat(["Key: \\ (ISO)","Key: #"]).ToList();
        var m=Named(names);var s=KeyShapes.Of(m)!;
        Assert.True(s.Standard);Assert.Equal(names.Count,s.Keys.Count);
        Assert.Equal(1.25,Of(s,m,"Key: Left Shift").W);
        var iso=Of(s,m,"Key: \\ (ISO)");Assert.Equal(1,iso.W);Assert.Equal(1.25,iso.X);
        Assert.Equal(1,Of(s,m,"Key: #").W);Assert.Equal(1.25,Of(s,m,"Key: Enter").W);
        for(var i=0;i<s.Keys.Count;i++) for(var j=i+1;j<s.Keys.Count;j++)
            Assert.False(Overlap(s.Keys[i],s.Keys[j]),$"{m.Names[s.Keys[i].Light]} overlaps {m.Names[s.Keys[j].Light]}");
    }
    [Fact] public void Unknown_named_lights_go_in_a_row_beneath()
    {
        var names=Ansi.Concat(["Logo","Side 1","","Side 2"]).ToList();
        var m=Named(names);var s=KeyShapes.Of(m)!;
        Assert.True(s.Standard);Assert.Equal(104+3,s.Keys.Count);   // the unnamed light is left out
        var logo=s.Keys.Single(k=>k.Light==104);var side1=s.Keys.Single(k=>k.Light==105);var side2=s.Keys.Single(k=>k.Light==107);
        Assert.Equal((0.0,6.75,1.0,1.0),(logo.X,logo.Y,logo.W,logo.H));Assert.Equal(1,side1.X);Assert.Equal(2,side2.X);Assert.Equal(6.75,side2.Y);
        Assert.DoesNotContain(s.Keys,k=>k.Light==106);Assert.Equal(7.75,s.Height);
        // a second taker of the Right Windows/Fn slot goes beneath too
        var two=Named(Ansi.Concat(["Key: Right Fn"]));var t=KeyShapes.Of(two)!;
        Assert.Equal(6.75,t.Keys.Single(k=>k.Light==104).Y);
        Assert.Equal(11.25,t.Keys.Single(k=>two.Names[k.Light]=="Key: Right Windows").X);
        // a long extra row wraps at the set width
        var many=Named(Ansi.Concat(Enumerable.Range(0,25).Select(i=>"Extra "+i)));var w=KeyShapes.Of(many)!;
        var wrapped=w.Keys.Single(k=>k.Light==104+22);
        Assert.Equal(7.75,wrapped.Y);Assert.Equal(0,wrapped.X);
    }
    [Fact] public void Fewer_than_half_recognised_falls_back_to_positions()
    {
        var m=new KeyMapView(["Key: A","Foo","Bar","Baz"],[0.0,1.0,0.0,1.0],[0.0,0.0,1.0,1.0],0.25);
        var s=KeyShapes.Of(m)!;
        Assert.False(s.Standard);Assert.Equal(4,s.Keys.Count);Assert.Equal(4,s.Width);
        Assert.True(s.Keys[1].X>s.Keys[0].X&&s.Keys[2].Y>s.Keys[0].Y);
        Assert.False(KeyShapes.Of(new KeyMapView(["Key: A","Key: B","Foo","Bar","Baz"],[0.0,1.0,0.0,1.0,0.5],[0.0,0.0,1.0,1.0,0.5],0.25))!.Standard);
        string[] eight=["Key: A","Key: B","Key: C","Key: D","Key: E","Key: F","Key: G","Key: H"];
        var half=eight.Concat(["Foo","Bar","Baz","Qux","Quux","Corge","Grault","Garply"]).ToList();   // exactly half of 16 is enough
        Assert.True(KeyShapes.Of(new KeyMapView(half,half.Select(_=>(double?)0.5).ToList(),half.Select(_=>(double?)0.5).ToList(),0.05))!.Standard);
    }
    [Fact] public void A_tiny_named_board_is_not_standard()
    {
        var m=new KeyMapView(["a","b","c","d"],[0.0,1.0,0.0,1.0],[0.0,0.0,1.0,1.0],0.25);
        Assert.False(KeyShapes.Of(m)!.Standard);
        var seven=Named(Ansi.Take(7));Assert.Null(KeyShapes.Of(seven));              // no positions, so no shapes at all
        Assert.True(KeyShapes.Of(Named(Ansi.Take(8)))!.Standard);                    // eight recognised lights is enough
    }
    [Fact] public void Unnamed_lights_fall_back_to_positions()
    {
        var m=new KeyMapView(["","",""],[0.0,0.5,1.0],[0.0,0.5,1.0],0.1);
        var s=KeyShapes.Of(m)!;Assert.False(s.Standard);Assert.Equal(3,s.Keys.Count);
    }
    [Fact] public void No_positions_and_no_names_gives_null()
    {
        Assert.Null(KeyShapes.Of(new KeyMapView(["",""],[null,null],[null,null],0.1)));
        Assert.Null(KeyShapes.Of(new KeyMapView([],[],[],0.1)));
        Assert.Null(KeyShapes.Of(new KeyMapView(["Foo"],[null],[null],0.1)));
    }
    [Fact] public void Labels()
    {
        var t=new (string,string)[]{("Key: Escape","Esc"),("Backspace","Bksp"),("Caps Lock","Caps"),("Left Shift","Shift"),("Right Shift","Shift"),("Left Control","Ctrl"),("Right Control","Ctrl"),
            ("Left Windows","Win"),("Right Windows","Win"),("Left Alt","Alt"),("Right Alt","Alt"),("Print Screen","PrtSc"),("Scroll Lock","ScrLk"),("Pause/Break","Pause"),("Insert","Ins"),("Delete","Del"),
            ("Page Up","PgUp"),("Page Down","PgDn"),("Up Arrow","↑"),("Down Arrow","↓"),("Left Arrow","←"),("Right Arrow","→"),("Number Pad 7","7"),("Number Pad .","."),("Number Pad Enter","Ent"),
            ("Num Lock","Num"),("\\ (ANSI)","\\"),("\\ (ISO)","\\"),("Key: A","A"),("Space","Space"),("Enter","Enter"),("Tab","Tab"),("Unknown12","Unkno"),("Key: Abcdefghi","Abcde")};
        foreach(var (n,l) in t) Assert.Equal(l,KeyShapes.Label(n));
    }
    [Fact] public void Hit_finds_the_key_and_misses_outside_the_board_and_between_blocks()
    {
        var m=Named(Ansi);var s=KeyShapes.Of(m)!;
        int Idx(string n)=>m.Names.ToList().IndexOf(n);
        Assert.Equal(Idx("Key: Escape"),KeyShapes.Hit(s,0.5,0.5));
        Assert.Equal(Idx("Key: Space"),KeyShapes.Hit(s,6,5.75));
        Assert.Equal(Idx("Key: Number Pad +"),KeyShapes.Hit(s,22,3.2));
        Assert.Equal(Idx("Key: A"),KeyShapes.Hit(s,2.0,3.75));
        Assert.Null(KeyShapes.Hit(s,-0.1,0.5));Assert.Null(KeyShapes.Hit(s,23,1));Assert.Null(KeyShapes.Hit(s,5,7));
        Assert.Null(KeyShapes.Hit(s,1.0,0.5));     // the right edge belongs to no key
        Assert.Null(KeyShapes.Hit(s,15.1,1.75));   // gap between the main and navigation blocks
        Assert.Null(KeyShapes.Hit(s,18.4,1.75));   // between navigation and number pad
        Assert.Null(KeyShapes.Hit(s,0.5,1.1));     // between the function row and the number row
    }
    [Fact] public void Groups()
    {
        var m=Named(Ansi);int Idx(string n)=>m.Names.ToList().IndexOf(n);
        Assert.Equal(new[]{"Key: W","Key: A","Key: S","Key: D"}.Select(Idx).Order(),KeyShapes.Group(m,"wasd"));
        Assert.Equal(new[]{"Key: Up Arrow","Key: Left Arrow","Key: Down Arrow","Key: Right Arrow"}.Select(Idx).Order(),KeyShapes.Group(m,"arrows"));
        Assert.Equal(new[]{"1","2","3","4","5","6","7","8","9","0"}.Select(d=>Idx("Key: "+d)).Order(),KeyShapes.Group(m,"numbers"));
        Assert.Equal(Enumerable.Range(1,12).Select(i=>Idx("Key: F"+i)).Order(),KeyShapes.Group(m,"fkeys"));
        var pad=KeyShapes.Group(m,"numpad");Assert.Equal(17,pad.Count);
        Assert.All(pad,i=>Assert.True(m.Names[i].Contains("Number Pad")||m.Names[i]=="Key: Num Lock"));
        Assert.Equal(104,KeyShapes.Group(m,"all").Count);
        var noPad=Named(Ansi.Where(n=>!n.Contains("Number Pad")&&!n.Contains("Num Lock")));
        Assert.Empty(KeyShapes.Group(noPad,"numpad"));Assert.Equal(4,KeyShapes.Group(noPad,"arrows").Count);
        Assert.Empty(KeyShapes.Group(m,"nonsense"));
    }
}

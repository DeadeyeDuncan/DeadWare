using System.Windows;
using System.Windows.Media;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

internal static class RuneTraceUiChecks
{
    internal static void Run()
    {
        // Separate figures are actual missing-reading/session gaps, never a path to bridge.
        IReadOnlyList<IReadOnlyList<Point>> segments=[new Point[]{new(0,30),new(90,60)},new Point[]{new(190,55),new(320,25)}];
        var plot=new Rect(0,0,320,90);
        var positions=RuneTrace.Placements(segments,plot);
        Assert.NotEmpty(positions);
        Assert.All(positions,p=>
        {
            Assert.True(p.X<=90||p.X>=190,"A rune must never bridge a gap.");
            Assert.True(p.X>=6&&p.X<=314&&p.Y>=6&&p.Y<=84,"Runes must remain within the plot.");
            double expected=p.X<=90?30+p.X/3:55-(p.X-190)*30/130;
            Assert.Equal(expected,p.Y,6);
        });
        Assert.Empty(RuneTrace.Placements([new Point[]{new(20,30)}],plot));
        Assert.Empty(RuneTrace.Placements(segments,new Rect(0,0,10,10)));
        IReadOnlyList<IReadOnlyList<Point>> dense=[Enumerable.Range(0,20001).Select(x=>new Point(x,30+x%13)).ToArray()];
        Assert.InRange(RuneTrace.Placements(dense,new Rect(0,0,20000,90)).Count,1,RuneTrace.MaximumRunes);
        Assert.Empty(RuneTrace.Placements([new Point[]{new(-400,20),new(-100,30)}],plot));

        string original=Theme.CurrentName;
        try
        {
            foreach(string name in new[]{"Midnight","Skyrim","Mage"})
            {
                Theme.Apply(name);
                var drawing=new DrawingGroup();
                using(var context=drawing.Open())RuneTrace.Draw(context,segments,plot,"Cpu",1);
                var glyphs=Glyphs(drawing).ToArray();
                if(name=="Midnight"||SystemParameters.HighContrast)Assert.Empty(glyphs);
                else
                {
                    Assert.Equal(positions.Count,glyphs.Length);
                    Assert.All(glyphs,g=>Assert.DoesNotContain((ushort)0,g.GlyphRun.GlyphIndices));
                }
            }
        }
        finally {Theme.Apply(original);}
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)=>drawing switch
    {
        GlyphRunDrawing glyph=>[glyph],
        DrawingGroup group=>group.Children.SelectMany(Glyphs),
        _=>[]
    };
}

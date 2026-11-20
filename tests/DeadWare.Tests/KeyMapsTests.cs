using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class KeyMapsTests
{
    const uint E=KeyMaps.Empty;
    static LightingDevice Kb(int width,int height,uint[] cells,int leds,int type=KeyMaps.KeyboardType,IReadOnlyList<string>? names=null)=>
        new("k|l","Kb",type,[new LightingZone(0,"Keyboard",leds,new ZoneMatrix(width,height,cells))],["Direct"],0,names);

    [Fact] public void Positions_are_the_mean_cell_normalised()
    {
        var m=KeyMaps.Of(Kb(3,2,[0,0,E,E,E,1],2,names:["Esc","F1"]))!;
        Assert.Equal(0.25,m.X[0]!.Value,9);Assert.Equal(0,m.Y[0]!.Value,9);                                        // columns 0 and 1: mean 0.5, over width-1 = 2
        Assert.Equal(1,m.X[1]!.Value,9);Assert.Equal(1,m.Y[1]!.Value,9);
        Assert.Equal(["Esc","F1"],m.Names);
        Assert.Equal(["",""],KeyMaps.Of(Kb(3,2,[0,0,E,E,E,1],2))!.Names);
    }
    [Fact] public void A_one_row_matrix_gives_y_one_half()
    {
        var m=KeyMaps.Of(Kb(3,1,[0,1,2],3))!;
        Assert.All(m.Y,y=>Assert.Equal(0.5,y!.Value,9));
        Assert.Equal(new double?[]{0,0.5,1},m.X);
    }
    [Fact] public void A_one_column_matrix_gives_x_one_half()
    {
        var m=KeyMaps.Of(Kb(1,3,[0,1,2],3))!;
        Assert.All(m.X,x=>Assert.Equal(0.5,x!.Value,9));
        Assert.Equal(new double?[]{0,0.5,1},m.Y);
    }
    [Fact] public void An_empty_cell_and_an_index_beyond_the_lights_are_ignored()
    {
        var m=KeyMaps.Of(Kb(4,1,[0,E,7,1],2))!;
        Assert.Equal(new double?[]{0,1},m.X);
    }
    [Fact] public void A_light_in_no_cell_has_no_position()
    {
        var m=KeyMaps.Of(Kb(2,1,[0,E],2))!;
        Assert.Equal(0,m.X[0]);Assert.Null(m.X[1]);Assert.Null(m.Y[1]);
    }
    [Fact] public void An_all_empty_matrix_supports_no_wave()
    {
        var d=Kb(2,2,[E,E,E,E],3);
        var m=KeyMaps.Of(d)!;
        Assert.Equal(3,m.X.Count);Assert.All(m.X,Assert.Null);Assert.All(m.Y,Assert.Null);
        Assert.False(KeyMaps.SupportsWave(d));
    }
    [Fact] public void Two_zones_or_a_short_zone_give_no_map()
    {
        var two=new LightingDevice("k|l","Kb",5,[new LightingZone(0,"A",2,new(2,1,[0,1])),new LightingZone(1,"B",2,new(2,1,[0,1]))],["Direct"],0);
        Assert.Null(KeyMaps.Of(two));Assert.False(KeyMaps.SupportsWave(two));
        var shortZone=Kb(3,1,[0,1,2],3,names:["a","b","c","d","e"]);
        Assert.Equal(5,KeyMaps.LightCount(shortZone));
        Assert.Null(KeyMaps.Of(shortZone));Assert.False(KeyMaps.SupportsWave(shortZone));
        Assert.Equal(4,KeyMaps.LightCount(two));
    }
    [Fact] public void Only_a_keyboard_supports_wave()
    {
        var mouse=Kb(3,1,[0,1,2],3,type:6);
        Assert.NotNull(KeyMaps.Of(mouse));Assert.False(KeyMaps.IsKeyboard(mouse));Assert.False(KeyMaps.SupportsWave(mouse));
        var kb=Kb(3,1,[0,1,2],3);
        Assert.True(KeyMaps.IsKeyboard(kb));Assert.True(KeyMaps.SupportsWave(kb));
    }
    [Fact] public void Key_width_follows_the_smallest_gap_and_is_clamped()
    {
        Assert.Equal(0.085,KeyMaps.Of(Kb(11,1,Enumerable.Range(0,11).Select(i=>(uint)i).ToArray(),11))!.KeyWidth,9);    // gap 0.1
        Assert.Equal(0.10,KeyMaps.Of(Kb(3,1,[0,1,2],3))!.KeyWidth,9);                                                  // gap 0.5, clamped
        Assert.Equal(0.008,KeyMaps.Of(Kb(201,1,Enumerable.Range(0,201).Select(i=>(uint)i).ToArray(),201))!.KeyWidth,9); // gap 0.005, clamped
        Assert.Equal(0.034,KeyMaps.Of(Kb(1,2,[0,1],2))!.KeyWidth,9);                                                   // one X value: no gap, 0.04
        Assert.Equal(0.085,KeyMaps.Of(Kb(11,2,[..Enumerable.Range(0,11).Select(i=>(uint)i),..Enumerable.Range(0,11).Select(i=>(uint)(i+11))],22))!.KeyWidth,9); // repeated X values count once
    }
}

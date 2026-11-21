using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class KeyPickTests
{
    static Dictionary<int,PaintedKey> Paint(params (int Light,string Color)[] keys)=>keys.ToDictionary(k=>k.Light,k=>new PaintedKey{Name="Key "+k.Light,Color=k.Color});

    [Fact] public void A_click_picks_and_a_second_click_drops()
    {
        var p=new KeyPick();
        p.Down(5);p.Up();
        Assert.Equal([5],p.Picked);
        p.Down(5);p.Up();
        Assert.Empty(p.Picked);
    }

    [Fact] public void A_drag_from_an_unpicked_key_adds_every_key_it_crosses()
    {
        var p=new KeyPick();
        p.Set([2]);
        p.Down(1);p.Over(2);p.Over(3);p.Over(4);p.Up();
        Assert.Equal([1,2,3,4],p.Picked.Order());
    }

    [Fact] public void A_drag_from_a_picked_key_removes()
    {
        var p=new KeyPick();
        p.Set([1,2,3,7]);
        p.Down(1);p.Over(2);p.Over(5);p.Over(3);p.Up();
        Assert.Equal([7],p.Picked);
        // After Up the drag is over: Over changes nothing.
        p.Over(7);
        Assert.Equal([7],p.Picked);
    }

    [Fact] public void Over_without_Down_does_nothing()
    {
        var p=new KeyPick();
        p.Over(3);
        Assert.Empty(p.Picked);
        p.Set([4]);p.Over(4);
        Assert.Equal([4],p.Picked);
    }

    [Fact] public void Set_replaces_and_Clear_empties()
    {
        var p=new KeyPick();
        p.Down(9);p.Up();
        p.Set([1,2]);
        Assert.Equal([1,2],p.Picked.Order());
        p.Set([3,3]);
        Assert.Equal([3],p.Picked);
        p.Clear();
        Assert.Empty(p.Picked);
    }

    [Fact] public void History_returns_states_newest_first_and_copies_them()
    {
        var h=new PaintHistory();
        var first=Paint((1,"#FF0000"));
        h.Push(first);
        h.Push(null);
        var third=Paint((2,"#00FF00"),(3,"#0000FF"));
        h.Push(third);
        // Changing what was pushed, entries included, changes nothing in the history.
        first[1].Color="#123456";first[9]=new PaintedKey{Name="x",Color="#000000"};
        third[2].Color="#654321";third.Remove(3);
        Assert.True(h.CanUndo);
        var a=h.Pop()!;
        Assert.Equal([2,3],a.Keys.Order());Assert.Equal("#00FF00",a[2].Color);Assert.Equal("#0000FF",a[3].Color);Assert.Equal("Key 3",a[3].Name);
        Assert.Null(h.Pop());
        var c=h.Pop()!;
        Assert.Equal([1],c.Keys);Assert.Equal("#FF0000",c[1].Color);
        Assert.False(h.CanUndo);
    }

    [Fact] public void History_keeps_the_newest_50()
    {
        var h=new PaintHistory();
        for(var i=0;i<60;i++) h.Push(Paint((i,"#FFFFFF")));
        var popped=new List<int>();
        while(h.CanUndo) popped.Add(h.Pop()!.Keys.Single());
        Assert.Equal(Enumerable.Range(10,50).Reverse(),popped);
        h.Push(null);h.Clear();
        Assert.False(h.CanUndo);
    }

    [Fact] public void Pop_on_empty_returns_null_and_CanUndo_is_false()
    {
        var h=new PaintHistory();
        Assert.False(h.CanUndo);
        Assert.Null(h.Pop());
        Assert.False(h.CanUndo);
    }
}

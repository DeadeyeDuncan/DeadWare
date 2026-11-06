using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessRowOrderTests
{
    static void AssertPlanSorts(string[] current,string[] sorted,int expectedMoves)
    {
        var moves=CalmOrderPlanner.Plan(current,sorted);
        var r=current.Where(sorted.Contains).ToList();
        foreach(var m in moves){Assert.NotEqual(m.From,m.To);Assert.Equal(m.Id,r[m.From]);r.RemoveAt(m.From);r.Insert(m.To,m.Id);}
        Assert.Equal(sorted.Where(current.Contains),r);
        Assert.Equal(expectedMoves,moves.Count);
    }
    [Fact] public void Moves_apply_in_sequence_to_reach_the_sorted_order(){
        AssertPlanSorts(["a","b","c"],["b","a","c"],1);
        AssertPlanSorts(["a","b","c","d"],["d","c","b","a"],3);
        AssertPlanSorts(["a","b","c"],["a","b","c"],0);
        AssertPlanSorts([],[],0);
        AssertPlanSorts(["a","b"],["c","d"],0);
        AssertPlanSorts(["a","x","b","c"],["c","y","a","b"],1);
        AssertPlanSorts(["a","b","c","d","e"],["b","c","d","e","a"],1);
    }
    [Fact] public void Sorted_order_moves_nothing()=>Assert.Empty(CalmOrderPlanner.Plan(["a","b","c"],["a","b","c"]));
    [Fact] public void Moves_only_rows_outside_the_longest_increasing_subsequence()=>Assert.Equal(["a"],CalmOrderPlanner.Plan(["a","b","c","d","e"],["b","c","d","e","a"]).Select(m=>m.Id));
    [Fact] public void Swap_of_two_neighbours_moves_one_row()=>Assert.Single(CalmOrderPlanner.Plan(["a","b","c"],["b","a","c"]));
    [Fact] public void Held_update_removes_exited_and_appends_new_apps_at_the_bottom()=>
        Assert.Equal(["app:A","app:C","app:D"],CalmOrderPlanner.Held(["app:A","app:B","app:C"],["app:D","app:C","app:A"],_=>null));
    [Fact] public void Held_update_adds_new_processes_after_their_expanded_app()=>
        Assert.Equal(["app:A","proc:1:1","proc:3:1","app:B"],CalmOrderPlanner.Held(["app:A","proc:1:1","app:B"],["app:A","proc:3:1","proc:1:1","app:B"],id=>id.StartsWith("proc:")?"app:A":null));
    [Fact] public void Held_update_puts_children_of_a_new_app_at_the_bottom_after_the_new_apps()=>
        Assert.Equal(["app:A","app:B","app:C","proc:3:1","proc:4:1"],CalmOrderPlanner.Held(["app:A"],["proc:3:1","app:B","proc:4:1","app:C","app:A"],id=>id.StartsWith("proc:")?(id=="proc:3:1"?"app:B":"app:C"):null));
    [Fact] public void Held_update_puts_children_of_an_absent_parent_at_the_bottom()=>
        Assert.Equal(["app:A","proc:9:1"],CalmOrderPlanner.Held(["app:A"],["app:A","proc:9:1"],id=>id.StartsWith("proc:")?"app:Z":null));
    [Fact] public void Hold_lasts_three_seconds_after_a_navigation_key(){ var h=new OrderHold();h.KeyNavigated(10);Assert.True(h.IsHeld(12.9));Assert.False(h.IsHeld(13.1));Assert.False(h.IsHeld(13.0));}
    [Fact] public void Expired_key_hold_does_not_release_a_pointer_hold(){ var h=new OrderHold{PointerOver=true};h.KeyNavigated(10);Assert.True(h.IsHeld(20));}
    [Fact] public void Pointer_or_menu_holds(){ var h=new OrderHold{PointerOver=true};Assert.True(h.IsHeld(0));h.PointerOver=false;h.MenuOpen=true;Assert.True(h.IsHeld(0));h.MenuOpen=false;Assert.False(h.IsHeld(0));}
}

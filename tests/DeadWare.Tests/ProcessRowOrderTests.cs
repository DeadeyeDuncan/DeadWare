using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessRowOrderTests
{
    [Fact] public void Sorted_order_moves_nothing()=>Assert.Empty(CalmOrderPlanner.Plan(["a","b","c"],["a","b","c"]));
    [Fact] public void Moves_only_rows_outside_the_longest_increasing_subsequence()=>Assert.Equal(["a"],CalmOrderPlanner.Plan(["a","b","c","d","e"],["b","c","d","e","a"]).Select(m=>m.Id));
    [Fact] public void Swap_of_two_neighbours_moves_one_row()=>Assert.Single(CalmOrderPlanner.Plan(["a","b","c"],["b","a","c"]));
    [Fact] public void Held_update_removes_exited_and_appends_new_apps_at_the_bottom()=>
        Assert.Equal(["app:A","app:C","app:D"],CalmOrderPlanner.Held(["app:A","app:B","app:C"],["app:D","app:C","app:A"],_=>null));
    [Fact] public void Held_update_adds_new_processes_after_their_expanded_app()=>
        Assert.Equal(["app:A","proc:1:1","proc:3:1","app:B"],CalmOrderPlanner.Held(["app:A","proc:1:1","app:B"],["app:A","proc:3:1","proc:1:1","app:B"],id=>id.StartsWith("proc:")?"app:A":null));
    [Fact] public void Hold_lasts_three_seconds_after_a_navigation_key(){ var h=new OrderHold();h.KeyNavigated(10);Assert.True(h.IsHeld(12.9));Assert.False(h.IsHeld(13.1));}
    [Fact] public void Pointer_or_menu_holds(){ var h=new OrderHold{PointerOver=true};Assert.True(h.IsHeld(0));h.PointerOver=false;h.MenuOpen=true;Assert.True(h.IsHeld(0));h.MenuOpen=false;Assert.False(h.IsHeld(0));}
}

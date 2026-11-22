using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LookNamesTests
{
    static readonly string[] Saved=["Gaming","Work"];

    static DeviceView Keyboard(string? look=null,bool changed=false,Dictionary<int,PaintedKey>? paint=null,List<DeviceLayer>? layers=null,DeviceMode? mode=null)=>
        new("k","Keys",5,PaintKind.Direct,null,null,false,[],LightingMode:mode,Keyboard:true,CanPaint:true,Paint:paint,Layers:layers,Looks:look is null?null:[look],Look:look,LookChanged:changed);

    [Fact] public void An_empty_or_blank_name_is_a_problem()
    {
        Assert.Equal("Give the look a name.",LookNames.Problem(null,Saved));
        Assert.Equal("Give the look a name.",LookNames.Problem("",Saved));
        Assert.Equal("Give the look a name.",LookNames.Problem("   ",Saved));
        Assert.Null(LookNames.Problem("Evening",Saved));
    }

    [Fact] public void A_41_character_name_is_a_problem_and_40_is_not()
    {
        Assert.Equal("A look's name can be up to 40 characters.",LookNames.Problem(new string('a',41),Saved));
        Assert.Null(LookNames.Problem(new string('a',40),Saved));
        // The helper trims the name before it counts, so spaces around 40 characters are fine.
        Assert.Null(LookNames.Problem("  "+new string('a',40)+"  ",Saved));
    }

    [Fact] public void A_taken_name_in_another_case_is_a_problem()
    {
        Assert.Equal("There is already a look called gaming.",LookNames.Problem("gaming",Saved));
        Assert.Equal("There is already a look called WORK.",LookNames.Problem(" WORK ",Saved));
        // Renaming another look to it is still a problem.
        Assert.Equal("There is already a look called gaming.",LookNames.Problem("gaming",Saved,renaming:"Work"));
    }

    [Fact] public void Renaming_a_look_to_its_own_name_in_another_case_is_fine()
    {
        Assert.Null(LookNames.Problem("GAMING",Saved,renaming:"Gaming"));
        Assert.Null(LookNames.Problem("Gaming",Saved,renaming:"Gaming"));
        Assert.Equal("Give the look a name.",LookNames.Problem(" ",Saved,renaming:"Gaming"));
    }

    [Fact] public void Unsaved_cases()
    {
        Assert.True(LookNames.Unsaved(Keyboard(look:"Gaming",changed:true)));
        Assert.True(LookNames.Unsaved(Keyboard(paint:new(){[3]=new PaintedKey{Name="W",Color="#FF0000"}})));
        Assert.True(LookNames.Unsaved(Keyboard(mode:new DeviceMode{Kind=DeviceModes.Static,Speed=40})));
        Assert.True(LookNames.Unsaved(Keyboard(layers:[new DeviceLayer()])));
        Assert.False(LookNames.Unsaved(Keyboard()));
        // Empty paint and layers are nothing.
        Assert.False(LookNames.Unsaved(Keyboard(paint:[],layers:[])));
        Assert.False(LookNames.Unsaved(Keyboard(look:"Gaming",paint:new(){[3]=new PaintedKey{Name="W",Color="#FF0000"}},mode:new DeviceMode{Kind=DeviceModes.Static,Speed=40})));
    }

    [Fact] public void A_change_not_yet_in_the_state_counts_as_unsaved()
    {
        // The state says the keyboard matches its look (or has nothing), but a paint or layer change the window holds hasn't reached it.
        Assert.True(LookNames.Unsaved(Keyboard(look:"Gaming"),pending:true));
        Assert.True(LookNames.Unsaved(Keyboard(),pending:true));
        Assert.False(LookNames.Unsaved(Keyboard(look:"Gaming"),pending:false));
        Assert.True(LookNames.Unsaved(Keyboard(look:"Gaming",changed:true),pending:false));
    }
}

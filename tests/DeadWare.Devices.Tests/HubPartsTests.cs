using DeadWare.Core;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class HubPartsTests
{
    static HubList One(byte type,byte model,int channel=1)=>new([new HubChannel(channel,type,model)],false,15);
    static HubBuild Owners()=>HubParts.Build(HubParser.Devices(HubFixtures.DeviceList())!);

    [Fact] public void The_owners_hub_is_three_fans_and_a_pump()
    {
        var b=Owners();
        Assert.Null(b.Blocker);Assert.Null(b.Detail);
        Assert.Equal(new[]{"iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK TITAN 360"},b.Parts.Select(p=>p.Name).ToArray());
        Assert.Equal(new[]{1,2,3,13},b.Parts.Select(p=>p.Channel).ToArray());
        Assert.Equal(new[]{8,8,8,20},b.Parts.Select(p=>p.Leds).ToArray());
        Assert.Equal(44,HubParts.Leds(b.Parts));
        Assert.Equal(new[]{false,false,false,true},b.Parts.Select(p=>p.Coolant).ToArray());
        Assert.Equal(new[]{"fan","fan","fan","pump"},b.Parts.Select(p=>p.Kind).ToArray());
    }

    [Theory]
    [InlineData(0x0F,0x00,"iCUE LINK RX RGB",8,false)]
    [InlineData(0x01,0x00,"iCUE LINK QX RGB",34,false)]
    [InlineData(0x13,0x00,"iCUE LINK RX",0,false)]
    [InlineData(0x11,0x00,"iCUE LINK TITAN 240",20,true)]
    [InlineData(0x11,0x04,"iCUE LINK TITAN 240",20,true)]
    [InlineData(0x11,0x01,"iCUE LINK TITAN 280",20,true)]
    [InlineData(0x11,0x02,"iCUE LINK TITAN 360",20,true)]
    [InlineData(0x11,0x05,"iCUE LINK TITAN 360",20,true)]
    [InlineData(0x11,0x03,"iCUE LINK TITAN 420",20,true)]
    [InlineData(0x07,0x00,"iCUE LINK H100i RGB",20,true)]
    [InlineData(0x07,0x04,"iCUE LINK H100i RGB",20,true)]
    [InlineData(0x07,0x01,"iCUE LINK H115i RGB",20,true)]
    [InlineData(0x07,0x02,"iCUE LINK H150i RGB",20,true)]
    [InlineData(0x07,0x05,"iCUE LINK H150i RGB",20,true)]
    [InlineData(0x07,0x03,"iCUE LINK H170i RGB",20,true)]
    [InlineData(0x0C,0x00,"iCUE LINK XD5 ELITE",22,true)]
    [InlineData(0x19,0x00,"iCUE LINK XD6 ELITE",22,true)]
    public void Each_table_row(int type,int model,string name,int leds,bool coolant)
    {
        var b=HubParts.Build(One((byte)type,(byte)model,5));
        Assert.Null(b.Blocker);
        var p=Assert.Single(b.Parts);
        Assert.Equal(new HubPart(5,(byte)type,(byte)model,name,leds,coolant,coolant?"pump":"fan"),p);
    }

    [Fact] public void A_fan_without_leds_is_kept()
    {
        var b=HubParts.Build(One(0x13,0x00,2));
        var p=Assert.Single(b.Parts);
        Assert.Equal(0,p.Leds);
        Assert.Equal(0,HubParts.Leds(b.Parts));
        var speeds=HubParser.Values(HubFixtures.Values(512,10,"25 00"))!;
        Assert.Equal(1,Assert.Single(HubParts.Readings(b.Parts,[],speeds).Parts).Rpm);
    }

    [Theory]
    [InlineData(0x1A,0x01,"type 0x1A, model 0x01")]
    [InlineData(0x06,0x00,"type 0x06, model 0x00")]
    [InlineData(0x0F,0x01,"type 0x0F, model 0x01")]
    [InlineData(0x05,0x02,"type 0x05, model 0x02")]
    public void An_unknown_type_or_model_is_unsupported(int type,int model,string detail)
    {
        var b=HubParts.Build(One((byte)type,(byte)model));
        Assert.Equal("unknownPart",b.Blocker);
        Assert.Equal(detail,b.Detail);
        Assert.Empty(b.Parts);
    }

    [Fact] public void An_unknown_part_among_known_ones_refuses_the_hub()
    {
        var list=new HubList([new HubChannel(1,0x0F,0),new HubChannel(2,0x1A,1)],false,50);
        var b=HubParts.Build(list);
        Assert.Equal("unknownPart",b.Blocker);
        Assert.Equal("type 0x1A, model 0x01",b.Detail);
    }

    [Fact] public void An_overflowing_list_is_too_many_parts()
    {
        var b=HubParts.Build(new HubList([new HubChannel(1,0x0F,0)],true,500));
        Assert.Equal("tooManyParts",b.Blocker);
        Assert.Empty(b.Parts);
    }

    [Fact] public void Readings_follow_the_channels()
    {
        var b=Owners();
        var r=HubParts.Readings(b.Parts,HubParser.Values(HubFixtures.Temperatures())!,HubParser.Values(HubFixtures.Speeds())!);
        Assert.Equal(new int?[]{1660,1654,1663,2774},r.Parts.Select(p=>p.Rpm).ToArray());
        Assert.Equal(new double?[]{null,null,null,43.7},r.Parts.Select(p=>p.Celsius).ToArray());
        Assert.Equal(new[]{1,2,3,13},r.Parts.Select(p=>p.Channel).ToArray());
        Assert.Equal(new[]{"fan","fan","fan","pump"},r.Parts.Select(p=>p.Kind).ToArray());
        Assert.DoesNotContain(r.Parts,p=>p.Celsius==33.7);
    }

    [Fact] public void A_channel_beyond_the_entry_count_reads_as_unavailable()
    {
        var b=Owners();
        var ten=HubFixtures.Values(512,10);
        var r=HubParts.Readings(b.Parts,HubParser.Values(ten)!,HubParser.Values(HubFixtures.Values(512,10,"25 00"))!);
        var pump=r.Parts[3];
        Assert.Equal(13,pump.Channel);
        Assert.Null(pump.Rpm);
        Assert.Null(pump.Celsius);
        Assert.Equal(1,r.Parts[0].Rpm);
        Assert.DoesNotContain(HubParts.Readings(b.Parts,[],[]).Parts,p=>p.Rpm is not null||p.Celsius is not null);
    }

    [Fact] public void A_speed_is_read_unsigned()
    {
        var speeds=HubParser.Values(HubFixtures.Reply(512,HubFixtures.Hex("00 00 08 00 25 00 02"),HubFixtures.Hex("01 00 00"),HubFixtures.Hex("00 30 F2")))!;
        var r=HubParts.Readings(HubParts.Build(One(0x0F,0x00,1)).Parts,[],speeds);
        Assert.Equal(62000,Assert.Single(r.Parts).Rpm);
    }

    static List<HubSavedPart> Saved(params (int Channel,int Type,int Model)[] e)=>e.Select(x=>new HubSavedPart{Channel=x.Channel,Type=x.Type,Model=x.Model}).ToList();

    [Fact] public void Saved_parts_round_trip_through_the_table()
    {
        var saved=HubParts.ToSaved(Owners().Parts);
        Assert.Equal([(1,15,0),(2,15,0),(3,15,0),(13,17,2)],saved.Select(p=>(p.Channel,p.Type,p.Model)).ToArray());
        var parts=HubParts.FromSaved(saved)!;
        Assert.Equal(["iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK TITAN 360"],parts.Select(p=>p.Name).ToArray());
        Assert.Equal([8,8,8,20],parts.Select(p=>p.Leds).ToArray());
        Assert.Equal([false,false,false,true],parts.Select(p=>p.Coolant).ToArray());
        Assert.Equal(["fan","fan","fan","pump"],parts.Select(p=>p.Kind).ToArray());
    }

    [Fact] public void Saved_parts_with_a_row_the_table_lacks_give_nothing()
    {
        Assert.Null(HubParts.FromSaved(Saved((1,15,0),(5,26,1))));
        Assert.Null(HubParts.FromSaved(Saved((1,300,0))));
        Assert.Null(HubParts.FromSaved(null));
        Assert.Null(HubParts.FromSaved([]));
    }
}

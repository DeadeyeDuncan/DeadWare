using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class HubParserTests
{
    static byte[] Hex(string hex)=>HubFixtures.Hex(hex);

    [Fact] public void Firmware_reads_4_1_656(){Assert.Equal("4.1.656",HubParser.Firmware(HubFixtures.Firmware()));}
    [Fact] public void A_firmware_reply_too_short_for_the_patch_is_null(){Assert.Null(HubParser.Firmware(Hex("00 00 02 00 04 01 90")));}

    [Fact] public void The_device_list_gives_channels_1_2_3_and_13()
    {
        var list=HubParser.Devices(HubFixtures.DeviceList());
        Assert.NotNull(list);
        Assert.Equal(new[]{1,2,3,13},list.Occupied.Select(c=>c.Channel).ToArray());
        Assert.Equal(new byte[]{0x0F,0x0F,0x0F,0x11},list.Occupied.Select(c=>c.Type).ToArray());
        Assert.Equal(new byte[]{0x00,0x00,0x00,0x02},list.Occupied.Select(c=>c.Model).ToArray());
        Assert.Equal(215,list.Used);
        Assert.False(list.Overflow);
    }

    [Fact] public void Temperatures_have_52_entries()
    {
        var v=HubParser.Values(HubFixtures.Temperatures());
        Assert.NotNull(v);
        Assert.Equal(52,v.Length);
        Assert.True(v[0].Available);Assert.Equal(337,v[0].Raw);
        Assert.False(v[1].Available);Assert.False(v[2].Available);Assert.False(v[3].Available);
        Assert.True(v[13].Available);Assert.Equal(437,v[13].Raw);
    }

    [Fact] public void Speeds_have_26_entries()
    {
        var v=HubParser.Values(HubFixtures.Speeds());
        Assert.NotNull(v);
        Assert.Equal(26,v.Length);
        Assert.False(v[0].Available);
        Assert.Equal(new short[]{1660,1654,1663},new[]{v[1].Raw,v[2].Raw,v[3].Raw});
        Assert.True(v[1].Available&&v[2].Available&&v[3].Available);
        Assert.True(v[13].Available);Assert.Equal(2774,v[13].Raw);
    }

    [Fact] public void A_list_that_runs_past_the_reply_is_overflow()
    {
        // last channel 15, every channel a 26-character id: 7 + 15 x 34 = 517, past the 512 bytes.
        var rec=Hex("00 00 0F 00 00 00 05 1A").Concat(new byte[26]).ToArray();
        var big=HubFixtures.Reply(512,Hex("00 00 08 00 21 00 0F"),rec,rec,rec,rec,rec,rec,rec,rec,rec,rec,rec,rec,rec,rec,rec);
        var list=HubParser.Devices(big);
        Assert.NotNull(list);
        Assert.True(list.Overflow);

        // The fixture cut to 200 bytes, short of its 215.
        var cut=HubParser.Devices(HubFixtures.DeviceList().AsSpan(0,200));
        Assert.NotNull(cut);
        Assert.True(cut.Overflow);
    }

    [Fact] public void A_short_reply_is_malformed()
    {
        var six=HubFixtures.DeviceList().AsSpan(0,6);
        Assert.Null(HubParser.Devices(six));
        Assert.Null(HubParser.Values(six));
    }

    [Fact] public void A_short_reply_bounds_the_count()
    {
        Assert.Null(HubParser.Values(HubFixtures.Values(100,52)));
        Assert.Equal(31,HubParser.Values(HubFixtures.Values(100,31))!.Length);
        Assert.Null(HubParser.Values(HubFixtures.Values(100,32).AsSpan(0,100)));
    }

    [Theory]
    [InlineData(168,true)]
    [InlineData(169,false)]
    public void An_entry_count_past_the_reply_is_malformed(int count,bool parses)
    {
        var reply=new byte[512];
        reply[6]=(byte)count;
        var v=HubParser.Values(reply);
        Assert.Equal(parses,v is not null);
        if(parses) Assert.Equal(168,v!.Length);
    }

    [Fact] public void A_wrong_data_type_is_not_that_endpoints_data()
    {
        Assert.True(HubParser.IsData(HubFixtures.DeviceList(),HubEndpoint.Devices));
        Assert.True(HubParser.IsData(HubFixtures.Temperatures(),HubEndpoint.Temperatures));
        Assert.True(HubParser.IsData(HubFixtures.Speeds(),HubEndpoint.Speeds));
        Assert.False(HubParser.IsData(HubFixtures.Speeds(),HubEndpoint.Temperatures));
        Assert.False(HubParser.IsData(HubFixtures.Temperatures(),HubEndpoint.Speeds));
        Assert.False(HubParser.IsData(HubFixtures.DeviceList(),HubEndpoint.Speeds));
        Assert.False(HubParser.IsData(HubFixtures.Ack(0x08),HubEndpoint.Speeds));
        Assert.False(HubParser.IsData(Hex("00 00 08"),HubEndpoint.Speeds));
    }

    [Fact] public void Status_03_is_wrong_mode()
    {
        Assert.Equal(HubStatus.Ok,HubParser.Status(HubFixtures.Ack(0x08)));
        Assert.Equal(HubStatus.WrongMode,HubParser.Status(HubFixtures.Ack(0x08,3)));
        Assert.Equal(HubStatus.Other,HubParser.Status(HubFixtures.Ack(0x08,1)));
        Assert.Equal(HubStatus.Other,HubParser.Status(HubFixtures.Ack(0x08,0xFF)));
        Assert.Equal(HubStatus.Other,HubParser.Status(Hex("00 00 08")));
    }

    [Fact] public void An_open_reply_with_leftovers_still_reads_as_an_echo()
    {
        var reply=HubFixtures.Ack(0x0D,0,HubFixtures.Speeds());
        Assert.True(HubParser.Echoes(reply,0x0D));
        Assert.False(HubParser.Echoes(reply,0x08));
        Assert.Equal(HubStatus.Ok,HubParser.Status(reply));
        Assert.False(HubParser.Echoes(Hex("00 00"),0x0D));
    }

    [Fact] public void A_negative_value_is_signed()
    {
        var reply=HubFixtures.Reply(512,Hex("00 00 08 00 10 00 01"),Hex("00 F6 FF"));
        Assert.Equal(-10,HubParser.Values(reply)![0].Raw);
    }
}

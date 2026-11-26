using System.Reflection;
using System.Runtime.InteropServices;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5: every frame the hub can be sent. The hub also carries the pump and the radiator fans; what is not on this list cannot be built.
public sealed class HubFrameTests
{
    static byte[] Hex(string hex)=>HubFixtures.Hex(hex);
    static void StartsWith(string hex,HubFrame frame)
    {
        var expected=Hex(hex);
        Assert.Equal(513,frame.Bytes.Length);
        Assert.Equal(expected,frame.Bytes.Span[..expected.Length].ToArray());
        Assert.All(frame.Bytes.Span[expected.Length..].ToArray(),b=>Assert.Equal(0,b));
    }

    [Fact] public void Firmware_is_02_13(){var f=HubFrame.Firmware();StartsWith("00 00 01 02 13",f);Assert.Equal(0x02,f.Opcode);}
    [Fact] public void Software_mode_is_01_03_00_02(){var f=HubFrame.SoftwareMode();StartsWith("00 00 01 01 03 00 02",f);Assert.Equal(0x01,f.Opcode);}
    [Fact] public void Hardware_mode_is_01_03_00_01(){StartsWith("00 00 01 01 03 00 01",HubFrame.HardwareMode());}
    [Fact] public void Close_names_its_endpoint()
    {
        StartsWith("00 00 01 05 01 01 36",HubFrame.Close(HubEndpoint.Devices));
        StartsWith("00 00 01 05 01 01 21",HubFrame.Close(HubEndpoint.Temperatures));
        StartsWith("00 00 01 05 01 01 17",HubFrame.Close(HubEndpoint.Speeds));
        Assert.Equal(0x05,HubFrame.Close(HubEndpoint.Devices).Opcode);
    }
    [Fact] public void Open_names_its_endpoint()
    {
        StartsWith("00 00 01 0D 01 36",HubFrame.Open(HubEndpoint.Devices));
        StartsWith("00 00 01 0D 01 21",HubFrame.Open(HubEndpoint.Temperatures));
        StartsWith("00 00 01 0D 01 17",HubFrame.Open(HubEndpoint.Speeds));
        Assert.Equal(0x0D,HubFrame.Open(HubEndpoint.Speeds).Opcode);
    }
    [Fact] public void Read_is_08_01(){var f=HubFrame.Read();StartsWith("00 00 01 08 01",f);Assert.Equal(0x08,f.Opcode);}
    [Fact] public void Close_colours_is_05_01_01_22(){StartsWith("00 00 01 05 01 01 22",HubFrame.CloseColours());}
    [Fact] public void Open_colours_is_0D_00_22(){StartsWith("00 00 01 0D 00 22",HubFrame.OpenColours());}

    [Fact] public void Forty_four_leds_make_one_colour_frame()
    {
        var leds=Enumerable.Repeat(new Rgb(1,2,3),44).ToArray();
        var frames=HubFrame.Colours(leds);
        var f=Assert.Single(frames);
        Assert.Equal(513,f.Bytes.Length);
        Assert.Equal(Hex("00 00 01 06 00 86 00 00 00 12 00 01 02 03"),f.Bytes.Span[..14].ToArray());
        Assert.Equal(0x03,f.Bytes.Span[142]);
        Assert.Equal(0x00,f.Bytes.Span[143]);
        Assert.Equal(0x06,f.Opcode);
    }

    [Fact] public void A_hundred_and_sixty_eight_leds_need_a_second_frame()
    {
        var leds=Enumerable.Repeat(new Rgb(1,2,3),168).ToArray();
        var frames=HubFrame.Colours(leds);
        Assert.Equal(2,frames.Count);
        Assert.Equal(Hex("00 00 01 06 00 FA 01 00 00 12 00"),frames[0].Bytes.Span[..11].ToArray());
        Assert.Equal(Hex("00 00 01 07 00"),frames[1].Bytes.Span[..5].ToArray());
        // 6 + 504 = 510 payload bytes: 508 in the first frame, the last 2 (02 03) in the second.
        Assert.Equal(Hex("00 00 01 07 00 02 03 00"),frames[1].Bytes.Span[..8].ToArray());
        Assert.Equal(0x01,frames[0].Bytes.Span[512]);
        Assert.Equal(513,frames[1].Bytes.Length);
    }

    [Fact] public void A_hundred_and_sixty_seven_leds_fit_one_frame()
    {
        var frames=HubFrame.Colours(Enumerable.Repeat(new Rgb(1,2,3),167).ToArray());
        Assert.Single(frames);
    }

    [Fact] public void No_leds_make_no_frames(){Assert.Empty(HubFrame.Colours([]));}

    [Fact] public void Bytes_gives_a_copy_so_the_frames_own_array_cannot_be_reached()
    {
        var f=HubFrame.Read();
        // The escape route under test is the array behind the memory; if Bytes ever wraps non-array memory that escape is gone, so revisit this assert rather than loosen it silently.
        Assert.True(MemoryMarshal.TryGetArray(f.Bytes,out var seg));
        seg.Array![3]=0xFF;seg.Array[6]=0xAA;
        Assert.Equal(0x08,f.Bytes.Span[3]);
        Assert.Equal(0x00,f.Bytes.Span[6]);
        Assert.Equal(0x08,f.Opcode);
        Assert.Equal(Hex("00 00 01 08 01"),f.Bytes.Span[..5].ToArray());
    }

    [Fact] public void The_type_can_build_nothing_off_the_list()
    {
        var t=typeof(HubFrame);
        const BindingFlags every=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
        // Constructors: all private (no way to build a frame but a factory).
        Assert.All(t.GetConstructors(every),c=>Assert.True(c.IsPrivate,"constructor "+c));
        // Methods that are not private: exactly the nine factories, static, public, with exactly these parameters and none optional.
        var open=t.GetMethods(every).Where(m=>!m.IsPrivate&&!m.IsSpecialName).ToArray();
        Assert.All(open,m=>Assert.True(m.IsStatic&&m.IsPublic,"not a public static: "+m));
        var expected=new Dictionary<string,Type[]>
        {
            ["Firmware"]=[],["SoftwareMode"]=[],["HardwareMode"]=[],["Read"]=[],["CloseColours"]=[],["OpenColours"]=[],
            ["Close"]=[typeof(HubEndpoint)],["Open"]=[typeof(HubEndpoint)],["Colours"]=[typeof(ReadOnlySpan<Rgb>)],
        };
        Assert.Equal(expected.Keys.Order().ToArray(),open.Select(m=>m.Name).Order().ToArray());
        foreach(var m in open)
        {
            var ps=m.GetParameters();
            Assert.Equal(expected[m.Name],ps.Select(p=>p.ParameterType).ToArray());
            Assert.DoesNotContain(ps,p=>p.IsOptional||p.HasDefaultValue);
            Assert.Equal(m.Name=="Colours"?typeof(IReadOnlyList<HubFrame>):typeof(HubFrame),m.ReturnType);
        }
        // Special-named methods (operators, accessors, event handlers): only the getters of Bytes and Opcode, so no conversion operator can make a frame.
        Assert.Equal(new[]{"get_Bytes","get_Opcode"},t.GetMethods(every).Where(m=>m.IsSpecialName).Select(m=>m.Name).Order().ToArray());
        // Properties of any visibility: Bytes and Opcode, getter only.
        var props=t.GetProperties(every);
        Assert.Equal(new[]{"Bytes","Opcode"},props.Select(p=>p.Name).Order().ToArray());
        Assert.All(props,p=>{Assert.Null(p.GetSetMethod(true));Assert.True(p.GetGetMethod(false) is not null);});
        Assert.Equal(typeof(ReadOnlyMemory<byte>),props.Single(p=>p.Name=="Bytes").PropertyType);
        Assert.Equal(typeof(byte),props.Single(p=>p.Name=="Opcode").PropertyType);
        // Fields that are not private: only the two constants. Nothing else is nested, raised or exposed.
        var fields=t.GetFields(every).Where(f=>!f.IsPrivate).ToArray();
        Assert.Equal(new[]{"ChunkBytes","Length"},fields.Select(f=>f.Name).Order().ToArray());
        Assert.All(fields,f=>Assert.True(f.IsLiteral));
        Assert.Empty(t.GetEvents(every));
        Assert.DoesNotContain(t.GetNestedTypes(every),n=>!n.IsNestedPrivate);
        // No non-private member of a type that exposes a frame's array or a frame itself (the checks above leave nothing else, this names the rule).
        Type[] forbidden=[typeof(HubFrame),typeof(byte[]),typeof(Span<byte>),typeof(Memory<byte>)];
        Assert.DoesNotContain(t.GetMembers(every).Where(m=>m is not MethodBase{IsPrivate:true}&&!(m is FieldInfo{IsPrivate:true})&&!(m is MethodInfo{IsStatic:true,IsPublic:true})),m=>m switch
        {
            PropertyInfo pi=>forbidden.Contains(pi.PropertyType),
            FieldInfo fi=>forbidden.Contains(fi.FieldType),
            MethodInfo mi=>forbidden.Contains(mi.ReturnType),
            _=>false,
        });

        var all=new List<HubFrame>{HubFrame.Firmware(),HubFrame.SoftwareMode(),HubFrame.HardwareMode(),HubFrame.Read(),HubFrame.CloseColours(),HubFrame.OpenColours()};
        foreach(var ep in new[]{HubEndpoint.Devices,HubEndpoint.Temperatures,HubEndpoint.Speeds}){all.Add(HubFrame.Close(ep));all.Add(HubFrame.Open(ep));}
        foreach(int n in new[]{1,44,167,168,400}) all.AddRange(HubFrame.Colours(Enumerable.Repeat(new Rgb(9,8,7),n).ToArray()));

        string[] commands=["02 13","01 03 00 02","01 03 00 01","05 01 01 36","05 01 01 21","05 01 01 17","05 01 01 22","0D 01 36","0D 01 21","0D 01 17","0D 00 22","08 01","06 00","07 00"];
        var allowed=commands.Select(Hex).ToArray();
        foreach(var f in all)
        {
            var b=f.Bytes.Span;
            Assert.Equal(513,b.Length);
            Assert.Equal(Hex("00 00 01"),b[..3].ToArray());
            var rest=b[3..].ToArray();
            Assert.Contains(allowed,c=>rest.Take(c.Length).SequenceEqual(c));
            Assert.False(b[3]==0x06&&b[4]==0x01);
            if(b[3] is 0x05 or 0x0D) Assert.NotEqual(0x18,b[b[3]==0x05?6:5]);
        }
        Assert.Equal(new byte[]{0x17,0x21,0x36},Enum.GetValues<HubEndpoint>().Select(e=>(byte)e).Order().ToArray());
        foreach(var bad in new byte[]{0x18,0x22,0x00})
        {
            Assert.Throws<ArgumentOutOfRangeException>(()=>HubFrame.Close((HubEndpoint)bad));
            Assert.Throws<ArgumentOutOfRangeException>(()=>HubFrame.Open((HubEndpoint)bad));
        }
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Sdk;

namespace DeadWare.Devices.Tests;

// Lighting 5: the fakes every later hub test stands on. A fake that let a frame off the list, an unlocked write or an unbalanced lock through would
// let the tests built on it pass for the wrong reason, so these pin the fakes' checks as well as their replies. Every test but the one about writing
// outside the lock enters the fake lock before it writes.
public sealed class FakeHubTests
{
    readonly FakeTimeProvider time=new();
    readonly FakeHubLock corsairLock=new();
    readonly FakeHubTransport fake;

    public FakeHubTests(){fake=new(time,corsairLock);}

    static byte[] Hex(string hex)=>HubFixtures.Hex(hex);
    // One exchange: the frame, then the reply.
    static byte[]? Send(IHubPort port,HubFrame frame){port.Write(frame);return port.Read(250);}
    static byte[] Head(byte[]? reply)=>reply![..4];
    // Enters the fake lock, runs f and lets go, all on the calling thread (one lock unit).
    T Locked<T>(Func<T> f)
    {
        Assert.True(corsairLock.Enter(250));
        try{return f();}
        finally{corsairLock.Exit();}
    }
    // Runs a on a thread of its own, waits for that thread to end and returns what a threw (on a bare thread it would end the test run).
    static Exception? OnAnotherThread(Action a)
    {
        Exception? thrown=null;
        var t=new Thread(()=>{try{a();}catch(Exception e){thrown=e;}});
        t.Start();t.Join();
        return thrown;
    }

    [Fact]
    public void It_answers_a_read_transaction_with_the_fixture()
    {
        Assert.True(corsairLock.Enter(250));
        using(var port=fake.Open())
        {
            var close=Send(port,HubFrame.Close(HubEndpoint.Devices));
            var open=Send(port,HubFrame.Open(HubEndpoint.Devices));
            var read=Send(port,HubFrame.Read());
            var closeAgain=Send(port,HubFrame.Close(HubEndpoint.Devices));
            Assert.Equal(HubFixtures.Ack(0x05),close);
            Assert.Equal(HubFixtures.Ack(0x0D,0,close),open);
            Assert.Equal(HubFixtures.DeviceList(),read);
            // A close after a read carries the read's bytes from offset 4 on, as the hub's replies do.
            Assert.Equal(HubFixtures.Ack(0x05,0,HubFixtures.DeviceList()),closeAgain);
            Assert.Null(port.Read(1));
        }
        corsairLock.Exit();
        Assert.Equal(1,fake.Opens);
        Assert.Equal(1,fake.Closes);
        Assert.Equal(new[]{Hex("05 01 01 36"),Hex("0D 01 36"),Hex("08 01"),Hex("05 01 01 36")},fake.Commands);
        Assert.Equal(4,fake.Writes.Count);
        Assert.All(fake.Writes,w=>Assert.Equal(513,w.Length));
        Assert.Equal(Hex("00 00 01 08 01"),fake.Writes[2][..5]);
    }

    [Fact]
    public void It_answers_firmware_temperatures_and_speeds_with_the_fixtures()
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Equal(HubFixtures.Firmware(),Send(port,HubFrame.Firmware()));
        foreach(var (ep,expected) in new[]{(HubEndpoint.Temperatures,HubFixtures.Temperatures()),(HubEndpoint.Speeds,HubFixtures.Speeds())})
        {
            Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.Close(ep))));
            Assert.Equal(Hex("00 00 0D 00"),Head(Send(port,HubFrame.Open(ep))));
            Assert.Equal(expected,Send(port,HubFrame.Read()));
            Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.Close(ep))));
        }
        corsairLock.Exit();
    }

    [Fact]
    public void Its_parts_coolant_and_speeds_can_be_changed()
    {
        fake.Parts[4]=(0x01,0x00);
        fake.Coolant=44.1;
        fake.Rpm[13]=2800;fake.Rpm[2]=null;fake.Rpm[4]=900;fake.Rpm[7]=500;   // channel 7 has no part, so no speed
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        byte[] Read(HubEndpoint ep){Send(port,HubFrame.Close(ep));Send(port,HubFrame.Open(ep));var r=Send(port,HubFrame.Read())!;Send(port,HubFrame.Close(ep));return r;}
        var list=Read(HubEndpoint.Devices);
        Assert.Equal(Hex("00 00 08 00 21 00 0D"),list[..7]);
        Assert.Equal(Hex("00 00 01 00 00 00 05 1A"),list[109..117]);          // channel 4 after three 34-byte records
        Assert.Equal(new byte[64],list[143..207]);                            // channels 5 to 12, empty
        Assert.Equal(Hex("00 00 11 02 00 00 05 1A"),list[207..215]);          // channel 13
        Assert.Equal(HubFixtures.IdA,System.Text.Encoding.ASCII.GetString(list,215,26));   // the fifth part takes the first id again
        var temperatures=Read(HubEndpoint.Temperatures);
        Assert.Equal(Hex("00 B9 01"),temperatures[46..49]);                   // entry 13: 441
        Assert.Equal(Hex("01 00 00"),temperatures[19..22]);                   // entry 4: a fan, no coolant
        var speeds=Read(HubEndpoint.Speeds);
        Assert.Equal(Hex("00 F0 0A"),speeds[46..49]);                         // 2800
        Assert.Equal(Hex("01 00 00"),speeds[13..16]);                         // channel 2: unavailable
        Assert.Equal(Hex("00 84 03"),speeds[19..22]);                         // channel 4: 900
        Assert.Equal(Hex("01 00 00"),speeds[28..31]);                         // channel 7: no part
        fake.Coolant=null;
        Assert.Equal(Hex("01 00 00"),Read(HubEndpoint.Temperatures)[46..49]);
        corsairLock.Exit();
    }

    [Fact]
    public void In_its_own_mode_an_open_answers_03()
    {
        fake.Software=false;
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Equal(HubFixtures.Firmware(),Send(port,HubFrame.Firmware()));     // the firmware answers in either mode
        Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.Close(HubEndpoint.Temperatures))));
        Assert.Equal(Hex("00 00 0D 03"),Head(Send(port,HubFrame.Open(HubEndpoint.Temperatures))));
        Assert.Equal(Hex("00 00 08 03"),Head(Send(port,HubFrame.Read())));
        Assert.Equal(Hex("00 00 0D 03"),Head(Send(port,HubFrame.OpenColours())));
        Assert.Equal(Hex("00 00 01 00"),Head(Send(port,HubFrame.SoftwareMode())));
        Assert.True(fake.Software);
        Assert.Equal(Hex("00 00 0D 00"),Head(Send(port,HubFrame.Open(HubEndpoint.Temperatures))));
        Assert.Equal(HubFixtures.Temperatures(),Send(port,HubFrame.Read()));
        Send(port,HubFrame.Close(HubEndpoint.Temperatures));
        // Hardware mode hands the hub back to its own mode.
        Assert.Equal(Hex("00 00 01 00"),Head(Send(port,HubFrame.HardwareMode())));
        Assert.False(fake.Software);
        Assert.Equal(Hex("00 00 0D 03"),Head(Send(port,HubFrame.Open(HubEndpoint.Speeds))));
        corsairLock.Exit();
    }

    [Fact]
    public void Wrong_mode_answers_03_to_the_coming_opens_although_held()
    {
        fake.WrongMode=2;
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Equal(Hex("00 00 0D 03"),Head(Send(port,HubFrame.Open(HubEndpoint.Speeds))));
        Assert.Equal(Hex("00 00 0D 03"),Head(Send(port,HubFrame.OpenColours())));
        Assert.Equal(Hex("00 00 0D 00"),Head(Send(port,HubFrame.Open(HubEndpoint.Speeds))));
        Assert.Equal(HubFixtures.Speeds(),Send(port,HubFrame.Read()));
        Assert.True(fake.Software);
        Assert.Equal(0,fake.WrongMode);
        corsairLock.Exit();
    }

    [Fact]
    public void A_colour_write_is_decoded()
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.CloseColours())));
        Assert.Equal(Hex("00 00 0D 00"),Head(Send(port,HubFrame.OpenColours())));
        var frame=Assert.Single(HubFrame.Colours(Enumerable.Repeat(new Rgb(1,2,3),44).ToArray()));
        Assert.Equal(Hex("00 00 06 00"),Head(Send(port,frame)));
        Assert.Null(fake.LastColours);                                       // finished only when the colour endpoint is closed
        Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.CloseColours())));
        corsairLock.Exit();
        var leds=fake.LastColours!;
        Assert.Equal(44,leds.Length);
        Assert.All(leds,c=>Assert.Equal(new Rgb(1,2,3),c));
        Assert.Equal(1,fake.Count(0x06,0x00));
        Assert.Equal(2,fake.Count(0x05,0x01,0x01,0x22));
        Assert.Equal(1,fake.Count(0x0D,0x00,0x22));
        Assert.Equal(0,fake.Count(0x07,0x00));
        Assert.Equal(Hex("06 00 86 00 00 00 12 00 01 02 03"),fake.Commands[2][..11]);
        Assert.Equal(2+6+44*3,fake.Commands[2].Length);                      // up to the last LED's blue
    }

    [Fact]
    public void A_colour_write_in_two_frames_is_decoded_in_order()
    {
        var sent=Enumerable.Range(0,168).Select(i=>new Rgb((byte)i,(byte)(255-i),(byte)(i/2+1))).ToArray();
        var frames=HubFrame.Colours(sent);
        Assert.Equal(2,frames.Count);
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Send(port,HubFrame.CloseColours());
        Send(port,HubFrame.OpenColours());
        Assert.Equal(Hex("00 00 06 00"),Head(Send(port,frames[0])));
        Assert.Equal(Hex("00 00 07 00"),Head(Send(port,frames[1])));
        Send(port,HubFrame.CloseColours());
        // Black needs no special case: a second write of all zeros replaces it.
        var black=new Rgb[168];
        Send(port,HubFrame.CloseColours());
        Send(port,HubFrame.OpenColours());
        Assert.Null(Record.Exception(()=>{foreach(var f in HubFrame.Colours(black)) Send(port,f);}));
        var before=fake.LastColours;
        Send(port,HubFrame.CloseColours());
        corsairLock.Exit();
        Assert.Equal(sent,before);
        Assert.Equal(black,fake.LastColours);
        Assert.Equal(2,fake.Count(0x07,0x00));
    }

    [Fact]
    public void Silent_answers_nothing_and_stale_reports_come_before_the_reply()
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        fake.Stale=2;
        Assert.Equal(Hex("00 00 08 00 25 00 1A 00 01 00"),port.Read(1)![..10]);  // another program's speeds reply: every entry 1 RPM
        Assert.Equal(Hex("00 00 08 00 25 00 1A 00 01 00"),port.Read(1)![..10]);
        Assert.Null(port.Read(1));
        fake.Silent=true;
        fake.Software=false;
        port.Write(HubFrame.SoftwareMode());
        Assert.Null(port.Read(250));
        Assert.False(fake.Software);                                         // a silent hub does nothing with what it is sent
        Assert.Single(fake.Writes);
        fake.Silent=false;
        fake.Stale=1;
        port.Write(HubFrame.Firmware());
        Assert.Equal(0x08,port.Read(250)![2]);
        Assert.Equal(HubFixtures.Firmware(),port.Read(250));
        Assert.Null(port.Read(1));
        corsairLock.Exit();
    }

    [Fact]
    public void Latency_moves_the_clock_on_every_read_and_the_limits_are_recorded()
    {
        fake.Latency=TimeSpan.FromMilliseconds(400);
        var start=time.GetTimestamp();
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Null(port.Read(1));                                           // an empty drain read moves it too
        Assert.Equal(HubFixtures.Firmware(),Send(port,HubFrame.Firmware()));
        corsairLock.Exit();
        Assert.Equal(TimeSpan.FromMilliseconds(800),time.GetElapsedTime(start));
        Assert.Equal(new[]{1,250},fake.Limits);
    }

    [Fact]
    public void It_lists_its_hubs_and_opens_only_one()
    {
        Assert.Equal(1,fake.Count());
        fake.Hubs=0;
        Assert.Equal(0,fake.Count());
        Assert.Throws<IOException>(()=>fake.Open());
        fake.Hubs=2;
        Assert.Equal(2,fake.Count());
        Assert.Throws<IOException>(()=>fake.Open());
        fake.Hubs=1;
        fake.ThrowOnOpen=true;
        Assert.Throws<IOException>(()=>fake.Open());
        Assert.Equal(0,fake.Opens);
        fake.ThrowOnOpen=false;
        fake.Open().Dispose();
        Assert.Equal(1,fake.Opens);
        Assert.Equal(1,fake.Closes);
    }

    [Fact]
    public void A_read_or_write_can_be_made_to_throw()
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        fake.ThrowOnRead=new InvalidOperationException("a read that went wrong");
        Assert.Throws<InvalidOperationException>(()=>port.Read(1));
        Assert.Null(port.Read(1));                                           // the next read only
        fake.ThrowOnWrite=true;
        Assert.Throws<IOException>(()=>port.Write(HubFrame.Firmware()));
        Assert.Equal(1,fake.Count(0x02,0x13));                               // recorded, never answered
        Assert.Null(port.Read(1));
        fake.ThrowOnWrite=false;
        Assert.Equal(HubFixtures.Firmware(),Send(port,HubFrame.Firmware()));
        corsairLock.Exit();
    }

    [Fact]
    public void A_reply_can_be_replaced()
    {
        fake.Reply=(command,reply)=>command[0]==0x08?reply[..100]:reply;
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Send(port,HubFrame.Close(HubEndpoint.Speeds));
        Send(port,HubFrame.Open(HubEndpoint.Speeds));
        Assert.Equal(HubFixtures.Speeds()[..100],Send(port,HubFrame.Read()));
        fake.Reply=(_,_)=>null;
        Assert.Null(Send(port,HubFrame.Firmware()));
        corsairLock.Exit();
    }

    [Fact]
    public async Task A_held_read_ends_when_the_port_is_disposed_and_goes_on_when_released()
    {
        using var hang=new ManualResetEventSlim(false);
        using var entered=new ManualResetEventSlim(false);
        fake.HangRead=hang;
        fake.ReadEntered=entered;
        var port=fake.Open();
        var reading=Task.Run(()=>Locked(()=>port.Read(250)));
        Assert.True(entered.Wait(Waits.RealTimeCap));
        Assert.False(reading.IsCompleted);
        port.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>reading.WaitAsync(Waits.RealTimeCap));
        Assert.Equal(1,fake.Closes);
        Assert.Throws<ObjectDisposedException>(()=>Locked(()=>port.Read(1)));
        entered.Reset();
        using var second=fake.Open();
        var released=Task.Run(()=>Locked(()=>second.Read(250)));
        Assert.True(entered.Wait(Waits.RealTimeCap));
        hang.Set();
        Assert.Null(await released.WaitAsync(Waits.RealTimeCap));
        Assert.Equal(3,corsairLock.Enters);
        Assert.Equal(3,corsairLock.Exits);
    }

    [Fact]
    public void Every_frame_a_factory_builds_passes()
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        var all=new List<HubFrame>{HubFrame.Firmware(),HubFrame.SoftwareMode(),HubFrame.Read(),HubFrame.CloseColours(),HubFrame.OpenColours()};
        foreach(var ep in new[]{HubEndpoint.Devices,HubEndpoint.Temperatures,HubEndpoint.Speeds}){all.Add(HubFrame.Close(ep));all.Add(HubFrame.Open(ep));}
        foreach(int n in new[]{1,44,167,168,400}) all.AddRange(HubFrame.Colours(Enumerable.Repeat(new Rgb(9,8,7),n).ToArray()));
        all.Add(HubFrame.HardwareMode());
        foreach(var f in all) port.Write(f);
        corsairLock.Exit();
        Assert.Equal(all.Count,fake.Writes.Count);
        Assert.Equal(1,corsairLock.Enters);
    }

    [Fact]
    public void A_write_outside_the_lock_fails_the_test()
    {
        using var port=fake.Open();
        Assert.Throws<FailException>(()=>port.Write(HubFrame.Firmware()));
        // The worker catches every exception inside a step, so the failure sticks: each value the fakes record fails the test again when read.
        Assert.Throws<FailException>(()=>corsairLock.Held);
        Assert.Throws<FailException>(()=>corsairLock.Enters);
        Assert.Throws<FailException>(()=>fake.Commands);
        Assert.Throws<FailException>(()=>fake.Opens);
        Assert.Throws<FailException>(()=>fake.LastColours);
    }

    [Fact]
    public void A_write_from_another_thread_than_the_holder_fails_the_test()
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        port.Write(HubFrame.Firmware());                                     // the holder's own write passes
        Assert.IsType<FailException>(OnAnotherThread(()=>port.Write(HubFrame.Firmware())));
        corsairLock.Exit();
        Assert.Throws<FailException>(()=>corsairLock.Exits);
        Assert.Throws<FailException>(()=>fake.Writes);
    }

    [Fact]
    public void A_second_enter_or_an_unmatched_exit_fails_the_test()
    {
        Assert.True(corsairLock.Enter(250));
        Assert.Throws<FailException>(()=>corsairLock.Enter(250));
        Assert.Throws<FailException>(()=>corsairLock.Held);
        var other=new FakeHubLock();
        Assert.Throws<FailException>(()=>other.Exit());
        Assert.Throws<FailException>(()=>other.Exits);
    }

    [Fact]
    public void An_exit_from_another_thread_fails_the_test()
    {
        Assert.True(corsairLock.Enter(250));
        Assert.IsType<FailException>(OnAnotherThread(()=>corsairLock.Exit()));
        Assert.Throws<FailException>(()=>corsairLock.Held);
    }

    [Fact]
    public void A_busy_or_unavailable_lock_refuses()
    {
        corsairLock.Busy=true;                                               // another program holds it
        Assert.False(corsairLock.Enter(250));
        corsairLock.Busy=false;
        corsairLock.Problem="the Corsair lock could not be made";
        Assert.False(corsairLock.Enter(250));
        corsairLock.Problem=null;
        Assert.Equal(0,corsairLock.Enters);
        Assert.True(corsairLock.Enter(250));
        Assert.True(corsairLock.Held);
        corsairLock.Exit();
        Assert.False(corsairLock.Held);
        Assert.Equal(1,corsairLock.Enters);
        Assert.Equal(1,corsairLock.Exits);
    }

    // Fix round 1, M4: the worker makes every Enter on its own thread, so a second thread of ours entering while the first holds it is a broken
    // rule, not a skipped round.
    [Fact]
    public void An_enter_from_another_thread_while_held_fails_the_test()
    {
        Assert.True(corsairLock.Enter(250));
        Assert.IsType<FailException>(OnAnotherThread(()=>corsairLock.Enter(0)));
        corsairLock.Exit();
        Assert.Throws<FailException>(()=>corsairLock.Held);
    }

    // Fix round 1, M2: reads are hub I/O too: inside the lock and on the thread that entered it, drain reads included.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_read_outside_the_lock_or_from_another_thread_fails_the_test(bool fromAnotherThread)
    {
        using var port=fake.Open();
        if(fromAnotherThread)
        {
            Assert.True(corsairLock.Enter(250));
            Assert.IsType<FailException>(OnAnotherThread(()=>port.Read(1)));
            corsairLock.Exit();
        }
        else Assert.Throws<FailException>(()=>port.Read(1));
        Assert.Throws<FailException>(()=>corsairLock.Held);
        Assert.Throws<FailException>(()=>fake.Limits);
    }

    // Fix round 1, I1: a transaction is held from its first frame to its last. Another program may take the lock between two units and open its
    // own endpoint on the hub's handle (FanControl writes speeds through 0x18), so a read, a colour chunk or the closing close in a later unit than
    // its open would go to that endpoint.
    [Theory]
    [InlineData(false)]                                                      // the read in the next unit
    [InlineData(true)]                                                       // the read in the unit of the open, the closing close in the next
    public void A_read_transaction_split_across_lock_units_fails_the_test(bool atTheClose)
    {
        using var port=fake.Open();
        Assert.True(corsairLock.Enter(250));
        Send(port,HubFrame.Close(HubEndpoint.Temperatures));
        Send(port,HubFrame.Open(HubEndpoint.Temperatures));
        if(atTheClose) Assert.Equal(HubFixtures.Temperatures(),Send(port,HubFrame.Read()));
        corsairLock.Exit();
        Assert.True(corsairLock.Enter(250));
        Assert.Throws<FailException>(()=>port.Write(atTheClose?HubFrame.Close(HubEndpoint.Temperatures):HubFrame.Read()));
        Assert.Throws<FailException>(()=>fake.Commands);
    }

    [Theory]
    [InlineData(false)]                                                      // the continuation chunk in the next unit
    [InlineData(true)]                                                       // both chunks in the unit of the open, the closing close in the next
    public void A_colour_write_split_across_lock_units_fails_the_test(bool atTheClose)
    {
        var frames=HubFrame.Colours(Enumerable.Repeat(new Rgb(1,2,3),168).ToArray());
        using var port=fake.Open();
        Assert.True(corsairLock.Enter(250));
        Send(port,HubFrame.CloseColours());
        Send(port,HubFrame.OpenColours());
        Send(port,frames[0]);
        if(atTheClose) Send(port,frames[1]);
        corsairLock.Exit();
        Assert.True(corsairLock.Enter(250));
        Assert.Throws<FailException>(()=>port.Write(atTheClose?HubFrame.CloseColours():frames[1]));
        Assert.Throws<FailException>(()=>fake.LastColours);
    }

    // The worker closes the port on every failure, so what a cut-short transaction left open belongs to that port and goes with it: the next
    // transaction's opening close, in a new unit on a new port, is not a split.
    [Fact]
    public void A_transaction_cut_short_by_closing_the_port_starts_cleanly_in_the_next_unit()
    {
        var frames=HubFrame.Colours(Enumerable.Repeat(new Rgb(4,5,6),168).ToArray());
        Locked(()=>
        {
            using var cut=fake.Open();
            Send(cut,HubFrame.Close(HubEndpoint.Temperatures));
            Send(cut,HubFrame.Open(HubEndpoint.Temperatures));
            Send(cut,HubFrame.CloseColours());
            Send(cut,HubFrame.OpenColours());
            return Send(cut,frames[0]);
        });
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.Close(HubEndpoint.Temperatures))));
        Send(port,HubFrame.Open(HubEndpoint.Temperatures));
        Assert.Equal(HubFixtures.Temperatures(),Send(port,HubFrame.Read()));
        Send(port,HubFrame.Close(HubEndpoint.Temperatures));
        Assert.Equal(Hex("00 00 05 00"),Head(Send(port,HubFrame.CloseColours())));
        Send(port,HubFrame.OpenColours());
        foreach(var f in frames) Send(port,f);
        Send(port,HubFrame.CloseColours());
        corsairLock.Exit();
        Assert.Equal(168,fake.LastColours!.Length);
        Assert.Equal(2,corsairLock.Enters);
        Assert.False(corsairLock.Held);
    }

    // Fix round 1, M5: the channel maps can be changed whole or channel by channel from the test's thread.
    [Fact]
    public void Parts_and_speeds_can_be_replaced_whole()
    {
        fake.Parts=new(){[1]=(0x13,0x00),[2]=(0x13,0x00)};
        fake.Rpm=new(){[1]=700,[2]=800};
        fake.Parts.Remove(2);
        Assert.Single(fake.Parts);
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Send(port,HubFrame.Close(HubEndpoint.Devices));
        Send(port,HubFrame.Open(HubEndpoint.Devices));
        var list=Send(port,HubFrame.Read())!;
        Send(port,HubFrame.Close(HubEndpoint.Devices));
        Send(port,HubFrame.Close(HubEndpoint.Speeds));
        Send(port,HubFrame.Open(HubEndpoint.Speeds));
        var speeds=Send(port,HubFrame.Read())!;
        Send(port,HubFrame.Close(HubEndpoint.Speeds));
        corsairLock.Exit();
        Assert.Equal(Hex("00 00 08 00 21 00 01 00 00 13 00 00 00 05 1A"),list[..15]);
        Assert.Equal(Hex("00 BC 02"),speeds[10..13]);                         // channel 1: 700
        Assert.Equal(Hex("01 00 00"),speeds[13..16]);                         // channel 2: no part now
    }

    // No factory can build these (Task 3), so each is forged with reflection to show the fake refuses it. They reach only the fake.
    static HubFrame Forged(string hex,int length)
    {
        var bytes=new byte[length];
        Hex(hex).CopyTo(bytes,0);
        var f=(HubFrame)RuntimeHelpers.GetUninitializedObject(typeof(HubFrame));
        typeof(HubFrame).GetField("bytes",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(f,bytes);
        return f;
    }

    [Theory]
    [InlineData("00 00 01 06 01 05 00",513)]                                 // a write on handle 1
    [InlineData("00 00 01 0D 01 18",513)]                                    // opens the speed endpoint
    [InlineData("00 00 01 05 01 01 18",513)]                                 // closes it
    [InlineData("00 00 01 0D 00 18",513)]                                    // the speed endpoint on the colour handle
    [InlineData("00 00 01 02 13 01",513)]                                    // a listed command with a byte after it
    [InlineData("00 00 01 01 08 01",513)]                                    // a command off the list
    [InlineData("00 00 01 0D 01 22",513)]                                    // the colour endpoint on handle 1
    [InlineData("00 00 02 02 13",513)]                                       // a wrong header
    [InlineData("01 00 01 02 13",513)]                                       // a report id other than 0
    [InlineData("00 00 01 02 13",512)]                                       // a wrong length
    [InlineData("00 00 01 02 13",514)]
    public void A_frame_off_the_list_fails_the_test(string hex,int length)
    {
        Assert.True(corsairLock.Enter(250));
        using var port=fake.Open();
        Assert.Throws<FailException>(()=>port.Write(Forged(hex,length)));
        Assert.Throws<FailException>(()=>fake.Writes);
    }

    [Fact]
    public void FakeHub_drives_reads_rounds_and_lets_go()
    {
        var hub=new FakeHub();
        int raised=0;
        hub.Changed+=()=>raised++;
        Assert.Equal("off",hub.Snapshot.State);
        time.Advance(TimeSpan.FromSeconds(1));
        var at=time.GetTimestamp();
        hub.Drive(time,pump:2716,fans:[1337,1326,null]);
        var s=hub.Snapshot;
        Assert.Equal("driving",s.State);
        Assert.Equal(at,s.SoftwareAt);
        Assert.Equal(at,s.ReadingsAt);
        Assert.Equal(new[]{1,2,3,13},s.Parts!.Select(p=>p.Channel));
        Assert.Equal(new[]{8,8,8,20},s.Parts!.Select(p=>p.Leds));
        Assert.Equal(new[]{"iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK TITAN 360"},s.Parts!.Select(p=>p.Name));
        Assert.Equal(new int?[]{1337,1326,null,2716},s.Readings!.Parts.Select(p=>p.Rpm));
        Assert.Equal(new[]{"fan","fan","fan","pump"},s.Readings.Parts.Select(p=>p.Kind));
        Assert.Equal(43.7,s.Readings.Parts[3].Celsius);
        Assert.Equal(1,raised);
        time.Advance(TimeSpan.FromSeconds(3));
        hub.Round(time);
        Assert.Equal(at,hub.Snapshot.SoftwareAt);                            // R12: a round leaves SoftwareAt as it was
        Assert.Equal(TimeSpan.FromSeconds(3),time.GetElapsedTime(at,hub.Snapshot.ReadingsAt));
        Assert.Equal(new int?[]{1660,1654,1663,2774},hub.Snapshot.Readings!.Parts.Select(p=>p.Rpm));
        Assert.Equal(2,raised);
        hub.Allow(null);
        Assert.Equal("driving",hub.Snapshot.State);
        Assert.Equal(2,raised);
        hub.Allow("icue","because");
        Assert.Equal("letGo",hub.Snapshot.State);
        Assert.Equal("icue",hub.Snapshot.Reason);
        Assert.Equal("because",hub.Snapshot.Detail);
        Assert.Equal(0,hub.Snapshot.SoftwareAt);
        Assert.Equal(4,hub.Snapshot.Parts!.Count);                           // parts are kept after a let-go
        Assert.Equal(3,raised);
        hub.Allow("off");                                                    // not driving: nothing changes
        Assert.Equal("icue",hub.Snapshot.Reason);
        Assert.Equal(3,raised);
        Assert.Equal(new (string?,string?)[]{(null,null),("icue","because"),("off",null)},hub.Allows);
    }

    [Fact]
    public async Task FakeHub_records_its_calls_and_holds_a_let_go()
    {
        var hub=new FakeHub();
        var colours=new[]{new Rgb(1,2,3),new Rgb(4,5,6)};
        hub.Post(colours);
        colours[0]=new Rgb(9,9,9);
        hub.SetDark(true);hub.SetDark(false);
        hub.Quiet();hub.Resume();hub.Resume();hub.Recheck();hub.RequestReading();
        Assert.True(hub.LetGoAsync().IsCompleted);
        hub.HoldLetGo=new TaskCompletionSource();
        var held=hub.LetGoAsync();
        Assert.False(held.IsCompleted);
        hub.HoldLetGo.SetResult();
        await held.WaitAsync(Waits.RealTimeCap);
        Assert.Equal(new Rgb(1,2,3),Assert.Single(hub.Posts)[0]);
        Assert.Equal(new[]{true,false},hub.Darks);
        Assert.Equal(1,hub.Quiets);
        Assert.Equal(2,hub.Resumes);
        Assert.Equal(1,hub.Rechecks);
        Assert.Equal(1,hub.ReadingRequests);
        Assert.Equal(2,hub.LetGos);
        hub.AutoLetGo=false;
        hub.Drive(time);
        hub.Allow("icue");
        Assert.Equal("driving",hub.Snapshot.State);
        int raised=0;
        hub.Changed+=()=>raised++;
        hub.Snapshot=HubSnapshot.Idle;
        hub.Raise();
        Assert.Equal(1,raised);
        Assert.Equal("off",hub.Snapshot.State);
    }
}

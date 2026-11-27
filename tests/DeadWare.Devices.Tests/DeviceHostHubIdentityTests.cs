using DeadWare.Core;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed partial class DeviceHostTests
{
    // Lighting 5, Task 8: the hub's colours saved under its old serial|location key are copied under linkhub before the layout is arranged.
    [Fact]
    public async Task The_hubs_old_colours_are_adopted_before_the_layout_is_arranged()
    {
        UseOwnerRig();
        var old=LightingKeys.Device("SERIAL-6",@"HID: \\?\HID#FAKE-3");
        var names=new[]{"iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK TITAN 360"};
        var seed=new LightingSettings();
        for(var i=0;i<4;i++) seed.ZoneColors[LightingKeys.Zone(old,i)]=new ZoneColorEntry{Color="#112233",ZoneName=names[i]};
        new LightingStore(lightingPath).Save(seed);
        var h=await Running();
        var hub=h.State.Devices.Single(d=>d.Key=="linkhub");
        Assert.Equal(["#112233","#112233","#112233","#112233"],hub.Zones.Select(z=>z.Color).ToArray());
        Assert.Equal("openrgb",hub.Hub);
        var saved=new LightingStore(lightingPath).Load().Settings;
        for(var i=0;i<4;i++)
        {
            Assert.Equal("#112233",saved.ZoneColors["linkhub#"+i].Color);
            Assert.Equal("#112233",saved.ZoneColors[LightingKeys.Zone(old,i)].Color);
        }
        Assert.Equal(1,LogCount("hub settings copied to linkhub"));
    }
}

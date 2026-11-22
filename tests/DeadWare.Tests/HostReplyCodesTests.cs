using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// The host's short reply codes reach the Lighting page's status line in plain words; a host sentence is shown as it came.
public class HostReplyCodesTests
{
    [Theory]
    [InlineData(HostReplyCodes.Starting,"The lighting helper is still starting. Try again in a moment.")]
    [InlineData(HostReplyCodes.UnknownZone,"That zone isn't listed any more. Its device may have been unplugged.")]
    [InlineData(HostReplyCodes.CouldntSave,"DeadWare couldn't save your lighting settings, so the change wasn't made.")]
    [InlineData(HostReplyCodes.InternalError,"The lighting helper hit an error. Details are in its log, devices.log.")]
    [InlineData(HostReplyCodes.RescanFailed,"The lighting helper lost its connection to OpenRGB and is reconnecting.")]
    [InlineData(HostReplyCodes.Unsupported,"The lighting helper didn't recognise the change. Restart DeadWare.")]
    [InlineData(HostReplyCodes.Version,"The lighting helper is a different version. Restart DeadWare.")]
    [InlineData(HostReplyCodes.BadEveryday,"Those everyday settings aren't valid.")]
    [InlineData(HostReplyCodes.BadSleep,"That sleep time isn't one of the choices.")]
    [InlineData(HostReplyCodes.ShortcutSlow,"Windows didn't answer about the shortcut in time. Try again.")]
    public void Every_short_code_is_explained(string code,string text)=>Assert.Equal(text,HostReplyCodes.Explain(code));

    [Fact]
    public void A_sentence_from_the_host_is_shown_as_it_came()=>Assert.Equal("Brightness must be 0 to 100.",HostReplyCodes.Explain("Brightness must be 0 to 100."));
}

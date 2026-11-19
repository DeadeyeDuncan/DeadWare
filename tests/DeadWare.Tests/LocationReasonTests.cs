using DeadWare.Core;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

// Polish round 2, item 2: every reason InstallLocation.Evaluate gives is read back as its fault and the path it names, so the refusals of
// DeadWare's own folders can name that path and fit their words to the fault, and the OpenRGB folder's refusal reads them the same way.
public class LocationReasonTests
{
    const string Entry=@"C:\ProgramData\DeadWare\updates\0.19.0\DeadWare.dll";
    static string Reason(params PathSecurity[] entries)=>InstallLocation.Evaluate(entries).Reason!;

    public static TheoryData<string,LocationFault> Reasons=>new()
    {
        {Reason(new PathSecurity(Entry,PathKind.File,"S-1-5-32-544",[new AclRule("S-1-5-32-545",0x2,true,false)],false)),LocationFault.Writable},
        {Reason(new PathSecurity(Entry,PathKind.File,"S-1-5-21-1-2-3-1001",[],false)),LocationFault.NotOwned},
        {Reason(new PathSecurity(Entry,PathKind.File,null,[],false,"Access to the path is denied.")),LocationFault.Unreadable},
        {Reason(new PathSecurity(Entry,PathKind.File,"S-1-5-32-544",[],true)),LocationFault.Link},
        {Reason(new PathSecurity(Entry,PathKind.File,null,[],false,Network:true)),LocationFault.Remote},
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void Each_reason_InstallLocation_gives_is_read_as_its_fault_and_the_path_it_names(string reason,LocationFault fault)
    {
        Assert.Equal((fault,Entry),LocationReason.Read(reason));
        Assert.Equal(fault is not (LocationFault.Writable or LocationFault.NotOwned),LocationReason.CantCheck(fault));
        Assert.Equal(LocationReason.CantCheck(fault),OpenRgbFolderRefusal.CantCheck(reason));
    }

    [Fact]
    public void Nothing_checked_names_no_path_and_couldnt_be_judged()
    {
        Assert.Equal((LocationFault.NothingChecked,(string?)null),LocationReason.Read(Reason()));
        Assert.Equal((LocationFault.NothingChecked,(string?)null),LocationReason.Read(null));
        Assert.True(LocationReason.CantCheck(LocationFault.NothingChecked));
    }

    // A network path's own reason, and an unreadable entry whose error text happens to end like another reason, keep their own fault.
    [Fact]
    public void A_network_path_and_an_odd_error_text_keep_their_own_fault()
    {
        Assert.Equal((LocationFault.Remote,@"\\server\share\OpenRGB"),LocationReason.Read(Reason(new PathSecurity(@"\\server\share\OpenRGB",PathKind.Directory,"S-1-5-32-544",[],false))));
        Assert.Equal((LocationFault.Unreadable,Entry),LocationReason.Read($"Permissions for {Entry} could not be read: {Entry} is a link or junction."));
    }

    // A reason in no form InstallLocation uses counts as one that lets other programs change the folder, as the OpenRGB folder's refusal
    // has always counted it, and names no path.
    [Fact]
    public void A_reason_in_no_known_form_counts_as_writable_and_names_no_path()
    {
        Assert.Equal((LocationFault.Writable,(string?)null),LocationReason.Read("Something else went wrong."));
        Assert.False(OpenRgbFolderRefusal.CantCheck("Something else went wrong."));
    }
}

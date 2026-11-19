using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Polish item 8 (B12/B21): the elevated host's refusal of OpenRGB's folder says the folder can be changed only when InstallLocation found
// that; a folder it couldn't judge gets the can't-check words with the reason. Settings recognises either, with or without a note after it.
public class OpenRgbFolderRefusalTests
{
    [Theory]
    [InlineData(@"C:\Tools\OpenRGB can be changed by S-1-5-32-545.")]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe can be changed by S-1-5-11.")]
    [InlineData(@"C:\Tools\OpenRGB is not owned by SYSTEM, Administrators or TrustedInstaller.")]
    public void A_folder_other_programs_can_change_gets_the_writable_text(string reason)
    {
        Assert.False(OpenRgbFolderRefusal.CantCheck(reason));
        Assert.Equal(OpenRgbFolderRefusal.WritableText,OpenRgbFolderRefusal.Text(reason));
    }

    [Theory]
    [InlineData(@"Permissions for C:\Tools\OpenRGB could not be read: The path does not exist.")]
    [InlineData(@"Permissions for C:\Tools\OpenRGB\plugins could not be read: Access to the path is denied.")]
    [InlineData(@"\\server\share\OpenRGB is not on a local drive.")]
    [InlineData(@"C:\Tools\OpenRGB is a link or junction.")]
    [InlineData("No folder permissions were checked.")]
    public void A_folder_that_couldnt_be_judged_gets_the_cant_check_text_with_the_reason(string reason)
    {
        Assert.True(OpenRgbFolderRefusal.CantCheck(reason));
        var text=OpenRgbFolderRefusal.Text(reason);
        Assert.StartsWith("DeadWare can't check the OpenRGB folder, so it won't run OpenRGB as administrator: ",text);
        Assert.Contains(reason,text);
        Assert.EndsWith(@"Copy OpenRGB to C:\Program Files\OpenRGB and choose it in Settings → Lighting.",text);
        Assert.DoesNotContain("can be changed",text);
    }

    [Fact]
    public void Both_refusals_are_recognised_with_or_without_a_note_after_them()
    {
        var cantCheck=OpenRgbFolderRefusal.Text(@"C:\Tools\OpenRGB is a link or junction.");
        Assert.True(OpenRgbFolderRefusal.Is(OpenRgbFolderRefusal.WritableText));
        Assert.True(OpenRgbFolderRefusal.Is(cantCheck));
        Assert.True(OpenRgbFolderRefusal.Is(cantCheck+" Your lighting settings file couldn't be read."));
        Assert.False(OpenRgbFolderRefusal.Is("OpenRGB stopped 5 times in 10 minutes."));
        Assert.False(OpenRgbFolderRefusal.Is(""));
        Assert.False(OpenRgbFolderRefusal.Is(null));
    }
}

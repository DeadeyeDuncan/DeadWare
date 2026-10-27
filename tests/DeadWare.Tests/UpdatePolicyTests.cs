using System.Text.Json;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

public class UpdatePolicyTests
{
    internal static string Release(string tag = "v0.6.1", bool preview = false, bool draft = false, string? assetUrl = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, prerelease = preview, draft, body = "Release notes",
        assets = new object[] {
            new { name = "DeadWare-0.6.1-win-x64.zip", size = 123, digest = "sha256:" + new string('a',64),
                browser_download_url = assetUrl ?? "https://github.com/DeadeyeDuncan/DeadWare/releases/download/v0.6.1/DeadWare-0.6.1-win-x64.zip" },
            new { name = "DeadWare-0.6.1-win-x64.zip.sha256", size = 94, digest = "sha256:" + new string('b',64),
                browser_download_url = "https://github.com/DeadeyeDuncan/DeadWare/releases/download/v0.6.1/DeadWare-0.6.1-win-x64.zip.sha256" }
        }
    });

    [Fact] public void Normal_release_is_validated_and_compared_numerically()
    {
        var release = ReleasePolicy.Parse(Release())!;
        Assert.Equal(new Version(0,6,1), release.Version);
        Assert.True(ReleasePolicy.IsNewer(release, "0.6.0"));
        Assert.False(ReleasePolicy.IsNewer(release, "0.6.1+sourcecommit"));
        Assert.False(ReleasePolicy.IsNewer(release, "0.10.0"));
        Assert.Equal("Release notes", release.Notes);
    }
    [Theory]
    [InlineData(true,false)] [InlineData(false,true)]
    public void Drafts_and_previews_are_excluded(bool preview, bool draft) => Assert.Null(ReleasePolicy.Parse(Release(preview:preview,draft:draft)));
    [Theory] [InlineData("v0.6.1-rc.1")] [InlineData("v01.6.1")] [InlineData("latest")] [InlineData("v0.6")]
    public void Ambiguous_or_preview_tags_are_rejected(string tag) => Assert.Throws<InvalidDataException>(()=>ReleasePolicy.Parse(Release(tag)));
    [Fact] public void Foreign_assets_and_incomplete_publication_are_rejected()
    {
        Assert.Throws<InvalidDataException>(()=>ReleasePolicy.Parse(Release(assetUrl:"https://example.org/update.zip")));
        Assert.Throws<InvalidDataException>(()=>ReleasePolicy.Parse(Release().Replace("sha256:","unknown:")));
        Assert.Throws<InvalidDataException>(()=>ReleasePolicy.Parse(Release().Replace("DeadWare-0.6.1-win-x64.zip.sha256","other.zip")));
    }
    [Fact] public void Sidecar_hash_must_match_digest_and_exact_asset_name()
    {
        var release=ReleasePolicy.Parse(Release())!;
        ReleasePolicy.VerifyChecksum(release,new string('a',64)+"  DeadWare-0.6.1-win-x64.zip\n");
        Assert.Throws<InvalidDataException>(()=>ReleasePolicy.VerifyChecksum(release,new string('b',64)+"  DeadWare-0.6.1-win-x64.zip"));
        Assert.Throws<InvalidDataException>(()=>ReleasePolicy.VerifyChecksum(release,new string('a',64)+"  other.zip"));
    }
}

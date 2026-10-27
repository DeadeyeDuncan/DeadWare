using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadWare.UpdateCore;

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, Uri Download, Uri Checksum, string Sha256, long Size)
{
    public string FileName => $"DeadWare-{Version}-win-x64.zip";
    public Uri Page => new($"https://github.com/DeadeyeDuncan/DeadWare/releases/tag/{Tag}");
}

public static partial class ReleasePolicy
{
    public const string Endpoint = "https://api.github.com/repos/DeadeyeDuncan/DeadWare/releases/latest";
    public const long MaximumDownloadBytes = 256L * 1024 * 1024;
    public static Version VersionOf(string text)
    {
        if(string.IsNullOrWhiteSpace(text))throw new InvalidDataException("A normal three-part version is required.");
        string value = text.Split('+')[0];
        if (!VersionPattern().IsMatch(value) || !Version.TryParse(value, out var version))
            throw new InvalidDataException("A normal three-part version is required.");
        return version;
    }
    public static bool IsNewer(ReleaseInfo release, string installed) => release.Version > VersionOf(installed);
    public static ReleaseInfo? Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string versionText = tag.StartsWith('v') ? tag[1..] : tag;
            if (versionText.Contains('+')) throw new InvalidDataException("Release tags cannot contain build metadata.");
            var version = VersionOf(versionText); string name = $"DeadWare-{version}-win-x64.zip";
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            JsonElement Asset(string expected)
            {
                var matches = assets.Where(a => a.GetProperty("name").GetString() == expected).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("The release package is not completely published yet.");
                return matches[0];
            }
            var zip = Asset(name); var checksum = Asset(name + ".sha256");
            Uri Url(JsonElement asset, string file)
            {
                string expected = $"https://github.com/DeadeyeDuncan/DeadWare/releases/download/{tag}/{file}";
                if (asset.GetProperty("browser_download_url").GetString() != expected)
                    throw new InvalidDataException("The release asset is outside the expected repository.");
                return new(expected);
            }
            string digest = zip.GetProperty("digest").GetString() ?? "";
            long size = zip.GetProperty("size").GetInt64();
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !HashPattern().IsMatch(digest[7..]) ||
                size <= 0 || size > MaximumDownloadBytes || checksum.GetProperty("size").GetInt64() is <= 0 or > 4096)
                throw new InvalidDataException("The release package has invalid size or verification metadata.");
            return new(version, tag, root.TryGetProperty("body", out var body) ? (body.GetString() ?? "")[..Math.Min(16000, (body.GetString() ?? "").Length)] : "",
                Url(zip, name), Url(checksum, name + ".sha256"), digest[7..].ToLowerInvariant(), size);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("GitHub returned incomplete release metadata.", error); }
    }
    public static void VerifyChecksum(ReleaseInfo release, string text)
    {
        if (text.Length > 4096) throw new InvalidDataException("The checksum file is too large.");
        var match = Regex.Match(text.Trim('\uFEFF', ' ', '\r', '\n'), @"\A([a-fA-F0-9]{64})[ \t]+\*?([^\r\n]+)\z");
        if (!match.Success || !match.Groups[1].Value.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase) ||
            match.Groups[2].Value != release.FileName) throw new InvalidDataException("Release checksum and asset digest do not agree.");
    }
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A[a-fA-F0-9]{64}\z")] internal static partial Regex HashPattern();
}

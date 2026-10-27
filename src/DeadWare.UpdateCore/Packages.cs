using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadWare.UpdateCore;

public sealed record PackageManifest(int Format, string Version, Dictionary<string, string> Files);

public static class SafeFiles
{
    public static string DirectoryPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length <= (Path.GetPathRoot(full)?.Length ?? 0)) throw new InvalidDataException("An application directory is required.");
        NoLinks(full); return full;
    }
    public static void RelativeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name) || name.Any(char.IsControl))
            throw new InvalidDataException("Invalid package path.");
        foreach (string part in name.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid package path component.");
    }
    public static string Child(string root, string relative)
    {
        RelativeName(relative); root = DirectoryPath(root);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escaped its directory.");
        NoLinks(full); return full;
    }
    public static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update paths cannot contain links or junctions.");
    }
    public static void WriteJson<T>(string path, T value)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    public static T ReadJson<T>(string path)
    {
        NoLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Update metadata is too large.");
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty update metadata."); }
        catch (JsonException error) { throw new InvalidDataException("Invalid update metadata.", error); }
    }
}

public static class Packages
{
    public const string ManifestName = "deadware-package.json";
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static void VerifyDownload(string path, string digest, long expectedSize)
    {
        if (new FileInfo(path).Length != expectedSize || expectedSize > ReleasePolicy.MaximumDownloadBytes ||
            !ReleasePolicy.HashPattern().IsMatch(digest) || !Hash(path).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded package verification failed.");
    }
    public static PackageManifest ReadManifest(string root, string? expectedVersion = null)
    {
        var manifest = SafeFiles.ReadJson<PackageManifest>(SafeFiles.Child(root, ManifestName));
        if (manifest.Format != 1 || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files is null || manifest.Files.Count is < 3 or > 2048 ||
            expectedVersion is not null && manifest.Version != expectedVersion) throw new InvalidDataException("Unsupported or incorrect package manifest.");
        ReleasePolicy.VersionOf(manifest.Version);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in manifest.Files)
        {
            SafeFiles.RelativeName(pair.Key);
            if (!paths.Add(pair.Key) || pair.Key.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                pair.Value is null || !ReleasePolicy.HashPattern().IsMatch(pair.Value)) throw new InvalidDataException("Invalid package file manifest.");
        }
        foreach (string required in new[] { "DeadWare.exe", "DeadWare.dll", "DeadWare.Updater.exe" })
            if (!paths.Contains(required)) throw new InvalidDataException("Required app files are missing.");
        return manifest;
    }
    public static PackageManifest ValidateDirectory(string root, string? expectedVersion = null)
    {
        var manifest = ReadManifest(root, expectedVersion);
        foreach (var pair in manifest.Files)
            if (!Hash(SafeFiles.Child(root, pair.Key)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An application file has changed: " + pair.Key);
        return manifest;
    }
    public static PackageManifest Extract(string zipPath, string destination, string expectedVersion, long maximumExpandedBytes = 768L * 1024 * 1024)
    {
        SafeFiles.DirectoryPath(destination);
        if (Directory.Exists(destination)) throw new InvalidDataException("Package staging must use a new directory.");
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count is < 4 or > 2048) throw new InvalidDataException("Unexpected package size.");
        long expanded = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("DeadWare/", StringComparison.Ordinal) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Unexpected package layout or symbolic link.");
            string relative = entry.FullName[9..].TrimEnd('/');
            if (relative.Length == 0) continue;
            SafeFiles.RelativeName(relative);
            if(relative.Equals(ManifestName,StringComparison.OrdinalIgnoreCase)&&entry.Length>1024*1024)throw new InvalidDataException("The package manifest is too large.");
            if (!names.Add(relative) || entry.Length < 0 || entry.Length > maximumExpandedBytes - expanded)
                throw new InvalidDataException("Duplicate package paths or expanded-size limit exceeded.");
            expanded += entry.Length;
        }
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                string file = SafeFiles.Child(destination, entry.FullName[9..]);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var input = entry.Open(); using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer=new byte[65536];long copied=0;int count;
                while((count=input.Read(buffer))>0)
                {
                    if(copied>entry.Length-count)throw new InvalidDataException("Expanded package data exceeds its declared size.");
                    output.Write(buffer,0,count);copied+=count;
                }
                if (output.Length != entry.Length) throw new InvalidDataException("Incorrect extracted file size.");
            }
            var manifest = ValidateDirectory(destination, expectedVersion);
            var actual = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(destination, f).Replace('\\', '/'));
            if (!actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(manifest.Files.Keys.Append(ManifestName)))
                throw new InvalidDataException("The package contains files outside its manifest.");
            return manifest;
        }
        catch { throw; } // The owned staging directory stays available for diagnostics, never applied.
    }
}

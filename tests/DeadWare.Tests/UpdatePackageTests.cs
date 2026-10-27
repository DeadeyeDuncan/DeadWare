using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

public class UpdatePackageTests
{
    [Fact] public void Complete_package_extracts_and_manifest_detects_tampering()
    {
        using var fixture=new UpdateFixture(); string zip=fixture.Zip(); string stage=Path.Combine(fixture.Root,"stage");
        var manifest=Packages.Extract(zip,stage,"0.6.1"); Assert.Equal("0.6.1",manifest.Version);
        Assert.Equal("new app",File.ReadAllText(Path.Combine(stage,"DeadWare.exe")));
        Packages.ValidateDirectory(stage,"0.6.1");
        File.WriteAllText(Path.Combine(stage,"DeadWare.exe"),"tampered");
        Assert.Throws<InvalidDataException>(()=>Packages.ValidateDirectory(stage,"0.6.1"));
    }
    [Theory]
    [InlineData("../outside.exe")] [InlineData("DeadWare/../outside.exe")]
    [InlineData("DeadWare/C:/outside.exe")] [InlineData("DeadWare/a:stream")]
    [InlineData("DeadWare/CON.txt")] [InlineData("DeadWare/file. ")]
    [InlineData("DeadWare\\outside.exe")]
    public void Unsafe_archive_names_are_rejected_before_extraction(string name)
    {
        using var fixture=new UpdateFixture(); string zip=fixture.Zip(extra:name);
        Assert.Throws<InvalidDataException>(()=>Packages.Extract(zip,Path.Combine(fixture.Root,"stage"),"0.6.1"));
        Assert.False(File.Exists(Path.Combine(fixture.Root,"outside.exe")));
    }
    [Fact] public void Case_collisions_extra_files_and_wrong_versions_are_rejected()
    {
        using var fixture=new UpdateFixture();
        Assert.Throws<InvalidDataException>(()=>Packages.Extract(fixture.Zip(extra:"DeadWare/DEADWARE.EXE"),Path.Combine(fixture.Root,"a"),"0.6.1"));
        Assert.Throws<InvalidDataException>(()=>Packages.Extract(fixture.Zip(extra:"DeadWare/unknown.exe"),Path.Combine(fixture.Root,"b"),"0.6.1"));
        Assert.Throws<InvalidDataException>(()=>Packages.Extract(fixture.Zip(),Path.Combine(fixture.Root,"c"),"0.7.0"));
    }
    [Fact] public void Corrupt_zip_digest_and_size_limits_fail_without_replacing_installation()
    {
        using var fixture=new UpdateFixture(); string zip=fixture.Zip();
        Assert.Throws<InvalidDataException>(()=>Packages.VerifyDownload(zip,new string('0',64),new FileInfo(zip).Length));
        Assert.Throws<InvalidDataException>(()=>Packages.Extract(zip,Path.Combine(fixture.Root,"stage"),"0.6.1",maximumExpandedBytes:1));
    }
}

internal sealed class UpdateFixture : IDisposable
{
    public string Root {get;}=Path.Combine(Path.GetTempPath(),"DeadWare-update-tests-"+Guid.NewGuid().ToString("N"));
    public UpdateFixture()=>Directory.CreateDirectory(Root);
    public string Package(string name,string version,string content)
    {
        string path=Path.Combine(Root,name); Directory.CreateDirectory(path);
        var files=new Dictionary<string,string>{{"DeadWare.exe",content},{"DeadWare.dll","assembly"},{"DeadWare.Updater.exe","helper"}};
        foreach(var pair in files)File.WriteAllText(Path.Combine(path,pair.Key),pair.Value);
        var manifest=new PackageManifest(1,version,files.ToDictionary(p=>p.Key,p=>Hash(Encoding.UTF8.GetBytes(p.Value))));
        File.WriteAllText(Path.Combine(path,Packages.ManifestName),JsonSerializer.Serialize(manifest)); return path;
    }
    public string Zip(string? extra=null)
    {
        string path=Path.Combine(Root,Guid.NewGuid()+".zip"); string source=Package(Guid.NewGuid().ToString("N"),"0.6.1","new app");
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        foreach(string file in Directory.GetFiles(source))zip.CreateEntryFromFile(file,"DeadWare/"+Path.GetFileName(file));
        if(extra is not null){using var writer=new StreamWriter(zip.CreateEntry(extra).Open());writer.Write("untrusted");}
        return path;
    }
    public void AddFile(string package,string name,string value)
    {
        File.WriteAllText(Path.Combine(package,name),value);
        var manifest=Packages.ReadManifest(package);manifest.Files[name]=Hash(Encoding.UTF8.GetBytes(value));
        File.WriteAllText(Path.Combine(package,Packages.ManifestName),JsonSerializer.Serialize(manifest));
    }
    public static string Hash(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
}

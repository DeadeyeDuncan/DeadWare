using System.Globalization;
using DeadWare.Core;

namespace DeadWare.Devices;

// Owns lighting.json. Only content that was read and found invalid is quarantined; IO errors propagate and leave the file alone.
internal sealed class LightingStore(string path)
{
    public LightingRead Load()
    {
        if(!File.Exists(path)) return new(new(),null,false);
        var json=File.ReadAllText(path);
        var read=LightingSettingsCodec.Read(json);
        if(read.Quarantine) Quarantine();
        return read;
    }
    public void Save(LightingSettings settings)
    {
        var json=LightingSettingsCodec.Write(settings);
        var dir=Path.GetDirectoryName(Path.GetFullPath(path));
        if(!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp=path+".tmp";
        File.WriteAllText(tmp,json);
        File.Move(tmp,path,overwrite:true);
    }
    void Quarantine()
    {
        var full=Path.GetFullPath(path);
        var dir=Path.GetDirectoryName(full)!;
        var stamp=DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);
        for(var i=0;;i++)
        {
            var name=Path.Combine(dir,i==0?$"lighting.corrupt-{stamp}.json":$"lighting.corrupt-{stamp}-{i}.json");
            if(File.Exists(name)) continue;
            try{File.Move(full,name);return;}
            catch(IOException) when(File.Exists(name)){}
        }
    }
}

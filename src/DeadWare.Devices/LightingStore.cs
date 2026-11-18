using System.Globalization;
using DeadWare.Core;
using DeadWare.UpdateCore;

namespace DeadWare.Devices;

// Owns lighting.json. Only content that was read and found invalid is quarantined; IO errors propagate and leave the file alone.
// Saves go through a fresh temporary name, as SafeFiles writes the updater's metadata, so a planted lighting.json.tmp can neither block
// nor receive one. guardLinks (an elevated host, Ruling AT): every read, save and quarantine first refuses a path through a link or
// junction with SafeFiles.NoLinks, the defence the updater uses for its own writes under %LOCALAPPDATA%\DeadWare, so a program without
// administrator rights can't steer the host's file operations elsewhere. Like the updater's, it is a check before use.
internal sealed class LightingStore(string path,bool guardLinks=false)
{
    public LightingRead Load()
    {
        Guard(path);
        if(!File.Exists(path)) return new(new(),null,false);
        var json=File.ReadAllText(path);
        var read=LightingSettingsCodec.Read(json);
        if(read.Quarantine) Quarantine();
        return read;
    }
    public void Save(LightingSettings settings)
    {
        var json=LightingSettingsCodec.Write(settings);
        Guard(path);
        var dir=Path.GetDirectoryName(Path.GetFullPath(path));
        if(!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(stream)){writer.Write(json);writer.Flush();stream.Flush(true);}
            File.Move(tmp,path,overwrite:true);
        }
        finally{if(File.Exists(tmp)) File.Delete(tmp);}
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
            Guard(full);Guard(name);
            try{File.Move(full,name);return;}
            catch(IOException) when(File.Exists(name)){}
        }
    }
    void Guard(string target)
    {
        if(!guardLinks) return;
        try{SafeFiles.NoLinks(target);}
        catch(InvalidDataException e){throw new IOException("lighting.json's path goes through a link or junction, so the lighting helper running as administrator won't use it.",e);}
    }
}

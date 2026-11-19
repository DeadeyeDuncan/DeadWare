using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using DeadWare.Core;
using DeadWare.UpdateCore;
using Microsoft.Win32.SafeHandles;

namespace DeadWare.Devices;

// Owns lighting.json. Only content that was read and found invalid is quarantined; IO errors propagate and leave the file alone.
// Saves go through a fresh temporary name, as SafeFiles writes the updater's metadata, so a planted lighting.json.tmp can neither block
// nor receive one. guardLinks (an elevated host, Ruling AT): every read, save and quarantine first refuses a path through a link or
// junction with SafeFiles.NoLinks, the defence the updater uses for its own writes under %LOCALAPPDATA%\DeadWare, so a program without
// administrator rights can't steer the host's file operations elsewhere. Like the updater's, it is a check before use.
// move: puts a written temporary file in place; File.Move unless a test stands in a failing one.
internal sealed class LightingStore(string path,bool guardLinks=false,Action<string,string>? move=null)
{
    readonly Action<string,string> moveFile=move??(static (from,to)=>File.Move(from,to,overwrite:true));
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
        // B2: the temporary file stays open, with delete access, until it is in place. If the move fails it is deleted through that handle,
        // never looked up again by name, so a program that changed what the name leads to meanwhile (a link in place of the folder, say)
        // can't turn the cleanup into a delete of its choosing, as administrator. Sharing delete lets the move rename it while it is open.
        using var stream=new FileInfo(tmp).Create(FileMode.CreateNew,FileSystemRights.Write|FileSystemRights.Delete,FileShare.Delete,4096,FileOptions.None,null);
        var moved=false;
        try
        {
            using(var writer=new StreamWriter(stream,leaveOpen:true)) writer.Write(json);
            stream.Flush(true);
            moveFile(tmp,path);
            moved=true;
        }
        finally{if(!moved) DeleteOnClose(stream.SafeFileHandle);}
    }
    // Marks the file this handle has open for deletion once the handle closes. A failure leaves a stray temporary file, which is harmless.
    static void DeleteOnClose(SafeFileHandle file)
    {
        byte delete=1;
        SetFileInformationByHandle(file,FileDispositionInfo,ref delete,sizeof(byte));
    }
    const int FileDispositionInfo=4; // FILE_DISPOSITION_INFO, whose one member is the BOOLEAN DeleteFile
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file,int fileInformationClass,ref byte fileInformation,int bufferSize);
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

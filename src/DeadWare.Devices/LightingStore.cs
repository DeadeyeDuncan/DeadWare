using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using DeadWare.Core;
using DeadWare.UpdateCore;
using Microsoft.Win32.SafeHandles;

namespace DeadWare.Devices;

// lighting.json's path goes through a link or junction, is redirected, so that the file opened has another final path than the one asked
// for, or ends at a file whose final path Windows can't give: an elevated host refuses all three (Ruling AT, F2 item 1). A type of its own,
// so the host can say why it runs on defaults: a restart doesn't change it (B3/B14/B17/B24).
internal sealed class LinkedPathException(string message,Exception? inner=null):IOException(message,inner);

// Owns lighting.json. Only content that was read and found invalid is quarantined; IO errors propagate and leave the file alone. Each
// operation opens its file once and from then on works on that open file, never on a name looked up again (whole-branch review D13/D14):
// Save writes lighting.json.<guid>.tmp, a fresh name, so a planted lighting.json.tmp can neither block nor receive it, and renames that open
// file to lighting.json; Load reads the open file and sets a damaged one aside by renaming that same file. guardLinks (an elevated host,
// Ruling AT): before the open, SafeFiles.NoLinks refuses a path through a link or junction, the check before use the updater makes for its
// own writes under %LOCALAPPDATA%\DeadWare; after it, the open file's final path must be the path asked for, so a link put in place
// between the check and the open is refused too, as is any other redirection and a final path Windows can't give (F2 item 1). What an
// elevated host reads, renames or replaces is then the file it checked, never one a program without administrator rights pointed the
// name at through a link, a junction or another redirection on the way. One route is outside what these checks cover (whole-branch review
// D11): the path is built from %LOCALAPPDATA%, which the host takes from the user's known-folder setting in HKCU, and such a program can
// change that setting with no link involved. The path then leads to another folder, yet both checks pass, since the path asked for comes
// from that same setting. Not trusting it in an elevated host is a follow-up.
// place: renames the written temporary file, through its handle, to lighting.json; a test can stand in a failing one. write: writes the
// settings' JSON to the open temporary file; a StreamWriter unless a test stands in a failing one. race: a test seam, called with "checked"
// once Guard has passed and before the file is opened, and with "read" once Load has read a file it will set aside; a test acts there as a
// program without administrator rights could.
internal sealed class LightingStore(string path,bool guardLinks=false,Action<SafeFileHandle,string>? place=null,Action<Stream,string>? write=null,Action<string>? race=null)
{
    readonly Action<SafeFileHandle,string> placeFile=place??PutInPlace;
    readonly Action<Stream,string> writeFile=write??(static (stream,json)=>{using var writer=new StreamWriter(stream,leaveOpen:true);writer.Write(json);});

    public LightingRead Load()
    {
        Guard(path);
        race?.Invoke("checked");
        var full=Path.GetFullPath(path);
        SafeFileHandle file;
        // Read access only, as File.ReadAllText asks, so a valid file loads wherever ReadAllText would have read it; a file that is missing,
        // or gone before it could be opened, means defaults. Shared for reading, as ReadAllText shares it, and for delete, so that a damaged
        // file can be opened again below with the delete access a rename needs while this handle is still open.
        try{file=File.OpenHandle(full,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,FileOptions.SequentialScan);}
        catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){return new(new(),null,false);}
        LightingRead read;
        SafeFileHandle? aside=null;
        using(file)
        {
            RequireOpened(file,full);
            read=LightingSettingsCodec.Read(ReadText(file));
            if(read.Quarantine){race?.Invoke("read");aside=Reopen(file);}
        }
        if(aside is not null) using(aside) Quarantine(aside);
        return read;
    }

    public void Save(LightingSettings settings)
    {
        var json=LightingSettingsCodec.Write(settings);
        Guard(path);
        race?.Invoke("checked");
        var full=Path.GetFullPath(path);
        var dir=Path.GetDirectoryName(full);
        if(!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        // B2 and D13: the temporary file is new, opened with delete access and shared with no one, so until it is in place no other program
        // can open it, to read, delete or rename it. It is checked, written and renamed into place through this one handle, and if anything
        // fails before the rename it is deleted through the handle too, never looked up again by name. Polish round 2, item 1: the stream
        // buffers nothing (a buffer size of 1), and the handle is taken while it is new. A buffering stream writes what it holds whenever its
        // handle is asked for and when it closes, so after a write that failed it would write the same bytes again, throw in place of the
        // write's own exception and skip the delete.
        using var stream=new FileInfo(tmp).Create(FileMode.CreateNew,FileSystemRights.Write|FileSystemRights.Delete,FileShare.None,1,FileOptions.None,null);
        var handle=stream.SafeFileHandle;
        var placed=false;
        try
        {
            RequireOpened(handle,tmp);
            writeFile(stream,json);
            stream.Flush(true);
            placeFile(handle,Path.GetFileName(full));
            placed=true;
        }
        finally{if(!placed) DeleteOnClose(handle);}
    }

    // guardLinks: the file a handle has open must be the one asked for. Its final path is where the open ended up, with any link or junction
    // on the way followed, so a folder swapped for a junction after Guard shows there, as does any other redirection. A final path Windows
    // can't give is refused too, since the host then can't confirm which file it opened (F2 item 1): GetFinalPathNameByHandleW gives none in
    // DOS form on a drive the Mount Manager didn't create, for one. Internal for a test, which hands it a handle with no such path.
    internal void RequireOpened(SafeFileHandle file,string expected)
    {
        if(!guardLinks) return;
        var final=FinalPath(file,out var error);
        if(final is null)
            throw new LinkedPathException($"Windows couldn't give the final path of the file opened for lighting.json (error {error}: {new Win32Exception(error).Message.TrimEnd('.')}), so the lighting helper running as administrator won't use it.");
        if(!string.Equals(final,expected,StringComparison.OrdinalIgnoreCase))
            throw new LinkedPathException($"lighting.json's path led to {final}, so the lighting helper running as administrator won't use it.");
    }

    // GetFinalPathNameByHandleW with 0, FILE_NAME_NORMALIZED and VOLUME_NAME_DOS: long names and the drive letter; null, with the Windows
    // error, when it can't give that path. A buffer too small gets back the size needed, its terminating null included.
    internal static string? FinalPath(SafeFileHandle file,out int error)
    {
        var buffer=new char[260];
        while(true)
        {
            var length=GetFinalPathNameByHandleW(file,buffer,(uint)buffer.Length,0);
            if(length==0){error=Marshal.GetLastPInvokeError();return null;}
            if(length<buffer.Length){error=0;return DosPath(new string(buffer,0,(int)length));}
            buffer=new char[length];
        }
    }
    // A final path in the \\?\ form GetFinalPathNameByHandleW returns, written as a path usually is: \\?\C:\… as C:\…, and \\?\UNC\server\…
    // as \\server\….
    internal static string DosPath(string final)
        =>final.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)?@"\\"+final[8..]:final.StartsWith(@"\\?\",StringComparison.Ordinal)?final[4..]:final;

    // The text File.ReadAllText would return: UTF-8 unless a byte order mark says otherwise.
    static string ReadText(SafeFileHandle file)
    {
        using var bytes=new MemoryStream();
        var buffer=new byte[4096];
        for(int count;(count=RandomAccess.Read(file,buffer,bytes.Length))>0;) bytes.Write(buffer,0,count);
        bytes.Position=0;
        using var reader=new StreamReader(bytes,Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
        return reader.ReadToEnd();
    }

    // Renames the written temporary file to lighting.json, replacing the one there, in the folder the open file is in (B2/D13).
    static void PutInPlace(SafeFileHandle file,string name)
    {
        var status=Rename(file,name,replace:true);
        if(status!=0) throw Failure("lighting.json couldn't be put in place",status);
    }

    // The file Load read, opened again through the handle it was read with, so no name is looked up, with the delete access a rename needs.
    // Shared for reading and delete in turn, so the read handle, still open while this one is made, doesn't stand in its way.
    static SafeFileHandle Reopen(SafeFileHandle read)
    {
        var file=ReOpenFile(read,DeleteAccess|Synchronize,ShareRead|ShareDelete,0);
        if(!file.IsInvalid) return file;
        var error=Marshal.GetLastPInvokeError();
        file.Dispose();
        throw new IOException("lighting.json couldn't be opened to set it aside: "+new Win32Exception(error).Message);
    }

    // Sets a damaged file aside by renaming it, through its handle, to lighting.corrupt-<stamp>.json, or -1, -2… while the name is taken,
    // replacing nothing. Since the read handle shares delete, a program could move the file meanwhile, into a folder it can write to; that
    // changes only the folder the file is renamed in, never which file is renamed.
    static void Quarantine(SafeFileHandle file)
    {
        var stamp=DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);
        for(var i=0;;i++)
        {
            var status=Rename(file,i==0?$"lighting.corrupt-{stamp}.json":$"lighting.corrupt-{stamp}-{i}.json",replace:false);
            if(status==0) return;
            if(status!=NameCollision) throw Failure("lighting.json couldn't be set aside",status);
        }
    }

    // FILE_RENAME_INFORMATION with a simple file name and no RootDirectory: the file "is simply renamed within the same directory", the folder
    // the open file is in, and no name above it is looked up. Through ntdll's NtSetInformationFile: the FILE_RENAME_INFO that kernel32's
    // SetFileInformationByHandle takes reads a name without a folder as relative to the process's current directory. The handle needs delete
    // access. Returns the NTSTATUS: 0, or NameCollision when the name is taken and replace is false.
    internal static int Rename(SafeFileHandle file,string name,bool replace)
    {
        if(name.Length==0||name.IndexOfAny(['\\','/'])>=0) throw new ArgumentException("A file name without a folder is required.",nameof(name));
        // ReplaceIfExists, a BOOLEAN in a ULONG union padded to a pointer's alignment; RootDirectory, NULL; FileNameLength in bytes; FileName.
        var nameAt=2*IntPtr.Size+sizeof(uint);
        var size=(nameAt+sizeof(char)+IntPtr.Size-1)/IntPtr.Size*IntPtr.Size; // sizeof(FILE_RENAME_INFORMATION)
        var info=new byte[size+name.Length*sizeof(char)];
        info[0]=replace?(byte)1:(byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(2*IntPtr.Size),(uint)(name.Length*sizeof(char)));
        Encoding.Unicode.GetBytes(name,info.AsSpan(nameAt));
        return NtSetInformationFile(file,out _,info,(uint)info.Length,FileRenameInformation);
    }
    static IOException Failure(string what,int status)=>new(what+": "+new Win32Exception((int)RtlNtStatusToDosError(status)).Message);

    // Marks the file this handle has open for deletion once the handle closes. It never throws, so the exception that ended the save is the
    // one that surfaces; a failure leaves a stray temporary file, which is harmless.
    static void DeleteOnClose(SafeFileHandle file)
    {
        byte delete=1;
        try{SetFileInformationByHandle(file,FileDispositionInfo,ref delete,sizeof(byte));}catch(Exception){}
    }

    void Guard(string target)
    {
        if(!guardLinks) return;
        try{SafeFiles.NoLinks(target);}
        catch(InvalidDataException e){throw new LinkedPathException("lighting.json's path goes through a link or junction, so the lighting helper running as administrator won't use it.",e);}
    }

    const int FileDispositionInfo=4; // FILE_DISPOSITION_INFO, whose one member is the BOOLEAN DeleteFile
    const int FileRenameInformation=10; // FILE_INFORMATION_CLASS
    internal const int NameCollision=unchecked((int)0xC0000035); // STATUS_OBJECT_NAME_COLLISION
    const uint DeleteAccess=0x00010000,Synchronize=0x00100000,ShareRead=1,ShareDelete=4;
    [StructLayout(LayoutKind.Sequential)] struct IoStatusBlock{public IntPtr Status;public IntPtr Information;}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file,int fileInformationClass,ref byte fileInformation,int bufferSize);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,[Out] char[] path,uint length,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeFileHandle ReOpenFile(SafeFileHandle original,uint access,uint share,uint flags);
    [DllImport("ntdll.dll")] static extern int NtSetInformationFile(SafeFileHandle file,out IoStatusBlock status,byte[] information,uint length,int informationClass);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
}

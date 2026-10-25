using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace DeadWare.App;

/// <summary>Opt-in local layout trace. No collection data is included; failures never affect the app.</summary>
internal sealed class LayoutDiagnostics
{
    private const int MaximumBytes=128*1024;
    private static readonly byte[] Header=Encoding.UTF8.GetBytes("{\"DeadWareLayoutTrace\":1,\"Format\":\"jsonl\"}"+Environment.NewLine);
    private readonly object gate=new();
    public string OutputPath {get;}
    public LayoutDiagnostics(string outputPath,string settingsPath)
    {
        OutputPath=ResolveOutputPath(outputPath);
        if(string.Equals(OutputPath,Path.GetFullPath(settingsPath),StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a layout diagnostic output separate from the settings file.");
    }
    public static string? RequestedPath(string[] args)
    {
        int index=Array.IndexOf(args,"--layout-diagnostics");if(index<0)return null;
        if(index+1>=args.Length)
            throw new ArgumentException("Provide an absolute output file after --layout-diagnostics.");
        return ResolveOutputPath(args[index+1]);
    }
    private static string ResolveOutputPath(string path)
    {
        // Diagnostics support ordinary drive paths, not device namespaces, UNC or alternate streams.
        if(path.Length<3||!char.IsAsciiLetter(path[0])||path[1]!=':'||path[2] is not ('\\' or '/')||path.AsSpan(2).Contains(':'))
            throw new ArgumentException("Use an ordinary absolute drive path for layout diagnostics, such as C:\\Logs\\layout.jsonl.");
        string resolved=Path.GetFullPath(path);
        if(resolved[3..].Split(['\\','/']).Any(part=>Regex.IsMatch(part.Split('.')[0].TrimEnd(' '),@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)))
            throw new ArgumentException("A layout diagnostic output must be a regular file, not a Windows device.");
        return resolved;
    }
    private FileStream OpenOwnedTrace()
    {
        FileStream file;
        try
        {
            file=new FileStream(OutputPath,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        }
        catch(IOException)
        {
            // An alias may reach any existing file. Validate its header on the same handle
            // used for writing, while denying other writers and replacement/deletion.
            file=new FileStream(OutputPath,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);
            try
            {
                byte[] header=new byte[Header.Length];file.ReadExactly(header);
                if(!header.AsSpan().SequenceEqual(Header))throw new IOException("The existing output is not an owned DeadWare layout trace.");
                return file;
            }
            catch {file.Dispose();throw;}
        }
        try {file.Write(Header);return file;}
        catch {file.Dispose();throw;}
    }
    private static string Short(string value)=>value.Length<=2048?value:value[..2048];
    public void Record(string eventName,object? data=null,Exception? error=null)
    {
        try
        {
            lock(gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
                byte[] Encode(bool truncated)=>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {Time=DateTimeOffset.UtcNow,ProcessId=Environment.ProcessId,Event=eventName,HistoryTruncated=truncated,Data=data,Error=error is null?null:new {Type=error.GetType().FullName,Message=Short(error.Message),HResult=$"0x{error.HResult:X8}"}})+Environment.NewLine);
                byte[] bytes=Encode(false);
                // Keep a hard cap even if a future event unexpectedly grows large.
                if(bytes.Length+Header.Length>MaximumBytes)return;
                using var file=OpenOwnedTrace();
                if(file.Length+bytes.Length>MaximumBytes) {bytes=Encode(true);file.SetLength(Header.Length);}
                file.Seek(0,SeekOrigin.End);file.Write(bytes);
            }
        }
        catch(Exception) { }
    }
    public static object ReadSettingsState(string path,string? expectedText=null)
    {
        try
        {
            var file=new FileInfo(path);
            if(!file.Exists)return new {Path=Path.GetFullPath(path),Exists=false};
            string text=File.ReadAllText(path);
            using var document=JsonDocument.Parse(text);
            var root=document.RootElement;
            string[] properties=root.ValueKind==JsonValueKind.Object?root.EnumerateObject().Select(p=>Short(p.Name)).Take(32).ToArray():[];
            string[] open=root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("OpenWindows",out var windows)&&windows.ValueKind==JsonValueKind.Array
                ?windows.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>Short(v.GetString()!)).Take(16).ToArray():[];
            return new {Path=file.FullName,Exists=true,ModifiedUtc=file.LastWriteTimeUtc,Bytes=file.Length,SchemaProperties=properties,OpenWindows=open,SnapshotMatches=expectedText is null?(bool?)null:string.Equals(text,expectedText,StringComparison.Ordinal)};
        }
        catch(Exception e) {return new {Path=path,Error=new {Type=e.GetType().FullName,Message=Short(e.Message),HResult=$"0x{e.HResult:X8}"}};}
    }
}

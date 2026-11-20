using System.Globalization;
using System.Text;

namespace DeadWare.Devices;

// Size-capped rolling log (devices.log, .1, .2): the old lighting app once grew a 23 GB log. Write never throws. A null folder logs nowhere:
// an elevated host whose protected log folder couldn't be prepared never falls back to a folder other programs can change.
// createFolder: a missing folder is made again before a write, as a host without administrator rights does. An elevated host's private
// folder is made only by ProtectedFolders.LogDirectory, at start (polish round 2, item 6): made here, it would take the logs folder's
// rights, which let every user read. So once it is missing, this log writes nothing more, even if the folder comes back, until the next start.
internal sealed class HostLog : IDisposable
{
    public const long MaxBytes=2_097_152;
    public const int Files=3;
    readonly string? dir;
    readonly Func<DateTimeOffset> clock;
    readonly bool createFolder;
    readonly object gate=new();
    long size=-1;
    bool disposed,gone;
    public HostLog(string? directory,Func<DateTimeOffset>? clock=null,bool createFolder=true){dir=directory;this.clock=clock??(()=>DateTimeOffset.UtcNow);this.createFolder=createFolder;}
    string PathOf(int n)=>Path.Combine(dir!,n==0?"devices.log":$"devices.{n}.log");
    public void Write(string line)
    {
        if(dir is null) return;
        try
        {
            lock(gate)
            {
                if(disposed||gone) return;
                var bytes=Encoding.UTF8.GetBytes(clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture)+" "+line+"\n");
                if(createFolder) Directory.CreateDirectory(dir);
                else if(!Directory.Exists(dir)){gone=true;return;}
                var main=PathOf(0);
                if(size<0) size=File.Exists(main)?new FileInfo(main).Length:0;
                if(size>0&&size+bytes.Length>MaxBytes){Rotate();size=0;}
                using(var fs=new FileStream(main,FileMode.Append,FileAccess.Write,FileShare.Read)) fs.Write(bytes,0,bytes.Length);
                size+=bytes.Length;
            }
        }
        catch{size=-1;}
    }
    void Rotate()
    {
        for(var n=Files-1;n>=1;n--)
        {
            var from=PathOf(n-1);
            if(File.Exists(from)) File.Move(from,PathOf(n),overwrite:true);
        }
    }
    public void Dispose(){lock(gate)disposed=true;}
}

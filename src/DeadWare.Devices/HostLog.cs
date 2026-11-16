using System.Globalization;
using System.Text;

namespace DeadWare.Devices;

// Size-capped rolling log (devices.log, .1, .2): the old lighting app once grew a 23 GB log. Write never throws.
internal sealed class HostLog : IDisposable
{
    public const long MaxBytes=2_097_152;
    public const int Files=3;
    readonly string dir;
    readonly Func<DateTimeOffset> clock;
    readonly object gate=new();
    long size=-1;
    bool disposed;
    public HostLog(string directory,Func<DateTimeOffset>? clock=null){dir=directory;this.clock=clock??(()=>DateTimeOffset.UtcNow);}
    string PathOf(int n)=>Path.Combine(dir,n==0?"devices.log":$"devices.{n}.log");
    public void Write(string line)
    {
        try
        {
            lock(gate)
            {
                if(disposed) return;
                var bytes=Encoding.UTF8.GetBytes(clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture)+" "+line+"\n");
                Directory.CreateDirectory(dir);
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

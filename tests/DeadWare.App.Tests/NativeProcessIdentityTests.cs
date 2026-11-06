using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class NativeProcessIdentityTests
{
    private const int InsufficientBuffer=122;
    /// <summary>Scripted native read: each call answers with the next (path, error) pair, the last one repeating, and records the capacity asked for.</summary>
    private sealed class ScriptedPath(params (string? Path,int Error)[] script)
    {
        public List<int> Capacities {get;}=[];
        public string? Query(int capacity,out int error)
        {
            var (path,code)=script[Math.Min(Capacities.Count,script.Length-1)];
            Capacities.Add(capacity);error=code;return path;
        }
    }
    [Fact] public void A_path_that_fits_is_read_once_with_1024_characters()
    {
        var q=new ScriptedPath((@"C:\Program Files\App\app.exe",0));
        Assert.Equal(@"C:\Program Files\App\app.exe",NativeProcessIdentity.ImagePath(q.Query));
        Assert.Equal(new[]{1024},q.Capacities);
    }
    [Fact] public void Insufficient_buffer_retries_once_with_32768_characters()
    {
        string longPath=@"C:\"+string.Join(@"\",Enumerable.Repeat(new string('d',200),10))+@"\app.exe";
        var q=new ScriptedPath((null,InsufficientBuffer),(longPath,0));
        Assert.Equal(longPath,NativeProcessIdentity.ImagePath(q.Query));
        Assert.Equal(new[]{1024,32_768},q.Capacities);
    }
    [Fact] public void A_failed_retry_stays_unreadable()
    {
        var q=new ScriptedPath((null,InsufficientBuffer));
        Assert.Null(NativeProcessIdentity.ImagePath(q.Query));
        Assert.Equal(new[]{1024,32_768},q.Capacities);
    }
    [Theory][InlineData(5)][InlineData(6)][InlineData(31)]
    public void Any_other_failure_is_unreadable_without_a_retry(int error)
    {
        var q=new ScriptedPath((null,error));
        Assert.Null(NativeProcessIdentity.ImagePath(q.Query));
        Assert.Equal(new[]{1024},q.Capacities);
    }
}

using System.Runtime.InteropServices;

namespace DeadWare.App;
/// <summary>Per-processor CPU load from Windows; the fallback when the hardware monitor reports no per-core loads.</summary>
public sealed class WindowsCpuCores
{
    private (ulong Idle,ulong Kernel,ulong User)[]? previous;
    public IReadOnlyList<double?> Read()=>Read(out _);
    public IReadOnlyList<double?> Read(out int dropped)
    {
        dropped=0;
        var count=(int)GetActiveProcessorCount(0xFFFF);
        if(count<=0){previous=null;return [];}
        var buffer=new ProcessorPerformance[count];
        var size=Marshal.SizeOf<ProcessorPerformance>();
        int status;uint returned;
        try{status=NtQuerySystemInformation(8,buffer,(uint)(size*count),out returned);}
        catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException){previous=null;return [];}
        if(status!=0){previous=null;return [];}
        var current=new (ulong Idle,ulong Kernel,ulong User)[Math.Min(count,(int)returned/size)];
        for(var i=0;i<current.Length;i++) current[i]=((ulong)buffer[i].IdleTime,(ulong)buffer[i].KernelTime,(ulong)buffer[i].UserTime);
        var before=previous;previous=current;
        if(before is null)return [];
        var result=Delta(before,current);
        if(result.Count>0)dropped=Dropped(current.Length);
        return result;
    }
    internal static int Dropped(int processors)=>Math.Max(0,processors-CpuCoreUsage.MaximumCores);
    internal static IReadOnlyList<double?> Delta(ReadOnlySpan<(ulong Idle,ulong Kernel,ulong User)> previous,ReadOnlySpan<(ulong Idle,ulong Kernel,ulong User)> current)
    {
        if(previous.Length!=current.Length) return [];
        var result=new double?[Math.Min(current.Length,CpuCoreUsage.MaximumCores)];
        for(var i=0;i<result.Length;i++)
        {
            var (pi,pk,pu)=previous[i];var (ci,ck,cu)=current[i];
            if(ci<pi || ck<pk || cu<pu) continue;
            ulong busy=(ck-pk)+(cu-pu),idle=ci-pi;
            if(busy>0 && idle<=busy) result[i]=Math.Round(100d*(busy-idle)/busy,1);
        }
        return result;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass,[Out] ProcessorPerformance[] information,uint length,out uint returned);
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessorPerformance {public long IdleTime,KernelTime,UserTime,DpcTime,InterruptTime;public uint InterruptCount;}
}

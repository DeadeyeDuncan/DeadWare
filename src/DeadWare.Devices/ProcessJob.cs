using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeadWare.Devices;

// Ruling Z: a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. The host holds the only handle to it for its whole life, so when the host
// ends for any reason, a crash or End task included, Windows closes the handle and ends every process in the job: an OpenRGB the host
// started can't outlive it and block the next host as "another OpenRGB". The job is unnamed and its handle not inheritable, because
// Process.Start creates children with bInheritHandles=TRUE and an OpenRGB holding its own job's handle would keep the job open.
internal sealed class ProcessJob : IDisposable
{
    const int JobObjectExtendedLimitInformation=9;
    const uint KillOnJobClose=0x2000;
    readonly SafeJobHandle handle;
    ProcessJob(SafeJobHandle handle){this.handle=handle;}

    // Throws Win32Exception when the job can't be created or its kill-on-close limit can't be set: a job without it is worthless.
    public static ProcessJob Create()
    {
        var job=CreateJobObjectW(IntPtr.Zero,null);
        if(job.IsInvalid){var error=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(error,"The job object could not be created.");}
        var limits=new JOBOBJECT_EXTENDED_LIMIT_INFORMATION{BasicLimitInformation=new(){LimitFlags=KillOnJobClose}};
        if(!SetInformationJobObject(job,JobObjectExtendedLimitInformation,ref limits,(uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {var error=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(error,"The job object's kill-on-close limit could not be set.");}
        return new(job);
    }

    // Puts a process the host started into the job; throws Win32Exception if Windows refuses (a parent job that forbids nesting, say).
    public void Assign(Process process)
    {
        if(!AssignProcessToJobObject(handle,process.SafeHandle)) throw new Win32Exception(Marshal.GetLastWin32Error(),"The process could not join the job object.");
    }

    public bool Contains(Process process)=>IsProcessInJob(process.SafeHandle,handle,out var inJob)&&inJob;

    public bool KillsOnClose=>QueryInformationJobObject(handle,JobObjectExtendedLimitInformation,out var limits,(uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(),IntPtr.Zero)
        &&(limits.BasicLimitInformation.LimitFlags&KillOnJobClose)!=0;

    // Ends every process in the job. The host never calls it: its handle closes when it exits. Tests stand in for a host's death with it.
    public void Dispose()=>handle.Dispose();

    internal static int ExtendedLimitSize=>Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

    sealed class SafeJobHandle():SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()=>CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit,PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize,MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS{public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
    // 144 bytes on x64: a wrong size makes SetInformationJobObject fail with ERROR_BAD_LENGTH.
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern SafeJobHandle CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(SafeJobHandle job,int infoClass,ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(SafeJobHandle job,int infoClass,out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,uint length,IntPtr returnLength);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeJobHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool IsProcessInJob(SafeProcessHandle process,SafeJobHandle job,[MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
}

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;

namespace DeadWare.Devices;

// One client at a time on a per-user named pipe, served from ONE pipe instance for the host's lifetime (Disconnect, then wait again),
// so the pipe name never disappears between clients. Any client I/O failure or write timeout drops that client only.
// Writes are serialised (frames never interleave). A reply is always written before any broadcast its request caused, and broadcasts
// are coalesced to the latest pending state, so a stalled client cannot queue frames or receive states out of order. Preview frames have a
// slot of their own, written only when no state waits. clientGone is called after each client's teardown, outside the server's locks.
internal sealed class HostPipeServer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultWriteTimeout=TimeSpan.FromSeconds(5);
    const int BufferBytes=65536;
    readonly string pipeName;
    readonly Func<HostMessage,Task<HostMessage>> handle;
    readonly HostLog log;
    readonly string? label;
    readonly TimeSpan writeTimeout;
    readonly SemaphoreSlim writeGate=new(1,1);
    readonly CancellationTokenSource cts=new();
    readonly TaskCompletionSource stopReplied=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object sync=new();
    NamedPipeServerStream? server;
    Stream? current;
    CancellationTokenSource? clientCts;
    // pending: the latest state (or other broadcast); pendingPreview: the latest preview frame, written only when no state waits, so a
    // preview never delays or displaces a state.
    HostMessage? pending,pendingPreview;
    bool handling;
    readonly Action? clientGone;
    public HostPipeServer(string pipeName,Func<HostMessage,Task<HostMessage>> handle,HostLog log,bool? elevated=null,TimeSpan? writeTimeout=null,string? labelLevel=null,Action? clientGone=null)
    {this.pipeName=pipeName;this.handle=handle;this.log=log;label=labelLevel??((elevated??Environment.IsPrivilegedProcess)?"HI":null);this.writeTimeout=writeTimeout??DefaultWriteTimeout;this.clientGone=clientGone;}

    // Current user: GRGW (on a pipe this includes FILE_CREATE_PIPE_INSTANCE; the one-instance limit and FILE_FLAG_FIRST_PIPE_INSTANCE are
    // what stop anyone creating extra instances, not the ACL). Administrators: full. Network logons: denied first, because the DACL is
    // evaluated in order. An elevated host adds a mandatory label with No-Write-Up and No-Read-Up so a lower-integrity (non-elevated)
    // process cannot even open the pipe. A non-elevated host has no SACL at all (the implicit label is Medium).
    public static string Sddl(string userSid,string? labelLevel)=>$"D:(D;;GA;;;NU)(A;;GRGW;;;{userSid})(A;;GA;;;BA)"+(labelLevel is null?"":$"S:(ML;;NWNR;;;{labelLevel})");

    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES{public int nLength;public IntPtr lpSecurityDescriptor;public int bInheritHandle;}
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,uint revision,out IntPtr sd,out uint length);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    static extern SafePipeHandle CreateNamedPipeW(string name,uint openMode,uint pipeMode,uint maxInstances,uint outBuffer,uint inBuffer,uint timeout,ref SECURITY_ATTRIBUTES attributes);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    const uint DuplexAccess=0x3,FirstInstance=0x00080000,Overlapped=0x40000000,TypeByte=0,ReadByte=0,Wait=0,RejectRemote=0x8;
    const int AccessDenied=5;

    // Created natively: PipeSecurity would carry an empty SACL, which needs SeSecurityPrivilege that an elevated token holds disabled,
    // so it could not create the labelled pipe. A string-converted descriptor sets just the label. Never falls back to an unlabelled pipe.
    NamedPipeServerStream Create()
    {
        var sddl=Sddl(WindowsIdentity.GetCurrent().User!.Value,label);
        if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,1,out var sd,out _))
            throw new IOException("The pipe security descriptor could not be built (Win32 error "+Marshal.GetLastWin32Error()+").");
        SafePipeHandle handle;
        var error=0;
        try
        {
            var attributes=new SECURITY_ATTRIBUTES{nLength=Marshal.SizeOf<SECURITY_ATTRIBUTES>(),lpSecurityDescriptor=sd};
            handle=CreateNamedPipeW(@"\\.\pipe\"+pipeName,DuplexAccess|FirstInstance|Overlapped,TypeByte|ReadByte|Wait|RejectRemote,1,BufferBytes,BufferBytes,0,ref attributes);
            if(handle.IsInvalid) error=Marshal.GetLastWin32Error();
        }
        finally{LocalFree(sd);}
        if(error==AccessDenied) throw new UnauthorizedAccessException("The pipe name is already taken.");
        if(error!=0) throw new IOException("The pipe could not be created (Win32 error "+error+").");
        return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
    }

    internal SafePipeHandle? PipeHandle{get{lock(sync)return server?.SafePipeHandle;}}

    // Creates the pipe now; throws if the name is already taken. RunAsync calls it if the caller did not.
    public void Open(){lock(sync)server??=Create();}

    public async Task RunAsync(CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,cts.Token);
        var ct=linked.Token;
        Open();
        while(!ct.IsCancellationRequested)
        {
            var failed=false;
            CancellationTokenSource? client=null;
            try
            {
                await server!.WaitForConnectionAsync(ct).ConfigureAwait(false);
                client=CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock(sync){clientCts=client;current=server;handling=false;pending=null;pendingPreview=null;}
                await ServeAsync(server,client.Token).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception e){failed=true;log.Write("pipe client dropped: "+e.Message);}
            // Teardown under the write gate: a broadcast picked just before the session ended can never reach the next client or cancel a disposed source.
            await writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock(sync){current=null;clientCts=null;pending=null;pendingPreview=null;handling=false;}
                try{client?.Dispose();}catch(ObjectDisposedException){}
                try{server!.Disconnect();}
                catch(Exception){lock(sync){try{server!.Dispose();}catch{}server=null;try{server=Create();}catch(Exception e){log.Write("pipe could not be recreated: "+e.Message);throw;}}}
            }
            finally{writeGate.Release();}
            // Outside every lock of ours (the host's ClientGone takes its lock, which a tick can hold for several OpenRGB bounds), and before
            // the next wait for a client, so it always comes before the next client's first request.
            if(client is not null) try{clientGone?.Invoke();}catch(Exception e){log.Write("client-gone handler failed: "+e.Message);}
            if(failed){try{await Task.Delay(50,ct).ConfigureAwait(false);}catch(OperationCanceledException){break;}}
        }
        lock(sync){try{server?.Dispose();}catch{}server=null;}
    }

    async Task ServeAsync(Stream stream,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var request=await DeviceHostProtocol.ReadAsync(stream,ct).ConfigureAwait(false);
            if(request is null) return;
            lock(sync)handling=true;
            try
            {
                HostMessage reply;
                if(request.Version!=DeviceHostProtocol.Version) reply=new Reply(false,HostReplyCodes.Version){Id=request.Id};
                else
                {
                    try{reply=await handle(request).ConfigureAwait(false);}
                    catch(Exception e){log.Write("request failed: "+e.Message);reply=new Reply(false,HostReplyCodes.InternalError){Id=request.Id};}
                }
                await writeGate.WaitAsync(ct).ConfigureAwait(false);
                try{await WriteAsync(stream,reply,ct).ConfigureAwait(false);}
                finally{writeGate.Release();}
                if(request is StopRequest) stopReplied.TrySetResult();
            }
            finally{lock(sync)handling=false;}
            await PumpAsync().ConfigureAwait(false);
        }
    }

    async Task WriteAsync(Stream stream,HostMessage message,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(writeTimeout);
        await DeviceHostProtocol.WriteAsync(stream,message,timeout.Token).ConfigureAwait(false);
    }

    bool HasWork(){lock(sync)return !handling&&(pending is not null||pendingPreview is not null)&&current is not null;}

    // Writes the latest pending state, else the latest pending preview. If another write holds the gate it re-checks for pending work when it finishes.
    async Task PumpAsync()
    {
        do
        {
            if(!await writeGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                HostMessage? message;Stream? stream;CancellationTokenSource? client;
                lock(sync)
                {
                    if(handling||current is null||(pending??pendingPreview) is null) continue;
                    if(pending is not null){message=pending;pending=null;}else{message=pendingPreview!;pendingPreview=null;}
                    stream=current;client=clientCts;
                }
                try{await WriteAsync(stream,message,client?.Token??cts.Token).ConfigureAwait(false);}
                catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
                {
                    log.Write("pipe broadcast failed, dropping the client: "+e.Message);
                    try{client?.Cancel();}catch(ObjectDisposedException){}
                }
            }
            finally{writeGate.Release();}
        }while(HasWork());
    }

    // Queues a broadcast for the connected client (none connected: dropped, the window asks for state on connect). Only the latest of each
    // kind survives: a preview in its own slot, anything else in pending.
    public Task SendAsync(HostMessage message)
    {
        lock(sync){if(current is null) return Task.CompletedTask;if(message is PreviewEvent) pendingPreview=message;else pending=message;}
        return PumpAsync();
    }

    // Completes once a Stop reply has been written, so the process does not exit before the window hears it.
    public async Task WaitForStopReplyAsync(TimeSpan limit)
    {
        try{await stopReplied.Task.WaitAsync(limit).ConfigureAwait(false);}catch(TimeoutException){}
    }

    public ValueTask DisposeAsync(){try{cts.Cancel();}catch(ObjectDisposedException){}return ValueTask.CompletedTask;}
}

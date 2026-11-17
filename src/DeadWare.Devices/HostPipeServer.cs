using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.Core;

namespace DeadWare.Devices;

// One client at a time on a per-user named pipe, served from ONE pipe instance for the host's lifetime (Disconnect, then wait again),
// so the pipe name never disappears between clients. Any client I/O failure or write timeout drops that client only.
// Writes are serialised (frames never interleave). A reply is always written before any broadcast its request caused, and broadcasts
// are coalesced to the latest pending state, so a stalled client cannot queue frames or receive states out of order.
internal sealed class HostPipeServer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultWriteTimeout=TimeSpan.FromSeconds(5);
    const int BufferBytes=65536;
    readonly string pipeName;
    readonly Func<HostMessage,Task<HostMessage>> handle;
    readonly HostLog log;
    readonly bool elevated;
    readonly TimeSpan writeTimeout;
    readonly SemaphoreSlim writeGate=new(1,1);
    readonly CancellationTokenSource cts=new();
    readonly TaskCompletionSource stopReplied=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object sync=new();
    NamedPipeServerStream? server;
    Stream? current;
    CancellationTokenSource? clientCts;
    HostMessage? pending;
    bool handling;
    public HostPipeServer(string pipeName,Func<HostMessage,Task<HostMessage>> handle,HostLog log,bool? elevated=null,TimeSpan? writeTimeout=null)
    {this.pipeName=pipeName;this.handle=handle;this.log=log;this.elevated=elevated??Environment.IsPrivilegedProcess;this.writeTimeout=writeTimeout??DefaultWriteTimeout;}

    // Current user: read/write only (no ACL changes, no new instances). Administrators: full. Network logons: denied first, because the
    // DACL is evaluated in order. An elevated host adds a High mandatory label with No-Write-Up so non-elevated clients are refused.
    public static string Sddl(string userSid,bool elevated)=>$"D:(D;;GA;;;NU)(A;;GRGW;;;{userSid})(A;;GA;;;BA)"+(elevated?"S:(ML;;NW;;;HI)":"");

    PipeSecurity Security(bool withLabel)
    {
        var ps=new PipeSecurity();
        ps.SetSecurityDescriptorSddlForm(Sddl(WindowsIdentity.GetCurrent().User!.Value,withLabel));
        return ps;
    }

    NamedPipeServerStream Create()
    {
        if(elevated)
        {
            try{return NamedPipeServerStreamAcl.Create(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,BufferBytes,BufferBytes,Security(true));}
            catch(Exception e) when(e is not IOException){log.Write("pipe integrity label not applied, using the access list only: "+e.Message);}
        }
        return NamedPipeServerStreamAcl.Create(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,BufferBytes,BufferBytes,Security(false));
    }

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
            try
            {
                await server!.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var client=CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock(sync){clientCts=client;current=server;handling=false;pending=null;}
                try{await ServeAsync(server,client.Token).ConfigureAwait(false);}
                finally{lock(sync){current=null;clientCts=null;pending=null;handling=false;}}
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception e){failed=true;log.Write("pipe client dropped: "+e.Message);}
            try{server!.Disconnect();}
            catch(Exception){lock(sync){try{server!.Dispose();}catch{}server=null;try{server=Create();}catch(Exception e){log.Write("pipe could not be recreated: "+e.Message);throw;}}}
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
                if(request.Version!=DeviceHostProtocol.Version) reply=new Reply(false,"version"){Id=request.Id};
                else
                {
                    try{reply=await handle(request).ConfigureAwait(false);}
                    catch(Exception e){log.Write("request failed: "+e.Message);reply=new Reply(false,"internal error"){Id=request.Id};}
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

    bool HasWork(){lock(sync)return !handling&&pending is not null&&current is not null;}

    // Writes the latest pending broadcast. If another write holds the gate it re-checks for pending work when it finishes.
    async Task PumpAsync()
    {
        do
        {
            if(!await writeGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                HostMessage? message;Stream? stream;CancellationTokenSource? client;
                lock(sync){if(handling||pending is null||current is null) continue;message=pending;pending=null;stream=current;client=clientCts;}
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

    // Queues a broadcast for the connected client (none connected: dropped, the window asks for state on connect). Only the latest survives.
    public Task SendAsync(HostMessage message)
    {
        lock(sync){if(current is null) return Task.CompletedTask;pending=message;}
        return PumpAsync();
    }

    // Completes once a Stop reply has been written, so the process does not exit before the window hears it.
    public async Task WaitForStopReplyAsync(TimeSpan limit)
    {
        try{await stopReplied.Task.WaitAsync(limit).ConfigureAwait(false);}catch(TimeoutException){}
    }

    public ValueTask DisposeAsync(){try{cts.Cancel();}catch(ObjectDisposedException){}return ValueTask.CompletedTask;}
}

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.Core;

namespace DeadWare.Devices;

// One client at a time on a per-user named pipe. Writes (replies and broadcasts) are serialised so frames never interleave.
internal sealed class HostPipeServer : IAsyncDisposable
{
    static readonly TimeSpan WriteTimeout=TimeSpan.FromSeconds(5);
    readonly string pipeName;
    readonly Func<HostMessage,Task<HostMessage>> handle;
    readonly HostLog log;
    readonly SemaphoreSlim writeGate=new(1,1);
    readonly CancellationTokenSource cts=new();
    volatile Stream? current;
    public HostPipeServer(string pipeName,Func<HostMessage,Task<HostMessage>> handle,HostLog log){this.pipeName=pipeName;this.handle=handle;this.log=log;}

    static PipeSecurity Security()
    {
        var ps=new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,PipeAccessRights.FullControl,AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
        return ps;
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,cts.Token);
        var ct=linked.Token;
        var security=Security();
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await using var server=NamedPipeServerStreamAcl.Create(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,0,security);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                current=server;
                try{await ServeAsync(server,ct).ConfigureAwait(false);}
                finally{current=null;}
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception e) when(e is IOException or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException)
            {
                log.Write("pipe client ended: "+e.Message);
                try{await Task.Delay(100,ct).ConfigureAwait(false);}catch(OperationCanceledException){break;}
            }
        }
    }

    async Task ServeAsync(Stream stream,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var request=await DeviceHostProtocol.ReadAsync(stream,ct).ConfigureAwait(false);
            if(request is null) return;
            HostMessage reply;
            if(request.Version!=DeviceHostProtocol.Version) reply=new Reply(false,"version"){Id=request.Id};
            else
            {
                try{reply=await handle(request).ConfigureAwait(false);}
                catch(Exception e){log.Write("request failed: "+e.Message);reply=new Reply(false,"internal error"){Id=request.Id};}
            }
            await WriteAsync(stream,reply,ct).ConfigureAwait(false);
        }
    }

    async Task WriteAsync(Stream stream,HostMessage message,CancellationToken ct)
    {
        await writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WriteTimeout);
            await DeviceHostProtocol.WriteAsync(stream,message,timeout.Token).ConfigureAwait(false);
        }
        finally{writeGate.Release();}
    }

    // Broadcast to the connected client, if any. A vanished or stalled client is that client's problem, never the host's.
    public async Task SendAsync(HostMessage message)
    {
        var stream=current;
        if(stream is null) return;
        try{await WriteAsync(stream,message,cts.Token).ConfigureAwait(false);}
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException){log.Write("pipe broadcast dropped: "+e.Message);}
    }

    public ValueTask DisposeAsync(){try{cts.Cancel();}catch(ObjectDisposedException){}return ValueTask.CompletedTask;}
}

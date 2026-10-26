using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using DeadWare.Core;
namespace DeadWare.App;
public partial class App : Application
{
    private GuiInstance? instance;
    private UiSession? session;
    private TrayIcon? tray;
    private readonly ApplicationLifetime lifetime=new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(await StartupRegistration.HandleCommandLineAsync(e.Args) is int configurationExit)
        {Shutdown(configurationExit);return;}
        if(e.Args.Contains("--diagnose"))
        {
            Shutdown(await DiagnoseAsync(e.Args));return;
        }
        Theme.Apply("Midnight");
        try
        {
            await GuiInstance.WaitForPreviousAsync(e.Args);
            instance=new GuiInstance();
            if(!instance.Acquired)
            {
                if(!e.Args.Contains("--startup"))MessageBox.Show("DeadWare is already running in this Windows session. Double-click its system tray icon or right-click it and choose Show dashboard. The icon may be in the tray overflow.","DeadWare",MessageBoxButton.OK,MessageBoxImage.Information);
                Shutdown();return;
            }
            session=new UiSession(LayoutDiagnostics.RequestedPath(e.Args));
            Theme.Apply(session.Settings.Theme);
            var main=new MainWindow(session,lifetime,()=>tray is not null,RequestQuit);MainWindow=main;
            try {tray=new TrayIcon(Dispatcher,ShowDashboard,RequestQuit);}
            catch(Exception) {main.SetTrayUnavailable();}
            session.Cooling.WarningRaised+=warning=>
            {
                if(lifetime.IsEnding||Dispatcher.HasShutdownStarted)return;
                Dispatcher.BeginInvoke(()=>
                {
                    if(!lifetime.IsEnding&&session.Settings.CoolingNotifications)tray?.ShowCoolingWarning(warning.Message);
                });
            };
            bool windowsStartup=e.Args.Contains("--startup")&&!e.Args.Contains("--restart-from");
            if(ApplicationLifetime.ShouldShowDashboardOnLaunch(windowsStartup,session.Settings.StartInTray,tray is not null))main.Show();
            session.Start();
        }
        catch(Exception ex) {MessageBox.Show($"DeadWare could not start: {ex.Message}","DeadWare",MessageBoxButton.OK,MessageBoxImage.Error);await QuitAsync(1);}
    }
    private void ShowDashboard()
    {
        if(lifetime.IsEnding||MainWindow is not MainWindow main)return;
        main.Show();if(main.WindowState==WindowState.Minimized)main.WindowState=WindowState.Normal;main.Activate();
    }
    private async void RequestQuit()=>await QuitAsync();
    internal Task QuitAsync(int exitCode=0)=>lifetime.QuitAsync(async ()=>
    {
        if(MainWindow is not null)MainWindow.IsEnabled=false;
        try {if(session is not null)await session.ShutdownAsync();}
        catch(Exception ex) {MessageBox.Show($"DeadWare reported a shutdown error: {ex.Message}","DeadWare",MessageBoxButton.OK,MessageBoxImage.Warning);}
    },()=>{try {DisposeTray();}finally {Shutdown(exitCode);}});
    private void DisposeTray() {var current=tray;tray=null;current?.Dispose();}
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        lifetime.BeginSessionEnding();
        try {session?.SaveForSessionEnding();}
        finally
        {
            try {session?.StopCollectorForSessionEnding();}
            finally {try {DisposeTray();}finally {base.OnSessionEnding(e);}}
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.BeginSessionEnding();
        try {DisposeTray();}finally {try {instance?.Dispose();}finally {base.OnExit(e);}}
    }
    private static async Task<int> DiagnoseAsync(string[] args)
    {
        Collector? collector=null;string? output=null;int exit=0;string status="Starting";
        var readings=new List<Reading>();int deadlineSeconds=15;
        var options=new CollectorOptions(HardwareEnabled:!args.Contains("--no-hardware"),EtwEnabled:!args.Contains("--no-etw"),DiagnosticTimings:true);
        async Task WriteReport()
        {
            if(output is null)return;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var text=JsonSerializer.Serialize(new{Version=BuildInfo.Version,Status=status,Stage=collector?.Stage,CollectionDeadlineSeconds=deadlineSeconds,ShutdownDeadlineSeconds=3,Options=options,Samples=readings},new JsonSerializerOptions{WriteIndented=true});
            string temporary=output+".tmp";await File.WriteAllTextAsync(temporary,text);File.Move(temporary,output,true);
        }
        try
        {
            int index=Array.IndexOf(args,"--diagnose");
            if(index+1>=args.Length)throw new ArgumentException("Provide an output file after --diagnose.");
            output=Path.GetFullPath(args[index+1]);
            int sampleIndex=Array.IndexOf(args,"--samples");
            int count=sampleIndex>=0&&sampleIndex+1<args.Length?int.Parse(args[sampleIndex+1]):3;
            if(count is <1 or >60)throw new ArgumentException("Samples must be between 1 and 60.");
            deadlineSeconds=Math.Max(15,count+10);
            collector=new(options);
            // An initial report survives an external watchdog exit during blocked hardware discovery.
            await WriteReport();
            var channel=Channel.CreateUnbounded<Reading>(new(){SingleReader=true,SingleWriter=true});
            collector.SampleAvailable+=reading=>channel.Writer.TryWrite(reading);
            collector.Start();
            _ = collector.Completion.ContinueWith(task=>channel.Writer.TryComplete(task.Exception),TaskScheduler.Default);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(deadlineSeconds));
            while(readings.Count<count)
            {
                readings.Add(await channel.Reader.ReadAsync(deadline.Token));status="Collecting";await WriteReport();
            }
            status="Completed";
        }
        catch(OperationCanceledException) {status="Diagnostic collection deadline exceeded";exit=1;}
        catch(Exception e) {status=$"Diagnostic failed ({e.GetType().Name})";exit=1;}
        finally
        {
            // Write partial results before cleanup so a native hardware hang cannot discard evidence.
            try {await WriteReport();}catch(Exception){exit=1;}
            if(collector is not null)
            {
                try {await collector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));}
                catch(TimeoutException) {status+="; provider shutdown deadline exceeded";exit=1;}
                catch(Exception e) {status+=$"; shutdown failed ({e.GetType().Name})";exit=1;}
            }
            try {await WriteReport();}catch(Exception){exit=1;}
        }
        return exit;
    }
}

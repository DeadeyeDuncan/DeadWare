using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeadWare.Core;
namespace DeadWare.App;
public sealed class UiSession
{
    public Collector Collector {get;}
    public CoolingHistoryService Cooling {get;}
    public GraphHistoryService GraphHistory {get;}
    public BatteryHistoryService Battery {get;}
    public UpdateService Updates {get;}=new(BuildInfo.Version);
    public UserSettings Settings {get;}
    public string SettingsStatus {get;private set;}="";
    private readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadWare","settings.json");
    private readonly SettingsSnapshotWriter writer;
    private readonly LayoutDiagnostics? diagnostics;
    private readonly DetachedWindowState layout;
    private long saveRevision;
    private bool restarting;
    private Task? shutdown;
    private Task? collectorStop;
    private readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromSeconds(1)};
    private readonly DispatcherTimer saveTimer=new() {Interval=TimeSpan.FromMilliseconds(500)};
    private readonly Dictionary<string,Window> detached=new();
    private readonly Dictionary<Window,Action> captureWindows=new();
    public event Action? Changed;
    public Reading? Current=>Collector.Latest;
    public int HistorySeconds=>Settings.HistorySeconds;
    public string? SelectedGpuId=>Current is null?null:DeviceSelection.Resolve(Settings.SelectedGpuId,Current.Gpus);
    public string GpuName=>Current?.Gpus.FirstOrDefault(d=>d.Id==SelectedGpuId)?.Name??"GPU unavailable";
    public double? Value(string id)=>Current?.Sample.Values.GetValueOrDefault(id);
    public bool Stale=>Current is not null && Collector.ElapsedSeconds-Current.Sample.MonotonicSeconds>4;
    public UiSession(string? layoutDiagnosticPath=null)
    {
        diagnostics=layoutDiagnosticPath is null?null:new LayoutDiagnostics(layoutDiagnosticPath,settingsPath);
        diagnostics?.Record("session_begin",new {Version=BuildInfo.Version,Executable=Environment.ProcessPath,SettingsPath=Path.GetFullPath(settingsPath),Elevated=StartupRegistration.IsAdministrator});
        try
        {
            var loaded=File.Exists(settingsPath)?SettingsCodec.Read(File.ReadAllText(settingsPath)):new SettingsRead(new(),null);
            Settings=loaded.Settings;SettingsStatus=loaded.Error??"";
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {Settings=new();SettingsStatus="Settings unavailable; using defaults.";diagnostics?.Record("settings_load_failed",error:e);}
        writer=new(settingsPath);
        layout=new(Settings.RestoreOpenWindows?Settings.OpenWindows:[]);
        diagnostics?.Record("settings_loaded",new {Status=SettingsStatus,Settings.RestoreOpenWindows,OpenWindows=layout.Remembered,Settings.SelectedGpuId,Disk=LayoutDiagnostics.ReadSettingsState(settingsPath)});
        saveTimer.Tick+=async (_,_)=>{saveTimer.Stop();await SaveAsync();};
        Collector=new(new CollectorOptions(ExtendedSensors:Settings.ExtendedSensors));
        GraphHistory=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"live-graph-history.db"),Environment.MachineName,Collector.History);
        Collector.SampleAvailable+=GraphHistory.TryRecord;
        Cooling=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"cooling-history.db"),Environment.MachineName,Settings.CoolingConfigurationId);
        Cooling.Configure(Settings.CoolingRecording,Settings.CoolingNotifications,Settings.CoolingDetailedDays,Settings.CoolingSummaryDays,Settings.CoolingConfigurationId);
        Collector.SampleAvailable+=Cooling.TryRecord;
        Battery=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"battery-history.db"),Environment.MachineName);
        Battery.Configure(Settings.BatteryRecording);
        Collector.SampleAvailable+=Battery.TryRecord;
        timer.Tick+=(_,_)=>
        {
            if(Settings.SelectedGpuId is null && Current is {Gpus.Count:>0} reading)
            {Settings.SelectedGpuId=DeviceSelection.Resolve(null,reading.Gpus);_ = SaveAsync();}
            Changed?.Invoke();
        };
    }
    public void Start()
    {
        foreach(string key in layout.Remembered)OpenDetached(key);
        GraphHistory.Start();Cooling.Start();Battery.Start();_ = SaveAsync();Collector.Start();timer.Start();
    }
    public void SetRange(int seconds) {Settings.HistorySeconds=seconds;Changed?.Invoke();_ = SaveAsync();}
    public void SetGpu(string? id) {Settings.SelectedGpuId=id;Changed?.Invoke();_ = SaveAsync();}
    public void SetTheme(string theme) {Settings.Theme=theme;Theme.Apply(theme);Changed?.Invoke();_ = SaveAsync();}
    public void SetExtendedSensors(bool enabled) {Settings.ExtendedSensors=enabled;_ = SaveAsync();}
    public void SetRestoreOpenWindows(bool enabled) {Settings.RestoreOpenWindows=enabled;_ = SaveAsync();}
    public void SetStartInTray(bool enabled) {Settings.StartInTray=enabled;_ = SaveAsync();}
    public void SetBatteryRecording(bool enabled) {Settings.BatteryRecording=enabled;Battery.Configure(enabled);Changed?.Invoke();_ = SaveAsync();}
    public void SetCoolingOptions(bool recording,bool notifications,int detailedDays,int summaryDays)
    {
        Settings.CoolingRecording=recording;Settings.CoolingNotifications=notifications;
        Settings.CoolingDetailedDays=detailedDays==7?7:30;Settings.CoolingSummaryDays=summaryDays==30?30:90;
        Cooling.Configure(recording,notifications,Settings.CoolingDetailedDays,Settings.CoolingSummaryDays,Settings.CoolingConfigurationId);
        _ = SaveAsync();
    }
    public void NewCoolingSetup()
    {
        Settings.CoolingConfigurationId=Guid.NewGuid().ToString("N");
        Cooling.Configure(Settings.CoolingRecording,Settings.CoolingNotifications,Settings.CoolingDetailedDays,Settings.CoolingSummaryDays,Settings.CoolingConfigurationId);
        _ = SaveAsync();
    }
    public string MetricId(string kind)=>kind switch {"cpu"=>"cpu/usage","cputemp"=>"cpu/temperature","gpu"=>(SelectedGpuId??"unavailable")+"/usage","gputemp"=>(SelectedGpuId??"unavailable")+"/temperature",_=>"network/download"};
    public async Task SaveAsync()
    {
        Settings.OpenWindows=layout.Remembered.ToList();
        string text;
        try {text=SettingsCodec.Write(Settings);}
        catch(Exception e) {diagnostics?.Record("settings_serialization_failed",error:e);throw;}
        long revision=++saveRevision;
        diagnostics?.Record("save_begin",new {Revision=revision,OpenWindows=Settings.OpenWindows.ToArray(),Settings.RestoreOpenWindows,Settings.SelectedGpuId});
        try {await Task.Run(()=>WriteSnapshot(revision,text));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {SettingsStatus="Settings could not be saved; current choices are temporary.";diagnostics?.Record("save_failed",new {Revision=revision},e);}
        catch(Exception e) {diagnostics?.Record("save_failed",new {Revision=revision},e);throw;}
    }
    private void WriteSnapshot(long revision,string text)
    {
        writer.Save(revision,text);
        diagnostics?.Record("save_write_returned",new {Revision=revision,Disk=LayoutDiagnostics.ReadSettingsState(settingsPath,text)});
    }
    private void QueueSave()
    {
        if(layout.Ending)return;
        if(!saveTimer.IsEnabled)diagnostics?.Record("save_queued",new {OpenWindows=layout.Remembered});
        saveTimer.Stop();saveTimer.Start();
    }
    private void CaptureAll() {foreach(var capture in captureWindows.Values.ToArray())capture();}
    public void SaveForSessionEnding()
    {
        diagnostics?.Record("windows_session_ending",new {OpenWindows=layout.Remembered});
        layout.BeginShutdown();timer.Stop();saveTimer.Stop();CaptureAll();
        Settings.OpenWindows=layout.Remembered.ToList();
        // Synchronous final disk checkpoint never waits for a UI continuation. The writer's
        // revision gate prevents a previously queued background save from replacing it later.
        try {WriteSnapshot(++saveRevision,SettingsCodec.Write(Settings));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {SettingsStatus="The final window layout could not be saved.";diagnostics?.Record("final_save_failed",new {Revision=saveRevision},e);}
        catch(Exception e) {diagnostics?.Record("final_save_failed",new {Revision=saveRevision},e);throw;}
    }
    private Task StopCollectorAsync()=>collectorStop??=Task.Run(async ()=>
    {
        // Request provider cancellation before closing the recording queue. History drains
        // independently even when a native provider is slow to finish shutting down.
        var providerStop=Collector.DisposeAsync().AsTask();
        Collector.SampleAvailable-=Cooling.TryRecord;
        Collector.SampleAvailable-=GraphHistory.TryRecord;
        Collector.SampleAvailable-=Battery.TryRecord;
        await Task.WhenAll(providerStop,Cooling.StopAsync(),GraphHistory.StopAsync(),Battery.StopAsync()).ConfigureAwait(false);
    });
    internal void StopCollectorForSessionEnding()
    {
        // Windows session ending cannot wait for a dispatcher continuation. The shared stop
        // runs entirely on the pool, stops ETW first, and is bounded even if hardware hangs.
        try
        {
            StopCollectorAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            diagnostics?.Record("session_end_collector_stopped");
        }
        catch(TimeoutException e) {diagnostics?.Record("session_end_collector_stop_timeout",new {DeadlineSeconds=3},e);}
        catch(Exception e) {diagnostics?.Record("session_end_collector_stop_failed",error:e);}
    }
    public void Place(Window window,string key,double width,double height)
    {
        window.Width=width;window.Height=height;
        Settings.Windows.TryGetValue(key,out var saved);
        if(saved is not null)window.Topmost=saved.Topmost;
        window.WindowStartupLocation=saved is null?WindowStartupLocation.CenterScreen:WindowStartupLocation.Manual;
        WindowPlacement? lastNormal=null;
        void Capture()
        {
            if(NativePlacement.CaptureNormal(window) is { } placement)lastNormal=placement;
            if(lastNormal is null)return;
            var latest=lastNormal with {Topmost=window.Topmost};
            if(Settings.Windows.GetValueOrDefault(key)==latest)return;
            Settings.Windows[key]=latest;QueueSave();
        }
        captureWindows[window]=Capture;
        window.SourceInitialized+=(_,_)=>NativePlacement.Restore(window,saved,width,height);
        window.Loaded+=(_,_)=>{NativePlacement.Restore(window,saved,width,height);Capture();};
        window.LocationChanged+=(_,_)=>Capture();window.SizeChanged+=(_,_)=>Capture();
        var topmost=DependencyPropertyDescriptor.FromProperty(Window.TopmostProperty,typeof(Window));
        EventHandler pinChanged=(_,_)=>Capture();topmost.AddValueChanged(window,pinChanged);
        window.Closing+=(_,_)=>Capture();
        window.Closed+=(_,_)=>
        {
            topmost.RemoveValueChanged(window,pinChanged);captureWindows.Remove(window);
        };
    }
    public void OpenDetached(string kind)
    {
        if(layout.Ending||!DetachedWindowState.IsAllowed(kind))return;
        if(detached.TryGetValue(kind,out var existing)) {existing.WindowState=WindowState.Normal;existing.Activate();return;}
        var window=kind=="batteryhistory"?(Window)new BatteryHistoryWindow(this):kind=="widget"?new WidgetWindow(this):new GraphWindow(this,kind);
        detached[kind]=window;
        window.Closed+=(_,_)=>{detached.Remove(kind);layout.Close(kind);diagnostics?.Record("window_closed",new {Kind=kind,layout.Ending,OpenWindows=layout.Remembered});QueueSave();};
        window.Show();layout.Open(kind);diagnostics?.Record("window_opened",new {Kind=kind,OpenWindows=layout.Remembered});QueueSave();
    }
    public Task ShutdownAsync()=>shutdown??=ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        diagnostics?.Record("shutdown_begin",new {OpenWindows=layout.Remembered});
        layout.BeginShutdown();timer.Stop();saveTimer.Stop();CaptureAll();
        foreach(var window in detached.Values.ToArray())window.Close();
        try {await SaveAsync();}finally {await StopCollectorAsync();}
        diagnostics?.Record("shutdown_completed");
    }
    public async void RestartElevated()
    {
        if(restarting||layout.Ending)return;restarting=true;
        try
        {
            CaptureAll();await SaveAsync();
            diagnostics?.Record("administrator_restart_requested");
            if(await GuiInstance.LaunchElevatedReplacementAsync(diagnostics?.OutputPath))await ((App)Application.Current).QuitAsync();
            else MessageBox.Show("The administrator replacement did not confirm this Windows user. Monitoring continues with current permissions.","DeadWare");
        }
        catch(System.ComponentModel.Win32Exception e) {MessageBox.Show(e.NativeErrorCode==1223?"Administrator restart was cancelled. Monitoring continues with current permissions.":"Windows could not restart DeadWare as administrator.","DeadWare");}
        catch(Exception e) {MessageBox.Show($"Restart failed: {e.Message}","DeadWare");}
        finally {restarting=false;}
    }
}

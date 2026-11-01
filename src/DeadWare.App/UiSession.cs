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
    public ConnectionService Connections {get;}
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
    public bool Stale=>Current is not null && Collector.ElapsedSeconds-Current.Sample.MonotonicSeconds>
        PollingPolicy.FreshnessSeconds(Math.Max(Settings.PollingIntervalSeconds,Current.Sample.PollingIntervalSeconds));
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
        Theme.SetGraphColors(Settings.GraphColors);Theme.GlassLevel=Settings.GlassLevel;
        writer=new(settingsPath);
        layout=new(Settings.RestoreOpenWindows?Settings.OpenWindows:[]);
        diagnostics?.Record("settings_loaded",new {Status=SettingsStatus,Settings.RestoreOpenWindows,OpenWindows=layout.Remembered,Settings.SelectedGpuId,Disk=LayoutDiagnostics.ReadSettingsState(settingsPath)});
        saveTimer.Tick+=async (_,_)=>{saveTimer.Stop();await SaveAsync();};
        timer.Interval=TimeSpan.FromSeconds(Math.Min(1,Settings.PollingIntervalSeconds));
        Collector=new(new CollectorOptions(ExtendedSensors:Settings.ExtendedSensors,PollingIntervalSeconds:Settings.PollingIntervalSeconds));
        GraphHistory=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"live-graph-history.db"),Environment.MachineName,Collector.History);
        Collector.SampleAvailable+=GraphHistory.TryRecord;
        Cooling=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"cooling-history.db"),Environment.MachineName,Settings.CoolingConfigurationId);
        Cooling.Configure(Settings.CoolingRecording,Settings.CoolingNotifications,Settings.CoolingDetailedDays,Settings.CoolingSummaryDays,Settings.CoolingConfigurationId);
        Collector.SampleAvailable+=Cooling.TryRecord;
        Battery=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"battery-history.db"),Environment.MachineName);
        Battery.Configure(Settings.BatteryRecording);
        Collector.SampleAvailable+=Battery.TryRecord;
        Connections=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"connections.db"));
        var home=ConnectionAssets.Cities.FirstOrDefault(city=>city.Id==Settings.ConnectionsHomeCityId);
        Settings.ConnectionsHomeCityId=home?.Id;
        Connections.Configure(Settings.ConnectionsRecording,home?.Point);
        Collector.SampleAvailable+=Connections.TryRecord;
        Collector.FinalConnectionsAvailable+=Connections.TryRecord;
        timer.Tick+=(_,_)=>
        {
            if(Settings.SelectedGpuId is null && Current is {Gpus.Count:>0} reading)
            {Settings.SelectedGpuId=DeviceSelection.Resolve(null,reading.Gpus);_ = SaveAsync();}
            Changed?.Invoke();
        };
    }
    public void Start()
    {
        // Restore the saved window types exactly; the launch preference only affects new requests.
        foreach(string key in layout.Remembered)OpenDetachedCore(key);
        GraphHistory.Start();Cooling.Start();Battery.Start();Connections.Start();_ = SaveAsync();Collector.Start();timer.Start();
    }
    public void SetRange(int seconds) {Settings.HistorySeconds=seconds;Changed?.Invoke();_ = SaveAsync();}
    public void SetGpu(string? id) {Settings.SelectedGpuId=id;Changed?.Invoke();_ = SaveAsync();}
    public void SetTheme(string theme) {Settings.Theme=theme;Theme.Apply(theme);Changed?.Invoke();_ = SaveAsync();}
    public void SetGlassLevel(string level)
    {
        level=GlassLevels.Normalize(level);if(Settings.GlassLevel==level)return;
        Settings.GlassLevel=level;Theme.GlassLevel=level;Theme.Apply(Settings.Theme);Changed?.Invoke();_ = SaveAsync();
    }
    public void SetGraphColor(string kind,string? color)
    {
        if(kind=="network")kind="download";
        if(!GraphColorSettings.Kinds.Contains(kind)||color is not null&&!GraphColorSettings.IsColor(color))return;
        if(color is null) {if(!Settings.GraphColors.Remove(kind))return;}
        else
        {
            color=color.ToUpperInvariant();
            if(Settings.GraphColors.GetValueOrDefault(kind)==color)return;
            Settings.GraphColors[kind]=color;
        }
        Theme.SetGraphColors(Settings.GraphColors);Changed?.Invoke();_ = SaveAsync();
    }
    public void SetExtendedSensors(bool enabled) {Settings.ExtendedSensors=enabled;_ = SaveAsync();}
    public void SetGraphStyle(string kind,string pattern,bool glow,double thickness=1,double glowStrength=1)
    {
        if(kind=="network")kind="download";
        if(!GraphColorSettings.Kinds.Contains(kind)||!GraphLineStyles.IsPattern(pattern))return;
        var style=GraphLineStyles.Normalize(new GraphLineStyle(pattern,glow,thickness,glowStrength));
        if(Settings.GraphStyles.GetValueOrDefault(kind,new())==style)return;
        if(style==new GraphLineStyle())Settings.GraphStyles.Remove(kind);else Settings.GraphStyles[kind]=style;
        Changed?.Invoke();_ = SaveAsync();
    }
    public void SetGraphSmoothing(bool enabled)
    {
        if(Settings.GraphSmoothing==enabled)return;
        Settings.GraphSmoothing=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetGpuFanSpeed(bool enabled)
    {
        if(Settings.ShowGpuFanSpeed==enabled)return;
        Settings.ShowGpuFanSpeed=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetCpuPumpSpeed(bool enabled)
    {
        if(Settings.ShowCpuPumpSpeed==enabled)return;
        Settings.ShowCpuPumpSpeed=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetJoinedPopouts(bool enabled)
    {
        if(Settings.JoinCpuGpuPopouts==enabled)return;
        Settings.JoinCpuGpuPopouts=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetWidgetAlwaysOnTop(bool enabled)
    {
        bool changed=Settings.WidgetAlwaysOnTop!=enabled;
        if(Settings.Windows.TryGetValue("widget",out var saved)&&saved is not null&&saved.Topmost!=enabled)
        {Settings.Windows["widget"]=saved with {Topmost=enabled};changed=true;}
        if(!changed)return;
        Settings.WidgetAlwaysOnTop=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetWidgetGraph(string kind,bool enabled)
    {
        if(!WidgetGraphSelection.Kinds.Contains(kind,StringComparer.Ordinal))return;
        var selected=new HashSet<string>(WidgetGraphSelection.Normalize(Settings.WidgetGraphs),StringComparer.Ordinal);
        if(enabled)selected.Add(kind);else selected.Remove(kind);
        var next=WidgetGraphSelection.Normalize(selected);
        if(Settings.WidgetGraphs is not null&&Settings.WidgetGraphs.SequenceEqual(next,StringComparer.Ordinal))return;
        Settings.WidgetGraphs=next;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetWidgetConnectionsMap(bool enabled)
    {
        if(Settings.WidgetConnectionsMap==enabled)return;
        Settings.WidgetConnectionsMap=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetWidgetConnectionsNorthAmerica(bool enabled)
    {
        if(Settings.WidgetConnectionsNorthAmerica==enabled)return;
        Settings.WidgetConnectionsNorthAmerica=enabled;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetPollingInterval(double seconds)
    {
        Settings.PollingIntervalSeconds=PollingPolicy.Normalize(seconds);
        Collector.SetPollingInterval(Settings.PollingIntervalSeconds);
        timer.Interval=TimeSpan.FromSeconds(Math.Min(1,Settings.PollingIntervalSeconds));
        Changed?.Invoke();_ = SaveAsync();
    }
    public void SetRestoreOpenWindows(bool enabled) {Settings.RestoreOpenWindows=enabled;_ = SaveAsync();}
    public void SetStartInTray(bool enabled) {Settings.StartInTray=enabled;_ = SaveAsync();}
    public void SetBatteryRecording(bool enabled) {Settings.BatteryRecording=enabled;Battery.Configure(enabled);Changed?.Invoke();_ = SaveAsync();}
    public void SetConnectionsRecording(bool enabled)
    {
        Settings.ConnectionsRecording=enabled;
        Connections.Configure(enabled,ConnectionAssets.Cities.FirstOrDefault(city=>city.Id==Settings.ConnectionsHomeCityId)?.Point);
        Changed?.Invoke();_ = SaveAsync();
    }
    public void SetConnectionsHomeCity(string? id)
    {
        var home=ConnectionAssets.Cities.FirstOrDefault(city=>city.Id==id);
        Settings.ConnectionsHomeCityId=home?.Id;Connections.Configure(Settings.ConnectionsRecording,home?.Point);
        Changed?.Invoke();_ = SaveAsync();
    }
    public void SetConnectionsAnimation(bool enabled) {Settings.ConnectionsAnimation=enabled;Changed?.Invoke();_ = SaveAsync();}
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
    public string MetricId(string kind)=>kind switch {"cpu"=>"cpu/usage","cputemp"=>"cpu/temperature","cpupump"=>"cpu/pump","gpu"=>(SelectedGpuId??"unavailable")+"/usage","gputemp"=>(SelectedGpuId??"unavailable")+"/temperature","gpufan"=>(SelectedGpuId??"unavailable")+"/fan","memory"=>"memory/usage",_=>"network/download"};
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
    private static readonly TimeSpan QuitDeadline=TimeSpan.FromSeconds(5),SessionEndingDeadline=TimeSpan.FromSeconds(3);
    private Task StopCollectorAsync(TimeSpan deadline)=>collectorStop??=Task.Run(async ()=>
    {
        // One absolute deadline bounds the whole stop, so a hung provider never keeps DeadWare alive.
        // Network capture stops without the hardware worker; every history drains independently.
        var expired=Task.Delay(deadline);
        var networkStop=Collector.StopNetworkCaptureAsync();
        var providerStop=Collector.DisposeAsync().AsTask();
        Collector.SampleAvailable-=Cooling.TryRecord;
        Collector.SampleAvailable-=GraphHistory.TryRecord;
        Collector.SampleAvailable-=Battery.TryRecord;
        var stopped=Task.WhenAll(providerStop,Cooling.StopAsync(),GraphHistory.StopAsync(),Battery.StopAsync(),StopConnectionsAsync(networkStop,deadline/2));
        if(await Task.WhenAny(stopped,expired).ConfigureAwait(false)==stopped)await stopped.ConfigureAwait(false);
        else diagnostics?.Record("collector_stop_timeout",new {DeadlineSeconds=deadline.TotalSeconds,NetworkCaptureStopped=networkStop.IsCompleted},networkStop.Exception?.GetBaseException());
    });
    private async Task StopConnectionsAsync(Task networkStop,TimeSpan captureDeadline)
    {
        if(Connections is null)return;
        // Wait for the final drain only part of the deadline; the rest flushes what is already
        // buffered even if network capture hangs or fails to stop.
        await Task.WhenAny(networkStop,Task.Delay(captureDeadline)).ConfigureAwait(false);
        Collector.SampleAvailable-=Connections.TryRecord;
        Collector.FinalConnectionsAvailable-=Connections.TryRecord;
        await Connections.StopAsync().ConfigureAwait(false);
    }
    internal void StopCollectorForSessionEnding()
    {
        // Windows session ending cannot wait for a dispatcher continuation. The shared stop
        // runs entirely on the pool, stops ETW first, and is bounded even if hardware hangs.
        try
        {
            StopCollectorAsync(SessionEndingDeadline).WaitAsync(SessionEndingDeadline).GetAwaiter().GetResult();
            diagnostics?.Record("session_end_collector_stopped");
        }
        catch(TimeoutException e) {diagnostics?.Record("session_end_collector_stop_timeout",new {DeadlineSeconds=SessionEndingDeadline.TotalSeconds},e);}
        catch(Exception e) {diagnostics?.Record("session_end_collector_stop_failed",error:e);}
    }
    public void Place(Window window,string key,double width,double height)
    {
        window.Width=width;window.Height=height;
        Settings.Windows.TryGetValue(key,out var saved);
        if(key=="widget")window.Topmost=Settings.WidgetAlwaysOnTop;
        else if(saved is not null)window.Topmost=saved.Topmost;
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
    public void OpenDetached(string kind)=>OpenDetachedCore(PopoutGraphMode.Resolve(kind,Settings.JoinCpuGpuPopouts));
    private void OpenDetachedCore(string kind)
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
        try {await SaveAsync();}finally {await StopCollectorAsync(QuitDeadline);}
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

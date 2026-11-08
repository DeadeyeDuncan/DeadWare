using System.Collections.Frozen;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeadWare.Core;
namespace DeadWare.App;
/// <summary>A Show in Connections request: the interval and the bare application name, or null for every application. It lives only in memory.</summary>
public sealed record ConnectionsHistoryRequest(DateTimeOffset From, DateTimeOffset To, string? Application);
public sealed class UiSession
{
    public Collector Collector {get;}
    public CoolingHistoryService Cooling {get;}
    public GraphHistoryService GraphHistory {get;}
    public BatteryHistoryService Battery {get;}
    public UsageHistoryService Usage {get;}
    /// <summary>Null only in fixtures built without the constructor, so every use outside it is null-guarded.</summary>
    public ProcessHistoryService ProcessHistory {get;}
    public AlertService Alerts {get;}
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
    /// <summary>Raised on the UI thread when a store's clear starts clearing and when it leaves Clearing, so the page it feeds re-reads.</summary>
    public event Action<HistoryClearStore>? HistoryClearChanged;
    // The Don't record keys, set from the loaded settings; null in fixtures built without the constructor.
    private FrozenSet<string>? excludedKeys;
    // Each store's clear phase as the timer last saw it (indexed by HistoryClearStore); null until the first check.
    private HistoryClearPhase[]? clearPhases;
    /// <summary>Raised when a page asks the dashboard to show Usage history for the day containing the given time.</summary>
    public event Action<DateTimeOffset>? UsageDayRequested;
    public void RequestUsageDay(DateTimeOffset at)=>UsageDayRequested?.Invoke(at);
    /// <summary>Raised when a page asks the dashboard to show Connections History for an interval, and only one application when it names one.</summary>
    public event Action<ConnectionsHistoryRequest>? ConnectionsHistoryRequested;
    /// <summary>Asks the dashboard for Connections History over [from, to): at most 30 days and 1 minute, with the bare application name or null for every application.</summary>
    public void RequestConnectionsHistory(DateTimeOffset from,DateTimeOffset to,string? application)=>ConnectionsHistoryRequested?.Invoke(new(from,to,application));
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
        excludedKeys=AppExclusions.Keys(Settings.ExcludedApps);
        writer=new(settingsPath);
        layout=new(Settings.RestoreOpenWindows?Settings.OpenWindows:[]);
        diagnostics?.Record("settings_loaded",new {Status=SettingsStatus,Settings.RestoreOpenWindows,OpenWindows=layout.Remembered,Settings.SelectedGpuId,Disk=LayoutDiagnostics.ReadSettingsState(settingsPath)});
        saveTimer.Tick+=async (_,_)=>{saveTimer.Stop();await SaveAsync();};
        timer.Interval=TimeSpan.FromSeconds(Math.Min(1,Settings.PollingIntervalSeconds));
        Collector=new(new CollectorOptions(ExtendedSensors:Settings.ExtendedSensors,PollingIntervalSeconds:Settings.PollingIntervalSeconds,StorageSensors:Settings.StorageSensors,ProcessMonitoring:Settings.ProcessMonitoring,ProcessDiskTracing:ProcessFeatures.DiskColumns&&Collector.IsElevated));
        GraphHistory=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"live-graph-history.db"),Environment.MachineName,Collector.History);
        Collector.SampleAvailable+=GraphHistory.TryRecord;
        Cooling=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"cooling-history.db"),Environment.MachineName,Settings.CoolingConfigurationId);
        Cooling.Configure(Settings.CoolingRecording,Settings.CoolingNotifications,Settings.CoolingDetailedDays,Settings.CoolingSummaryDays,Settings.CoolingConfigurationId);
        Collector.SampleAvailable+=Cooling.TryRecord;
        Battery=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"battery-history.db"),Environment.MachineName);
        Battery.Configure(Settings.BatteryRecording);
        Collector.SampleAvailable+=Battery.TryRecord;
        Usage=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"usage-history.db"),Environment.MachineName);
        Usage.Configure(Settings.UsageRecording);Usage.SetExclusions(excludedKeys);
        Collector.SampleAvailable+=Usage.TryRecord;
        ProcessHistory=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"process-history.db"),Environment.MachineName);
        ProcessHistory.Configure(Settings.ProcessMonitoring,Settings.UsageRecording);ProcessHistory.SetExclusions(excludedKeys);
        Collector.SampleAvailable+=ProcessHistory.TryRecord;
        Alerts=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"alerts.db"),Environment.MachineName,Usage,()=>Current is null||SelectedGpuId is null?null:GpuName,connectionNames:()=>Connections!.ReadApplicationNamesAsync());
        Alerts.Configure(Settings.Alerts,Settings.AlertNotifications,Settings.FirstUseIgnoredApps,Settings.ExcludedApps);
        Collector.SampleAvailable+=Alerts.TryRecordBattery;
        Collector.SampleAvailable+=Alerts.TryRecordNetworkUse;
        Connections=new(Path.Combine(Path.GetDirectoryName(settingsPath)!,"connections.db"));
        var home=ConnectionAssets.Cities.FirstOrDefault(city=>city.Id==Settings.ConnectionsHomeCityId);
        Settings.ConnectionsHomeCityId=home?.Id;
        Connections.Configure(Settings.ConnectionsRecording,home?.Point);Connections.SetExclusions(excludedKeys);
        Collector.SampleAvailable+=Connections.TryRecord;
        Collector.FinalConnectionsAvailable+=Connections.TryRecord;
        timer.Tick+=(_,_)=>
        {
            if(Settings.SelectedGpuId is null && Current is {Gpus.Count:>0} reading)
            {Settings.SelectedGpuId=DeviceSelection.Resolve(null,reading.Gpus);_ = SaveAsync();}
            CheckClearStates();
            Changed?.Invoke();
        };
    }
    public void Start()
    {
        // Restore the saved window types exactly; the launch preference only affects new requests.
        foreach(string key in layout.Remembered)OpenDetachedCore(key);
        GraphHistory.Start();Cooling.Start();Battery.Start();Usage.Start();ProcessHistory.Start();Alerts.Start();Connections.Start();_ = SaveAsync();Collector.Start();timer.Start();
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
        if(!GraphColorSettings.IsKind(kind)||color is not null&&!GraphColorSettings.IsColor(color))return;
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
    public void SetStorageSensors(bool enabled) {Settings.StorageSensors=enabled;_ = SaveAsync();}
    public void SetGraphStyle(string kind,string pattern,bool glow,double thickness=1,double glowStrength=1)
    {
        if(kind=="network")kind="download";
        if(!GraphColorSettings.IsKind(kind)||!GraphLineStyles.IsPattern(pattern))return;
        var style=GraphLineStyles.Normalize(new GraphLineStyle(pattern,glow,thickness,glowStrength));
        if(Settings.GraphStyles.GetValueOrDefault(kind,new())==style)return;
        if(style==new GraphLineStyle())Settings.GraphStyles.Remove(kind);else Settings.GraphStyles[kind]=style;
        Changed?.Invoke();_ = SaveAsync();
    }
    /// <summary>Drops every per-core colour and line style so cores follow the CPU line again.</summary>
    public void ResetCpuCoreStyles()
    {
        bool removed=false;
        foreach(string key in Settings.GraphColors.Keys.Where(key=>GraphColorSettings.TryCoreIndex(key,out _)).ToArray())removed|=Settings.GraphColors.Remove(key);
        foreach(string key in Settings.GraphStyles.Keys.Where(key=>GraphColorSettings.TryCoreIndex(key,out _)).ToArray())removed|=Settings.GraphStyles.Remove(key);
        if(!removed)return;
        Theme.SetGraphColors(Settings.GraphColors);Changed?.Invoke();_ = SaveAsync();
    }
    /// <summary>Copies the CPU line style (and optionally its colour) onto cores 0..count-1.</summary>
    public void ApplyStyleToAllCores(int count,bool copyColor)
    {
        count=Math.Clamp(count,0,GraphColorSettings.MaxCores);
        var style=Settings.GraphStyles.GetValueOrDefault("cpu",new());string? color=Settings.GraphColors.GetValueOrDefault("cpu");
        bool changed=false;
        for(int i=0;i<count;i++)
        {
            string key=GraphColorSettings.CoreKey(i);
            if(style==new GraphLineStyle())changed|=Settings.GraphStyles.Remove(key);
            else if(Settings.GraphStyles.GetValueOrDefault(key)!=style) {Settings.GraphStyles[key]=style;changed=true;}
            if(!copyColor)continue;
            if(color is null)changed|=Settings.GraphColors.Remove(key);
            else if(Settings.GraphColors.GetValueOrDefault(key)!=color) {Settings.GraphColors[key]=color;changed=true;}
        }
        if(!changed)return;
        Theme.SetGraphColors(Settings.GraphColors);Changed?.Invoke();_ = SaveAsync();
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
    public void SetCpuCoreUsage(bool enabled)
    {
        if(Settings.ShowCpuCoreUsage==enabled)return;
        Settings.ShowCpuCoreUsage=enabled;Changed?.Invoke();_ = SaveAsync();
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
    public void SetProcessMonitoring(bool enabled)
    {
        Settings.ProcessMonitoring=enabled;Collector.SetProcessMonitoring(enabled);ProcessHistory?.Configure(enabled,Settings.UsageRecording);Changed?.Invoke();_ = SaveAsync();
    }
    public void SetProcessColumns(IEnumerable<string> keys)
    {
        var next=ProcessColumnKeys.Normalize(keys);
        if(Settings.ProcessColumns.SequenceEqual(next,StringComparer.Ordinal))return;
        Settings.ProcessColumns=next;Changed?.Invoke();_ = SaveAsync();
    }
    public void SetRestoreOpenWindows(bool enabled) {Settings.RestoreOpenWindows=enabled;_ = SaveAsync();}
    public void SetStartInTray(bool enabled) {Settings.StartInTray=enabled;_ = SaveAsync();}
    public void SetBatteryRecording(bool enabled) {Settings.BatteryRecording=enabled;Battery.Configure(enabled);Changed?.Invoke();_ = SaveAsync();}
    public void SetUsageRecording(bool enabled) {Settings.UsageRecording=enabled;Usage.Configure(enabled);ProcessHistory?.Configure(Settings.ProcessMonitoring,enabled);Changed?.Invoke();_ = SaveAsync();}
    public void SetAlertRules(IReadOnlyList<AlertRule> rules)
    {
        Settings.Alerts=AlertRules.Normalize(rules);Alerts.Configure(Settings.Alerts,Settings.AlertNotifications,Settings.FirstUseIgnoredApps,Settings.ExcludedApps);Changed?.Invoke();_ = SaveAsync();
    }
    public void SetAlertNotifications(bool enabled)
    {
        if(Settings.AlertNotifications==enabled)return;
        Settings.AlertNotifications=enabled;Alerts.Configure(Settings.Alerts,enabled,Settings.FirstUseIgnoredApps,Settings.ExcludedApps);Changed?.Invoke();_ = SaveAsync();
    }
    /// <summary>Adds the app to the first network use Don't alert list (normalized, at most 256), passes it to the alert worker, then saves.</summary>
    public void IgnoreFirstUse(string name)
    {
        Settings.FirstUseIgnoredApps=FirstNetworkUse.NormalizeIgnored([..Settings.FirstUseIgnoredApps,name]);
        Alerts?.Configure(Settings.Alerts,Settings.AlertNotifications,Settings.FirstUseIgnoredApps,Settings.ExcludedApps);
        Changed?.Invoke();_ = SaveAsync();
    }
    /// <summary>Removes the app key (<see cref="AppKey.Of"/>, ordinal) from the Don't alert list, passes the list to the alert worker, then saves.</summary>
    public void UnignoreFirstUse(string key)
    {
        Settings.FirstUseIgnoredApps=FirstNetworkUse.NormalizeIgnored(Settings.FirstUseIgnoredApps.Where(name=>!string.Equals(AppKey.Of(name),key,StringComparison.Ordinal)));
        Alerts?.Configure(Settings.Alerts,Settings.AlertNotifications,Settings.FirstUseIgnoredApps,Settings.ExcludedApps);
        Changed?.Invoke();_ = SaveAsync();
    }
    /// <summary>The Don't record keys (<see cref="AppKey.Of"/>, ordinal): the cached set, or in a fixture without one, the settings list's keys.</summary>
    public FrozenSet<string> ExcludedKeys=>excludedKeys??AppExclusions.Keys(Settings?.ExcludedApps??[]);
    /// <summary>True when the name's key is on the Don't record list. Never throws.</summary>
    public bool IsExcluded(string? name)=>!string.IsNullOrEmpty(name)&&ExcludedKeys.Contains(AppKey.Of(name));
    /// <summary>Normalizes the list and, when it changed, passes its keys to the history services and the list to the alert worker, then saves.</summary>
    public void SetExcludedApps(IEnumerable<string> names)
    {
        var next=AppExclusions.Normalize(names);
        if(Settings.ExcludedApps.SequenceEqual(next,StringComparer.Ordinal))return;
        Settings.ExcludedApps=next;var keys=excludedKeys=AppExclusions.Keys(next);
        Connections?.SetExclusions(keys);Usage?.SetExclusions(keys);ProcessHistory?.SetExclusions(keys);
        Alerts?.Configure(Settings.Alerts,Settings.AlertNotifications,Settings.FirstUseIgnoredApps,Settings.ExcludedApps);
        Changed?.Invoke();_ = SaveAsync();
    }
    /// <summary>Each present store's clear state and whether it offers Try again, in the progress block's order.</summary>
    private (HistoryClearStore Store,HistoryClearState State,bool CanRetry)[] ClearStates()
    {
        var states=new List<(HistoryClearStore,HistoryClearState,bool)>(4);
        if(Connections is { } connections)states.Add((HistoryClearStore.Connections,connections.ClearState,connections.CanRetryClear));
        if(Usage is { } usage)states.Add((HistoryClearStore.Usage,usage.ClearState,usage.CanRetryClear));
        if(ProcessHistory is { } process)states.Add((HistoryClearStore.ProcessHistory,process.ClearState,process.CanRetryClear));
        if(Alerts is { } alerts)states.Add((HistoryClearStore.Alerts,alerts.ClearState,alerts.CanRetryClear));
        return states.ToArray();
    }
    private static bool Active(HistoryClearState state)=>state.Phase is HistoryClearPhase.Queued or HistoryClearPhase.Clearing||state is {Phase:HistoryClearPhase.Failed,MarkerSaved:true};
    /// <summary>One clear at a time: true while any store's clear is Queued or Clearing, or Failed with its marker saved.</summary>
    public bool ClearActive=>ClearStates().Any(s=>Active(s.State));
    /// <summary>A store is Failed with its marker saved and cannot retry, because its worker has stopped: the clear resumes at the next start.</summary>
    public bool ClearWaitingForRestart=>ClearStates().Any(s=>s.State is {Phase:HistoryClearPhase.Failed,MarkerSaved:true}&&!s.CanRetry);
    /// <summary>Posts the request to the four stores. Refused (false) while <see cref="ClearActive"/>.</summary>
    public bool ClearHistory(HistoryClear request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(ClearActive)return false;
        // Only the UI posts, so no store becomes active after the check, except one resuming a marker found at startup: that store
        // already runs a clear, and its own refusal (the throw) leaves it alone.
        Action<HistoryClear>?[] posts=[Connections is null?null:Connections.Clear,Usage is null?null:Usage.Clear,ProcessHistory is null?null:ProcessHistory.Clear,Alerts is null?null:Alerts.Clear];
        // The timer compares against Queued from here on, so a clear that finishes before the next tick still raises HistoryClearChanged.
        var seen=clearPhases??=new HistoryClearPhase[4];
        for(int store=0;store<posts.Length;store++)
        {
            if(posts[store] is not { } post)continue;
            try {post(request);seen[store]=HistoryClearPhase.Queued;}
            catch(InvalidOperationException) { }
        }
        return true;
    }
    /// <summary>Try again for one store: re-posts its kept request while it failed and its worker runs.</summary>
    public void RetryClear(HistoryClearStore store)
    {
        switch(store)
        {
            case HistoryClearStore.Connections:Connections?.RetryClear();break;
            case HistoryClearStore.Usage:Usage?.RetryClear();break;
            case HistoryClearStore.ProcessHistory:ProcessHistory?.RetryClear();break;
            case HistoryClearStore.Alerts:Alerts?.RetryClear();break;
        }
        // A re-posted store left Failed at once (Queued, or further already); the timer then compares against Queued, as after ClearHistory.
        var state=ClearStates().Where(s=>s.Store==store).Select(s=>s.State).FirstOrDefault();
        if(state is not null&&state.Phase!=HistoryClearPhase.Failed)(clearPhases??=new HistoryClearPhase[4])[(int)store]=HistoryClearPhase.Queued;
    }
    /// <summary>The progress block's lines: Connections history, Usage history, Application resources, Alert log, each store with a clear or a result.</summary>
    public IReadOnlyList<HistoryClearLine> ClearLines()=>Array.AsReadOnly(ClearStates().Where(s=>s.State.Phase!=HistoryClearPhase.None).Select(s=>new HistoryClearLine(s.Store,s.State,s.CanRetry)).ToArray());
    /// <summary>Called by the 1-second timer on the UI thread: raises <see cref="HistoryClearChanged"/> for each store whose clear started
    /// clearing or left Clearing since the last check. A clear that went from Queued to Done or Failed between two checks left Clearing unseen.</summary>
    internal void CheckClearStates()
    {
        var seen=clearPhases??=new HistoryClearPhase[4];
        foreach(var (store,state,_) in ClearStates())
        {
            var previous=seen[(int)store];var phase=state.Phase;seen[(int)store]=phase;
            bool started=phase==HistoryClearPhase.Clearing&&previous!=HistoryClearPhase.Clearing;
            bool ended=previous==HistoryClearPhase.Clearing&&phase!=HistoryClearPhase.Clearing||previous==HistoryClearPhase.Queued&&phase is HistoryClearPhase.Done or HistoryClearPhase.Failed;
            if(started||ended)HistoryClearChanged?.Invoke(store);
        }
    }
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
    /// <summary>Saves the Destinations table's scope: "All", "Internet" or "Local"; anything else is "All".</summary>
    public void SetConnectionsDestinationScope(string scope) {Settings.ConnectionsDestinationScope=ConnectionDestinations.ParseScope(scope).ToString();Changed?.Invoke();_ = SaveAsync();}
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
    public string MetricId(string kind)=>kind switch {"cpu"=>"cpu/usage","cputemp"=>"cpu/temperature","cpupump"=>"cpu/pump","gpu"=>(SelectedGpuId??"unavailable")+"/usage","gputemp"=>(SelectedGpuId??"unavailable")+"/temperature","gpufan"=>(SelectedGpuId??"unavailable")+"/fan","memory"=>"memory/usage","storage"=>"storage/total/read",_=>"network/download"};
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
    private static readonly TimeSpan QuitDeadline=TimeSpan.FromSeconds(5),HistoryDeadline=TimeSpan.FromSeconds(20),SessionEndingDeadline=TimeSpan.FromSeconds(3);
    /// <summary>Every deadline is absolute from the start of the stop; the first caller's deadlines win.</summary>
    private Task StopCollectorAsync(TimeSpan providerDeadline,TimeSpan finalDrainDeadline,TimeSpan historyDeadline)=>collectorStop??=Task.Run(async ()=>
    {
        // Native providers share one deadline, so a hung provider never keeps DeadWare alive. History
        // drains need no provider and get their own cap, so a normal flush is never cut short.
        Task providerExpired=Task.Delay(providerDeadline),finalDrainExpired=Task.Delay(finalDrainDeadline),historyExpired=Task.Delay(historyDeadline);
        var networkStop=Begin(Collector.StopNetworkCaptureAsync);
        var providerStop=Begin(()=>Collector.DisposeAsync().AsTask());
        // Start the connection flush before anything else can throw synchronously.
        var connections=StopConnectionsAsync(networkStop,finalDrainExpired);
        Collector.SampleAvailable-=Cooling.TryRecord;
        Collector.SampleAvailable-=GraphHistory.TryRecord;
        Collector.SampleAvailable-=Battery.TryRecord;
        Collector.SampleAvailable-=Usage.TryRecord;
        // Null only in shutdown fixtures built without a constructor, like Connections.
        if(ProcessHistory is not null)Collector.SampleAvailable-=ProcessHistory.TryRecord;
        Collector.SampleAvailable-=Alerts.TryRecordBattery;Collector.SampleAvailable-=Alerts.TryRecordNetworkUse;
        (string Stage,Task Stop)[] drains=[("connections",connections),("cooling",Begin(Cooling.StopAsync)),("graph history",Begin(GraphHistory.StopAsync)),("battery",Begin(Battery.StopAsync)),("usage history",Begin(Usage.StopAsync)),("process history",ProcessHistory is null?Task.CompletedTask:Begin(ProcessHistory.StopAsync)),("alerts",Begin(Alerts.StopAsync))];
        await Task.WhenAny(providerStop,providerExpired).ConfigureAwait(false);
        await Task.WhenAny(Task.WhenAll(drains.Select(d=>d.Stop)),historyExpired).ConfigureAwait(false);
        (string Stage,Task Stop)[] stages=[("network capture",networkStop),("hardware provider",providerStop),..drains];
        string[] outstanding=stages.Where(s=>!s.Stop.IsCompleted).Select(s=>s.Stage).ToArray();
        if(outstanding.Length==0)await Task.WhenAll(stages.Select(s=>s.Stop)).ConfigureAwait(false);
        else diagnostics?.Record("collector_stop_timeout",new {ProviderDeadlineSeconds=providerDeadline.TotalSeconds,HistoryDeadlineSeconds=historyDeadline.TotalSeconds,Outstanding=outstanding},
            stages.Select(s=>s.Stop.Exception?.GetBaseException()).FirstOrDefault(e=>e is not null));
    });
    /// <summary>A stop that throws before returning a task must not skip the other stops or the deadlines.</summary>
    private static Task Begin(Func<Task> stop) {try {return stop();} catch(Exception e) {return Task.FromException(e);}}
    private async Task StopConnectionsAsync(Task networkStop,Task finalDrainExpired)
    {
        if(Connections is null)return;
        // Keep the final drain subscribed until it arrives or its deadline passes, then flush what is
        // already buffered even if network capture hangs or fails to stop.
        await Task.WhenAny(networkStop,finalDrainExpired).ConfigureAwait(false);
        Collector.SampleAvailable-=Connections.TryRecord;
        Collector.FinalConnectionsAvailable-=Connections.TryRecord;
        await Connections.StopAsync().ConfigureAwait(false);
    }
    internal void StopCollectorForSessionEnding()
    {
        // Windows session ending cannot wait for a dispatcher continuation. The shared stop
        // runs entirely on the pool, stops ETW first, and is bounded even if hardware hangs.
        // Connections flush halfway through the window even if network capture hangs.
        try
        {
            StopCollectorAsync(SessionEndingDeadline,SessionEndingDeadline/2,SessionEndingDeadline).WaitAsync(SessionEndingDeadline).GetAwaiter().GetResult();
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
    /// <summary>Test hook: when set, an allowed pop-out is built and handed here instead of shown and remembered.</summary>
    internal Action<Window>? ShowWindow {get;set;}
    private Window CreateDetached(string kind)=>kind=="batteryhistory"?new BatteryHistoryWindow(this):kind=="usagehistory"?new UsageHistoryWindow(this):kind=="widget"?new WidgetWindow(this):new GraphWindow(this,kind);
    public void OpenDetached(string kind)=>OpenDetachedCore(PopoutGraphMode.Resolve(kind,Settings.JoinCpuGpuPopouts));
    private void OpenDetachedCore(string kind)
    {
        if(!DetachedWindowState.IsAllowed(kind))return;
        if(ShowWindow is { } show){show(CreateDetached(kind));return;}
        if(layout.Ending)return;
        if(detached.TryGetValue(kind,out var existing)) {existing.WindowState=WindowState.Normal;existing.Activate();return;}
        var window=CreateDetached(kind);
        detached[kind]=window;
        window.Closed+=(_,_)=>{detached.Remove(kind);layout.Close(kind);diagnostics?.Record("window_closed",new {Kind=kind,layout.Ending,OpenWindows=layout.Remembered});QueueSave();};
        window.Show();layout.Open(kind);diagnostics?.Record("window_opened",new {Kind=kind,OpenWindows=layout.Remembered});QueueSave();
    }
    /// <summary>Opens (or raises) one drive's pop-out; it closes with the app but is not remembered across restarts.</summary>
    public void OpenDriveWindow(StorageDrive drive)
    {
        string key="storage:"+drive.Id;
        if(ShowWindow is { } show){show(new GraphWindow(this,drive));return;}
        if(layout.Ending)return;
        if(detached.TryGetValue(key,out var existing)) {existing.WindowState=WindowState.Normal;existing.Activate();return;}
        var window=new GraphWindow(this,drive);detached[key]=window;
        window.Closed+=(_,_)=>detached.Remove(key);
        window.Show();
    }
    /// <summary>Opens (or raises) the CPU core line editor; it closes with the app but is not remembered across restarts.</summary>
    public void OpenCpuCoreStyles()
    {
        const string key="cpucorestyles";
        if(ShowWindow is { } show){show(new CpuCoreStyleWindow(this));return;}
        if(layout.Ending)return;
        if(detached.TryGetValue(key,out var existing)) {existing.WindowState=WindowState.Normal;existing.Activate();return;}
        var window=new CpuCoreStyleWindow(this);detached[key]=window;
        window.Closed+=(_,_)=>detached.Remove(key);
        window.Show();
    }
    public Task ShutdownAsync()=>shutdown??=ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        diagnostics?.Record("shutdown_begin",new {OpenWindows=layout.Remembered});
        layout.BeginShutdown();timer.Stop();saveTimer.Stop();CaptureAll();
        foreach(var window in detached.Values.ToArray())window.Close();
        try {await SaveAsync();}finally {await StopCollectorAsync(QuitDeadline,QuitDeadline,HistoryDeadline);}
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

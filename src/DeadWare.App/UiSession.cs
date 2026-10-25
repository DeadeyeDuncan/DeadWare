using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeadWare.Core;
namespace DeadWare.App;
public sealed class UiSession
{
    public Collector Collector {get;}
    public UserSettings Settings {get;}
    public string SettingsStatus {get;private set;}="";
    private readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadWare","settings.json");
    private readonly SettingsSnapshotWriter writer;
    private readonly DetachedWindowState layout;
    private long saveRevision;
    private bool restarting;
    private Task? shutdown;
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
    public UiSession()
    {
        try
        {
            var loaded=File.Exists(settingsPath)?SettingsCodec.Read(File.ReadAllText(settingsPath)):new SettingsRead(new(),null);
            Settings=loaded.Settings;SettingsStatus=loaded.Error??"";
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {Settings=new();SettingsStatus="Settings unavailable; using defaults.";}
        writer=new(settingsPath);
        layout=new(Settings.RestoreOpenWindows?Settings.OpenWindows:[]);
        saveTimer.Tick+=async (_,_)=>{saveTimer.Stop();await SaveAsync();};
        Collector=new(new CollectorOptions(ExtendedSensors:Settings.ExtendedSensors));
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
        Collector.Start();timer.Start();
    }
    public void SetRange(int seconds) {Settings.HistorySeconds=seconds;Changed?.Invoke();_ = SaveAsync();}
    public void SetGpu(string? id) {Settings.SelectedGpuId=id;Changed?.Invoke();_ = SaveAsync();}
    public void SetTheme(string theme) {Settings.Theme=theme;Theme.Apply(theme);Changed?.Invoke();_ = SaveAsync();}
    public void SetExtendedSensors(bool enabled) {Settings.ExtendedSensors=enabled;_ = SaveAsync();}
    public void SetRestoreOpenWindows(bool enabled) {Settings.RestoreOpenWindows=enabled;_ = SaveAsync();}
    public string MetricId(string kind)=>kind switch {"cpu"=>"cpu/usage","cputemp"=>"cpu/temperature","gpu"=>(SelectedGpuId??"unavailable")+"/usage","gputemp"=>(SelectedGpuId??"unavailable")+"/temperature",_=>"network/download"};
    public async Task SaveAsync()
    {
        Settings.OpenWindows=layout.Remembered.ToList();
        var text=SettingsCodec.Write(Settings);long revision=++saveRevision;
        try {await Task.Run(()=>writer.Save(revision,text));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {SettingsStatus="Settings could not be saved; current choices are temporary.";}
    }
    private void QueueSave()
    {
        if(layout.Ending)return;
        saveTimer.Stop();saveTimer.Start();
    }
    private void CaptureAll() {foreach(var capture in captureWindows.Values.ToArray())capture();}
    public void SaveForSessionEnding()
    {
        layout.BeginShutdown();timer.Stop();saveTimer.Stop();CaptureAll();
        Settings.OpenWindows=layout.Remembered.ToList();
        // Synchronous final disk checkpoint never waits for a UI continuation. The writer's
        // revision gate prevents a previously queued background save from replacing it later.
        try {writer.Save(++saveRevision,SettingsCodec.Write(Settings));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {SettingsStatus="The final window layout could not be saved.";}
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
        var window=kind=="widget"?(Window)new WidgetWindow(this):new GraphWindow(this,kind);
        detached[kind]=window;
        window.Closed+=(_,_)=>{detached.Remove(kind);layout.Close(kind);QueueSave();};
        window.Show();layout.Open(kind);QueueSave();
    }
    public Task ShutdownAsync()=>shutdown??=ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        layout.BeginShutdown();timer.Stop();saveTimer.Stop();CaptureAll();
        foreach(var window in detached.Values.ToArray())window.Close();
        await SaveAsync();await Collector.DisposeAsync();
    }
    public async void RestartElevated()
    {
        if(restarting||layout.Ending)return;restarting=true;
        try
        {
            CaptureAll();await SaveAsync();
            if(await GuiInstance.LaunchElevatedReplacementAsync())Application.Current.MainWindow.Close();
            else MessageBox.Show("The administrator replacement did not confirm this Windows user. Monitoring continues with current permissions.","DeadWare");
        }
        catch(System.ComponentModel.Win32Exception e) {MessageBox.Show(e.NativeErrorCode==1223?"Administrator restart was cancelled. Monitoring continues with current permissions.":"Windows could not restart DeadWare as administrator.","DeadWare");}
        catch(Exception e) {MessageBox.Show($"Restart failed: {e.Message}","DeadWare");}
        finally {restarting=false;}
    }
}

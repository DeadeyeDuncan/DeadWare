using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeadWare.Core;
namespace DeadWare.App;
public sealed class UiSession
{
    public Collector Collector {get;}=new();
    public UserSettings Settings {get;}
    public string SettingsStatus {get;private set;}="";
    private readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadWare","settings.json");
    private readonly SemaphoreSlim saveGate=new(1,1);
    private readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromSeconds(1)};
    private readonly Dictionary<string,Window> detached=new();
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
        timer.Tick+=(_,_)=>
        {
            if(Settings.SelectedGpuId is null && Current is {Gpus.Count:>0} reading)
            {Settings.SelectedGpuId=DeviceSelection.Resolve(null,reading.Gpus);_ = SaveAsync();}
            Changed?.Invoke();
        };
    }
    public void Start() {Collector.Start();timer.Start();}
    public void SetRange(int seconds) {Settings.HistorySeconds=seconds;Changed?.Invoke();_ = SaveAsync();}
    public void SetGpu(string? id) {Settings.SelectedGpuId=id;Changed?.Invoke();_ = SaveAsync();}
    public void SetTheme(string theme) {Settings.Theme=theme;Theme.Apply(theme);Changed?.Invoke();_ = SaveAsync();}
    public string MetricId(string kind)=>kind switch {"cpu"=>"cpu/usage","cputemp"=>"cpu/temperature","gpu"=>(SelectedGpuId??"unavailable")+"/usage","gputemp"=>(SelectedGpuId??"unavailable")+"/temperature",_=>"network/download"};
    public async Task SaveAsync()
    {
        var text=SettingsCodec.Write(Settings);
        await saveGate.WaitAsync();
        try {Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);var temp=settingsPath+"."+Guid.NewGuid().ToString("N")+".tmp";await File.WriteAllTextAsync(temp,text);File.Move(temp,settingsPath,true);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {SettingsStatus="Settings could not be saved; current choices are temporary.";}
        finally {saveGate.Release();}
    }
    public void Place(Window window,string key,double width,double height)
    {
        window.Width=width;window.Height=height;
        if(Settings.Windows.TryGetValue(key,out var saved))
        {
            // Bound even enormous but finite persisted dimensions before passing them to WPF.
            var p=WindowPlacement.Clamp(saved,NativePlacement.WorkAreas(),width,height);
            window.Left=p.Left;window.Top=p.Top;window.Width=p.Width;window.Height=p.Height;window.Topmost=p.Topmost;window.WindowStartupLocation=WindowStartupLocation.Manual;
        }
        else window.WindowStartupLocation=WindowStartupLocation.CenterScreen;
        window.SourceInitialized+=(_,_)=>NativePlacement.Clamp(window);
        window.Closing+=(_,_)=>Remember(window,key);
    }
    private void Remember(Window window,string key)
    {
        var r=window.WindowState==WindowState.Normal?new Rect(window.Left,window.Top,window.Width,window.Height):window.RestoreBounds;
        Settings.Windows[key]=new(r.Left,r.Top,r.Width,r.Height,window.Topmost);_ = SaveAsync();
    }
    public void OpenDetached(string kind)
    {
        if(detached.TryGetValue(kind,out var existing)) {existing.WindowState=WindowState.Normal;existing.Activate();return;}
        var window=kind=="widget"?(Window)new WidgetWindow(this):new GraphWindow(this,kind);
        detached[kind]=window;window.Closed+=(_,_)=>detached.Remove(kind);window.Show();
    }
    public async Task ShutdownAsync()
    {
        timer.Stop();foreach(var window in detached.Values.ToArray()) window.Close();
        await SaveAsync();await Collector.DisposeAsync();
    }
    public void RestartElevated()
    {
        try
        {
            var path=Environment.ProcessPath??throw new InvalidOperationException("Executable path unavailable.");
            Process.Start(new ProcessStartInfo(path) {UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory});
            Application.Current.MainWindow.Close();
        }
        catch(System.ComponentModel.Win32Exception e) {MessageBox.Show(e.NativeErrorCode==1223?"Administrator restart was cancelled. Monitoring continues with current permissions.":"Windows could not restart DeadWare as administrator.","DeadWare");}
        catch(Exception e) {MessageBox.Show($"Restart failed: {e.Message}","DeadWare");}
    }
}

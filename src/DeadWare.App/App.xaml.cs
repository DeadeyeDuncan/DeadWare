using System.IO;
using System.Text.Json;
using System.Windows;
namespace DeadWare.App;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--diagnose"))
        {
            int exit=0;
            try
            {
                int index=Array.IndexOf(e.Args,"--diagnose");
                if(index+1>=e.Args.Length) throw new ArgumentException("Provide an output file after --diagnose.");
                string output=Path.GetFullPath(e.Args[index+1]);
                int sampleIndex=Array.IndexOf(e.Args,"--samples");
                int count=sampleIndex>=0&&sampleIndex+1<e.Args.Length?int.Parse(e.Args[sampleIndex+1]):3;
                if(count is <1 or >60) throw new ArgumentException("Samples must be between 1 and 60.");
                var readings=new List<Reading>();
                var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using(var collector=new Collector())
                {
                    collector.SampleAvailable+=reading=>{readings.Add(reading);if(readings.Count>=count) done.TrySetResult();};
                    collector.Start();
                    await done.Task.WaitAsync(TimeSpan.FromSeconds(180));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Version="0.1.0",Samples=readings.Take(count)},new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception) {exit=1;}
            Shutdown(exit);return;
        }
        Theme.Apply("Midnight");
        try
        {
            var session=new UiSession();
            Theme.Apply(session.Settings.Theme);
            var main=new MainWindow(session);MainWindow=main;main.Show();session.Start();
        }
        catch(Exception ex) {MessageBox.Show($"DeadWare could not start: {ex.Message}","DeadWare",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
    }
}

using System.Windows;
using System.Windows.Threading;
namespace DeadWare.App;

/// <summary>The WPF confirmation for End task; the actions themselves live in DeadWare.Services.</summary>
internal static class ProcessConfirm
{
    /// <summary>A Yes/No warning whose default button is No, shown on <paramref name="dispatcher"/>'s thread.</summary>
    internal static Func<string,string?,bool> MessageBox(Dispatcher dispatcher)=>(question,summary)=>
        !dispatcher.HasShutdownStarted&&dispatcher.Invoke(()=>
            System.Windows.MessageBox.Show(question+(summary is null?"":"\n\n"+summary),"DeadWare",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes);
}

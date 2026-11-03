using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One row per drive above the selected drive's detail card: its own read / write chart, volumes and SMART summary.</summary>
internal sealed class StoragePage : UserControl
{
    private readonly UiSession session;
    private readonly StackPanel detailBody=new();
    private ChartCard? chart;
    private string? chartDriveId;
    internal StackPanel Rows {get;}=new();
    internal Grid Volumes {get;}=new() {Margin=new Thickness(0,10,0,0)};
    internal StackPanel Smart {get;}=new() {Margin=new Thickness(0,12,0,0)};
    internal Border Detail {get;}
    internal TextBlock Status {get;}=Ui.Text("",11,"Muted");
    internal string? SelectedDriveId {get;private set;}
    /// <summary>Shows a drive's pop-out; tests replace it to inspect the window instead of opening it.</summary>
    internal Action<Window>? ShowWindow {get;set;}

    public StoragePage(UiSession session)
    {
        this.session=session;
        var body=new StackPanel {Margin=new Thickness(6,0,6,6)};
        Status.TextWrapping=TextWrapping.Wrap;Status.Margin=new Thickness(4,0,4,10);body.Children.Add(Status);
        body.Children.Add(Rows);
        var header=new DockPanel {Margin=new Thickness(0,0,0,4)};
        var open=Ui.Button("Open in window ↗",PopOut);DockPanel.SetDock(open,Dock.Right);header.Children.Add(open);
        header.Children.Add(Ui.Heading("Drive detail",14));
        var detail=new StackPanel();detail.Children.Add(header);detail.Children.Add(detailBody);detail.Children.Add(Volumes);detail.Children.Add(Smart);
        Detail=Ui.Panel(detail,new Thickness(16,12,16,14));Detail.Margin=new Thickness(0,10,0,0);body.Children.Add(Detail);
        Content=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Loaded+=(_,_)=>{session.Changed+=OnChanged;Refresh();};
        Unloaded+=(_,_)=>session.Changed-=OnChanged;
        Refresh();
    }

    private void OnChanged()
    {
        if(Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.InvokeAsync(OnChanged);return;}
        if(IsVisible)Refresh();
    }

    private StorageReading Storage=>session.Current?.Storage??StorageReading.Empty;
    private StorageDrive? Selected=>Storage.Drives.FirstOrDefault(d=>d.Id==SelectedDriveId);

    private void Refresh()
    {
        var storage=Storage;
        if(SelectedDriveId is null||storage.Drives.All(d=>d.Id!=SelectedDriveId))SelectedDriveId=storage.Drives.FirstOrDefault()?.Id;
        Status.Text=StatusText(session.Current);Status.Visibility=Status.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        Rows.Children.Clear();
        if(storage.Drives.Count==0)Rows.Children.Add(Ui.Text(session.Current is null?"Discovering drives…":"No drives detected",12,"Muted"));
        foreach(var drive in storage.Drives)Rows.Children.Add(Row(drive,storage.Health.FirstOrDefault(h=>h.DriveId==drive.Id)));
        RefreshDetail();
    }

    /// <summary>The reading's storage status, else the per-drive sensor notes, else the storage part of the hardware status.</summary>
    internal static string StatusText(Reading? reading)
    {
        if(reading is null)return "";
        if(!string.IsNullOrWhiteSpace(reading.Storage.Status))return reading.Storage.Status;
        var notes=reading.Storage.Health.Select(h=>h.Unavailable).OfType<string>().Where(n=>n.Length>0).Distinct().ToArray();
        if(notes.Length>0)return string.Join(" · ",notes);
        return string.Join(" · ",(reading.HardwareStatus??"").Split(['\n',';'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(part=>part.Contains("storage",StringComparison.OrdinalIgnoreCase)));
    }

    private Button Row(StorageDrive drive,StorageHealth? health)
    {
        var lines=new StackPanel();
        string title=$"{drive.Model} · {drive.BusType} · {Ui.Bytes(drive.SizeBytes)}";
        if(drive.IdentityNote.Length>0)title+=$" · {drive.IdentityNote}";
        if(drive.Removable)title+=" · removable";
        var heading=Ui.Text(title,13);heading.FontWeight=FontWeights.SemiBold;heading.TextTrimming=TextTrimming.CharacterEllipsis;lines.Children.Add(heading);
        string life=health?.LifePercent is { } l?$"Life {l.ToString("0",CultureInfo.CurrentCulture)}%":"Life —";
        lines.Children.Add(Ui.Text($"{Ui.Number(health?.Temperature,"°C")} · {(string.IsNullOrEmpty(health?.Badge)?"—":health.Badge)} · {life}",12,health?.Badge=="Failing"?"Warning":"Muted"));
        long size=drive.Volumes.Sum(v=>v.SizeBytes),free=drive.Volumes.Sum(v=>v.FreeBytes);
        var bar=new ProgressBar {Minimum=0,Maximum=1,Value=size>0?(size-free)/(double)size:0,Height=6,Margin=new Thickness(0,6,0,3)};lines.Children.Add(bar);
        lines.Children.Add(Ui.Text(size>0?$"{Ui.Bytes(free)} free of {Ui.Bytes(size)}":"No volumes",11,"Muted"));
        var button=new Button {Content=lines,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,0,6),Tag=drive.Id==SelectedDriveId?"active":null};
        System.Windows.Automation.AutomationProperties.SetName(button,title);
        button.Click+=(_,_)=>{SelectedDriveId=drive.Id;Refresh();};
        return button;
    }

    private void RefreshDetail()
    {
        var drive=Selected;var health=Storage.Health.FirstOrDefault(h=>h.DriveId==SelectedDriveId);
        if(chartDriveId!=SelectedDriveId||chart is null&&drive is not null)
        {
            detailBody.Children.Clear();chart=null;chartDriveId=SelectedDriveId;
            if(drive is not null){chart=new ChartCard(session,"storage",false,false,drive.Id) {Height=260};detailBody.Children.Add(chart);}
        }
        Volumes.Children.Clear();Volumes.ColumnDefinitions.Clear();Volumes.RowDefinitions.Clear();Smart.Children.Clear();
        if(drive is null){Smart.Children.Add(Ui.Text("Select a drive.",12,"Muted"));return;}
        for(int c=0;c<4;c++)Volumes.ColumnDefinitions.Add(new() {Width=new GridLength(c==1?2:1,GridUnitType.Star)});
        void Cell(UIElement element,int row,int column){Grid.SetRow(element,row);Grid.SetColumn(element,column);Volumes.Children.Add(element);}
        Volumes.RowDefinitions.Add(new() {Height=GridLength.Auto});
        string[] headers=["Letter","Label","Used","Free"];
        for(int c=0;c<headers.Length;c++)Cell(Ui.Caption(headers[c]),0,c);
        int r=1;
        foreach(var volume in drive.Volumes)
        {
            Volumes.RowDefinitions.Add(new() {Height=GridLength.Auto});
            string[] cells=[volume.Letter,volume.Label,Ui.Bytes(volume.SizeBytes-volume.FreeBytes),Ui.Bytes(volume.FreeBytes)];
            for(int c=0;c<cells.Length;c++){var text=Ui.Text(cells[c],12);text.Margin=new Thickness(0,4,8,0);Cell(text,r,c);}
            r++;
        }
        if(drive.Volumes.Count==0){Volumes.RowDefinitions.Add(new() {Height=GridLength.Auto});var none=Ui.Text("No mounted volumes",12,"Muted");Grid.SetColumnSpan(none,4);Cell(none,1,0);}
        string Hours(double? v)=>v is { } h?h.ToString("0",CultureInfo.CurrentCulture):"—";
        string Life(double? v)=>v is { } l?l.ToString("0",CultureInfo.CurrentCulture)+"%":"—";
        Smart.Children.Add(Ui.Text($"Power-on hours · {Hours(health?.PowerOnHours)}",12));
        Smart.Children.Add(Ui.Text($"Highest temperature · {Ui.Number(health?.MaxTemperature,"°C")}",12));
        Smart.Children.Add(Ui.Text($"Remaining life · {Life(health?.LifePercent)}",12));
        if(!string.IsNullOrEmpty(health?.Unavailable))Smart.Children.Add(Ui.Text(health.Unavailable,11,"Muted"));
        if(drive.IdentityNote.Length>0)Smart.Children.Add(Ui.Text($"Identity · {drive.IdentityNote} · live only, not kept in history",11,"Muted"));
    }

    private void PopOut()
    {
        if(Selected is not { } drive)return;
        if(ShowWindow is { } show)show(new GraphWindow(session,drive));
        else session.OpenDriveWindow(drive);
    }
}

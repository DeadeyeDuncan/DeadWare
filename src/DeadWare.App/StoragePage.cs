using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>A growing drive chart with responsive drive selectors and lower Volumes / Health tabs.</summary>
internal sealed class StoragePage : UserControl
{
    private readonly UiSession session;
    private readonly Grid body=new(),detailLayout=new(),chartColumn=new(),drivePanel=new();
    private readonly Border lowerCard;
    private readonly TextBlock chartPlaceholder=Ui.Text("Select a drive.",12,"Muted");
    private bool drivesBeside;
    private ChartCard? chart;
    private string? chartDriveId;
    internal StackPanel Rows {get;}=new();
    internal Grid Volumes {get;}=new() {Margin=new Thickness(0,10,0,0)};
    internal StackPanel Smart {get;}=new() {Margin=new Thickness(0,12,0,0)};
    internal Border Detail {get;}
    internal TabControl Tabs {get;}=new();
    internal TabItem VolumesTab {get;}=new() {Header="Volumes"};
    internal TabItem HealthTab {get;}=new() {Header="Health"};
    internal TextBlock Status {get;}=Ui.Text("",11,"Muted");
    internal string? SelectedDriveId {get;private set;}
    /// <summary>Shows a drive's pop-out; tests replace it to inspect the window instead of opening it.</summary>
    internal Action<Window>? ShowWindow {get;set;}

    public StoragePage(UiSession session)
    {
        this.session=session;
        Margin=new(6,0,6,6);
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})body.RowDefinitions.Add(new() {Height=height});
        body.RowDefinitions[2].MinHeight=370;
        Status.TextWrapping=TextWrapping.Wrap;Status.Margin=new Thickness(4,0,4,10);body.Children.Add(Status);
        drivePanel.RowDefinitions.Add(new() {Height=GridLength.Auto});drivePanel.RowDefinitions.Add(new() {Height=GridLength.Auto});
        drivePanel.Children.Add(Ui.Caption("Drives"));
        var driveScroll=new ScrollViewer {Content=Rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        // A wheel the drive list cannot use scrolls the page instead; a ScrollViewer otherwise swallows it even with nothing to scroll.
        driveScroll.PreviewMouseWheel+=(_,e)=>
        {
            if(e.Handled||e.Delta==0)return;
            bool canScroll=e.Delta<0?driveScroll.VerticalOffset+.01<driveScroll.ScrollableHeight:driveScroll.VerticalOffset>.01;
            if(canScroll||Content is not ScrollViewer page)return;
            e.Handled=true;page.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice,e.Timestamp,e.Delta) {RoutedEvent=MouseWheelEvent});
        };
        Grid.SetRow(driveScroll,1);drivePanel.Children.Add(driveScroll);Grid.SetRow(drivePanel,1);body.Children.Add(drivePanel);
        var header=new DockPanel {Margin=new Thickness(0,0,0,4)};
        var open=Ui.Button("Open in window ↗",PopOut);DockPanel.SetDock(open,Dock.Right);header.Children.Add(open);
        header.Children.Add(Ui.Heading("Drive detail",14));
        chartColumn.RowDefinitions.Add(new() {Height=GridLength.Auto});chartColumn.RowDefinitions.Add(new() {Height=new(1,GridUnitType.Star)});
        chartColumn.Children.Add(header);Grid.SetRow(chartPlaceholder,1);chartColumn.Children.Add(chartPlaceholder);
        detailLayout.RowDefinitions.Add(new() {Height=new(1,GridUnitType.Star)});
        detailLayout.ColumnDefinitions.Add(new() {Width=new(1,GridUnitType.Star)});detailLayout.ColumnDefinitions.Add(new() {Width=new(0)});detailLayout.Children.Add(chartColumn);
        Detail=Ui.Panel(detailLayout,new Thickness(16,12,16,14));Detail.Margin=new Thickness(0,10,0,0);Detail.MinHeight=360;Grid.SetRow(Detail,2);body.Children.Add(Detail);
        VolumesTab.Content=Volumes;HealthTab.Content=Smart;Tabs.Items.Add(VolumesTab);Tabs.Items.Add(HealthTab);Tabs.SelectedItem=VolumesTab;
        lowerCard=Ui.Panel(Tabs,new Thickness(16,12,16,14));lowerCard.Margin=new(0,10,0,0);Grid.SetRow(lowerCard,3);body.Children.Add(lowerCard);
        Content=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        Status.SizeChanged+=(_,_)=>InvalidateMeasure();drivePanel.SizeChanged+=(_,_)=>{if(!drivesBeside)InvalidateMeasure();};lowerCard.SizeChanged+=(_,_)=>InvalidateMeasure();
        Tabs.SelectionChanged+=(_,e)=>{if(e.OriginalSource!=Tabs)return;InvalidateMeasure();};
        SizeChanged+=(_,e)=>UpdateDrivePanelPlacement(e.NewSize.Width);
        Loaded+=(_,_)=>{session.Changed+=OnChanged;Refresh();};
        Unloaded+=(_,_)=>session.Changed-=OnChanged;
        Refresh();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width=double.IsFinite(availableSize.Width)?Math.Max(1,availableSize.Width-SystemParameters.VerticalScrollBarWidth):1000;
        var measure=new Size(width,double.PositiveInfinity);
        Status.Measure(measure);lowerCard.Measure(measure);
        if(!drivesBeside)drivePanel.Measure(measure);
        double minimum=Status.DesiredSize.Height+(drivesBeside?0:drivePanel.DesiredSize.Height)+lowerCard.DesiredSize.Height+370;
        body.Height=Math.Max(minimum,double.IsFinite(availableSize.Height)?availableSize.Height:minimum);
        return base.MeasureOverride(availableSize);
    }

    private void UpdateDrivePanelPlacement(double pageWidth)
    {
        bool beside=pageWidth>=1100;
        if(drivesBeside==beside)return;
        ((Panel)drivePanel.Parent).Children.Remove(drivePanel);
        drivePanel.ClearValue(Grid.RowProperty);drivePanel.ClearValue(Grid.ColumnProperty);
        drivePanel.RowDefinitions[1].Height=beside?new(1,GridUnitType.Star):GridLength.Auto;
        drivePanel.Margin=beside?new(8,0,0,0):new(0);
        detailLayout.ColumnDefinitions[1].Width=new(beside?280:0);
        if(beside){Grid.SetColumn(drivePanel,1);detailLayout.Children.Add(drivePanel);}
        else {Grid.SetRow(drivePanel,1);body.Children.Insert(body.Children.IndexOf(Detail),drivePanel);}
        foreach(var row in rows.Values)ApplyRowDensity(row,beside);
        drivesBeside=beside;InvalidateMeasure();
    }

    private void OnChanged()
    {
        if(Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.InvokeAsync(OnChanged);return;}
        if(IsVisible)Refresh();
    }

    private StorageReading Storage=>session.Current?.Storage??StorageReading.Empty;
    private StorageDrive? Selected=>Storage.Drives.FirstOrDefault(d=>d.Id==SelectedDriveId);

    private sealed record RowParts(Button Button,TextBlock Title,TextBlock Health,ProgressBar Bar,TextBlock Free);
    private readonly Dictionary<string,RowParts> rows=new(StringComparer.Ordinal);
    private readonly TextBlock emptyRows=Ui.Text("",12,"Muted");

    private void Refresh()
    {
        var storage=Storage;
        if(SelectedDriveId is null||storage.Drives.All(d=>d.Id!=SelectedDriveId))SelectedDriveId=storage.Drives.OrderBy(d=>d.Index).FirstOrDefault()?.Id;
        Status.Text=StatusText(session.Current);Status.Visibility=Status.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        // Rows are kept per drive id and updated in place, so keyboard focus survives each poll.
        var ordered=storage.Drives.OrderBy(d=>d.Index).ToArray();
        foreach(var gone in rows.Keys.Except(ordered.Select(d=>d.Id)).ToArray()){Rows.Children.Remove(rows[gone].Button);rows.Remove(gone);}
        emptyRows.Text=session.Current is null?"Discovering drives…":"No drives detected";
        if(ordered.Length==0){if(!Rows.Children.Contains(emptyRows))Rows.Children.Add(emptyRows);}
        else Rows.Children.Remove(emptyRows);
        for(int i=0;i<ordered.Length;i++)
        {
            var drive=ordered[i];
            if(!rows.TryGetValue(drive.Id,out var parts)){parts=CreateRow(drive.Id);rows[drive.Id]=parts;}
            int at=Rows.Children.IndexOf(parts.Button);
            if(at!=i){if(at>=0)Rows.Children.RemoveAt(at);Rows.Children.Insert(i,parts.Button);}
            UpdateRow(parts,drive,storage.Health.FirstOrDefault(h=>h.DriveId==drive.Id));
        }
        RefreshDetail();
        InvalidateMeasure();
    }

    /// <summary>The reading's storage status, else the per-drive sensor notes, else the storage part of the hardware status.</summary>
    internal static string StatusText(Reading? reading)
    {
        if(reading is null)return "";
        if(!string.IsNullOrWhiteSpace(reading.Storage.Status))return reading.Storage.Status;
        var notes=reading.Storage.Health.Select(h=>h.Unavailable).OfType<string>().Where(n=>n.Length>0).Distinct().ToArray();
        if(notes.Length>0)return string.Join(" · ",notes);
        return string.Join(" · ",(reading.HardwareStatus??"").Split(" · ",StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(part=>part.Contains("drive",StringComparison.OrdinalIgnoreCase)||part.Contains("disk",StringComparison.OrdinalIgnoreCase)||part.Contains("storage",StringComparison.OrdinalIgnoreCase)));
    }

    private RowParts CreateRow(string id)
    {
        var lines=new StackPanel();
        var title=Ui.Text("",13);title.FontWeight=FontWeights.SemiBold;title.TextTrimming=TextTrimming.CharacterEllipsis;lines.Children.Add(title);
        var health=Ui.Text("",12,"Muted");lines.Children.Add(health);
        var bar=new ProgressBar {Minimum=0,Maximum=1,Height=6,Margin=new Thickness(0,6,0,3)};lines.Children.Add(bar);
        var free=Ui.Text("",11,"Muted");lines.Children.Add(free);
        var button=new Button {Content=lines,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        button.Click+=(_,_)=>{SelectedDriveId=id;Refresh();};
        var row=new RowParts(button,title,health,bar,free);ApplyRowDensity(row,drivesBeside);return row;
    }

    private static void ApplyRowDensity(RowParts row,bool compact)
    {
        row.Button.Padding=compact?new(10,6,10,6):new(12,8,12,8);
        row.Button.Margin=new(0,0,0,compact?4:6);
    }

    private void UpdateRow(RowParts row,StorageDrive drive,StorageHealth? health)
    {
        string title=$"{drive.Model} · {drive.BusType} · {Ui.Bytes(drive.SizeBytes)}";
        if(drive.IdentityNote.Length>0)title+=$" · {drive.IdentityNote}";
        if(drive.Removable)title+=" · removable";
        row.Title.Text=title;System.Windows.Automation.AutomationProperties.SetName(row.Button,title);
        string life=health?.LifePercent is { } l?$"Life {l.ToString("0",CultureInfo.CurrentCulture)}%":"Life —";
        row.Health.Text=$"{Ui.Number(health?.Temperature,"°C")} · {(string.IsNullOrEmpty(health?.Badge)?"—":health.Badge)} · {life}";
        row.Health.SetResourceReference(TextBlock.ForegroundProperty,health?.Badge=="Failing"?"Warning":"Muted");
        long size=drive.Volumes.Sum(v=>v.SizeBytes),free=drive.Volumes.Sum(v=>v.FreeBytes);
        row.Bar.Value=size>0?(size-free)/(double)size:0;
        row.Free.Text=size>0?$"{Ui.Bytes(free)} free of {Ui.Bytes(size)}":"No volumes";
        // The selected row carries the accent border and soft accent fill, like the active rail button.
        bool selected=drive.Id==SelectedDriveId;
        row.Button.Tag=selected?"active":null;
        if(selected)
        {
            row.Button.SetResourceReference(BorderBrushProperty,"Accent");row.Button.SetResourceReference(BackgroundProperty,"AccentSoft");
            row.Button.BorderThickness=new Thickness(3,1,1,1);
        }
        else {row.Button.ClearValue(BorderBrushProperty);row.Button.ClearValue(BackgroundProperty);row.Button.ClearValue(BorderThicknessProperty);}
    }

    private void RefreshDetail()
    {
        var drive=Selected;var health=Storage.Health.FirstOrDefault(h=>h.DriveId==SelectedDriveId);
        chartPlaceholder.Visibility=drive is null?Visibility.Visible:Visibility.Collapsed;
        if(chartDriveId!=SelectedDriveId||chart is null&&drive is not null)
        {
            if(chart is not null)chartColumn.Children.Remove(chart);
            chart=null;chartDriveId=SelectedDriveId;
            if(drive is not null){chart=new ChartCard(session,"storage",false,false,drive.Id);Grid.SetRow(chart,1);chartColumn.Children.Add(chart);}
        }
        Volumes.Children.Clear();Volumes.ColumnDefinitions.Clear();Volumes.RowDefinitions.Clear();Smart.Children.Clear();
        if(drive is null)return;
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
        if(!string.IsNullOrEmpty(health?.Unavailable)&&!Status.Text.Contains(health.Unavailable))Smart.Children.Add(Ui.Text(health.Unavailable,11,"Muted"));
        if(drive.IdentityNote.Length>0)Smart.Children.Add(Ui.Text($"Identity · {drive.IdentityNote} · live only, not kept in history",11,"Muted"));
    }

    private void PopOut()
    {
        if(Selected is not { } drive)return;
        if(ShowWindow is { } show)show(new GraphWindow(session,drive));
        else session.OpenDriveWindow(drive);
    }
}

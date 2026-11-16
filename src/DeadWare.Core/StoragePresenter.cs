using System.Globalization;
namespace DeadWare.Core;

/// <summary>The Storage page's wording, with no user-interface framework: what each drive's lines and tables say.</summary>
public static class StoragePresenter
{
    /// <summary>
    /// A drive as the app names it wherever it shows one: its volumes' letters, then its model ("C: Samsung 990",
    /// "D: E: WD Game Drive"), because people know their drives by letter. The letters keep the volumes' order, which
    /// WindowsStorage sorts; a volume with no letter is left out, and a drive with no lettered volume is its model alone.
    /// The model drops the ending Windows gives a drive behind a bridge or on USB ("WD P40 Game Drive SCSI Disk Device"),
    /// which names the connection rather than the drive and would crowd the name out of a card's header.
    /// </summary>
    public static string Name(StorageDrive drive)=>
        string.Join(" ",drive.Volumes.Select(v=>v.Letter).Where(letter=>!string.IsNullOrWhiteSpace(letter)).Select(letter=>letter+":").Append(ShortModel(drive.Model)));

    // The generic endings Windows appends to a bridged, USB or ATA drive's model.
    private static readonly string[] GenericEndings=[" SCSI Disk Device"," USB Device"," ATA Device"];

    /// <summary>The model without a generic ending, matched at the very end in any case; a model that is nothing but the ending is kept whole.</summary>
    private static string ShortModel(string model)
    {
        foreach(string ending in GenericEndings)
        {
            if(!model.EndsWith(ending,StringComparison.OrdinalIgnoreCase))continue;
            string rest=model[..^ending.Length].TrimEnd();
            return rest.Length>0?rest:model;
        }
        return model;
    }

    /// <summary>Name, bus and size, then why the drive's identity is weak and whether it is removable.</summary>
    public static string Title(StorageDrive drive)
    {
        string title=$"{Name(drive)} · {drive.BusType} · {HistoryChartModel.Bytes(drive.SizeBytes)}";
        if(drive.IdentityNote.Length>0)title+=$" · {drive.IdentityNote}";
        if(drive.Removable)title+=" · removable";
        return title;
    }

    /// <summary>Temperature, health badge and remaining life, each a dash when unknown.</summary>
    public static string HealthLine(StorageHealth? health)
    {
        string life=health?.LifePercent is { } l?$"Life {l.ToString("0",CultureInfo.CurrentCulture)}%":"Life —";
        return $"{TableSync.Number(health?.Temperature,"°C")} · {(string.IsNullOrEmpty(health?.Badge)?"—":health.Badge)} · {life}";
    }

    public static bool Failing(StorageHealth? health)=>health?.Badge=="Failing";

    /// <summary>Free space across the drive's volumes, and the used share for a bar; "No volumes" when none are mounted.</summary>
    public static (string Text,double UsedFraction) Free(StorageDrive drive)
    {
        long size=drive.Volumes.Sum(v=>v.SizeBytes),free=drive.Volumes.Sum(v=>v.FreeBytes);
        return size>0?($"{HistoryChartModel.Bytes(free)} free of {HistoryChartModel.Bytes(size)}",(size-free)/(double)size):("No volumes",0);
    }

    /// <summary>One row per volume: letter, label, used and free.</summary>
    public static IReadOnlyList<TableRow> Volumes(StorageDrive drive)=>drive.Volumes
        .Select(v=>new TableRow(v.Letter,[v.Letter,v.Label,HistoryChartModel.Bytes(v.SizeBytes-v.FreeBytes),HistoryChartModel.Bytes(v.FreeBytes)])).ToList();

    /// <summary>The drive's health details; a note already shown in <paramref name="status"/> is not repeated.</summary>
    public static IReadOnlyList<string> Details(StorageDrive drive,StorageHealth? health,string status)
    {
        string Whole(double? v,string suffix="")=>v is { } value?value.ToString("0",CultureInfo.CurrentCulture)+suffix:"—";
        var lines=new List<string>
        {
            $"Power-on hours · {Whole(health?.PowerOnHours)}",
            $"Highest temperature · {TableSync.Number(health?.MaxTemperature,"°C")}",
            $"Remaining life · {Whole(health?.LifePercent,"%")}",
        };
        if(!string.IsNullOrEmpty(health?.Unavailable)&&!status.Contains(health.Unavailable))lines.Add(health.Unavailable);
        if(drive.IdentityNote.Length>0)lines.Add($"Identity · {drive.IdentityNote} · live only, not kept in history");
        return lines;
    }

    /// <summary>The page's status line: the storage status, else the drives' health notes, else the storage parts of the hardware status.</summary>
    public static string Status(string? storageStatus,IReadOnlyList<StorageHealth> health,string? hardwareStatus)
    {
        if(!string.IsNullOrWhiteSpace(storageStatus))return storageStatus;
        var notes=health.Select(h=>h.Unavailable).OfType<string>().Where(n=>n.Length>0).Distinct().ToArray();
        if(notes.Length>0)return string.Join(" · ",notes);
        return string.Join(" · ",(hardwareStatus??"").Split(" · ",StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Where(part=>part.Contains("drive",StringComparison.OrdinalIgnoreCase)||part.Contains("disk",StringComparison.OrdinalIgnoreCase)||part.Contains("storage",StringComparison.OrdinalIgnoreCase)));
    }
}

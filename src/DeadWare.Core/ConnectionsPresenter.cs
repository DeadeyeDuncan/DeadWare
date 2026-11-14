using System.Globalization;
using System.Net;
using System.Text.Json;

namespace DeadWare.Core;

/// <summary>What a Destinations row's cells depend on besides its destination: the mode, the Live view's capture state, whether a
/// location database is installed and, in Live, which apps are in Don't record these apps (null marks none, as History rows pass).</summary>
public sealed record DestinationCellContext(bool History, bool Available, bool Complete, bool HasLocationDatabase, Func<string, bool>? IsExcluded = null);

/// <summary>One Destinations row's shown texts and tooltips. Every "—" has a tooltip naming its reason.</summary>
public sealed record DestinationCells(string Destination, string Class, string Location, string Apps, string Ports, string Download, string Upload,
    string Received, string Sent, string Connections, string Minutes, string FirstSeen, string LastSeen,
    string? DestinationTip, string? ClassTip, string? LocationTip, string? AppsTip, string? PortsTip, string? DownloadTip, string? UploadTip, string Spoken)
{
    private const string Dash = "—";

    /// <summary>The shown text of one column's cell, as Copy row copies it.</summary>
    public string Cell(ConnectionDestinationColumn column) => column switch
    {
        ConnectionDestinationColumn.Destination => Destination,
        ConnectionDestinationColumn.Class => Class,
        ConnectionDestinationColumn.Location => Location,
        ConnectionDestinationColumn.Apps => Apps,
        ConnectionDestinationColumn.Ports => Ports,
        ConnectionDestinationColumn.Download => Download,
        ConnectionDestinationColumn.Upload => Upload,
        ConnectionDestinationColumn.Received => Received,
        ConnectionDestinationColumn.Sent => Sent,
        ConnectionDestinationColumn.Connections => Connections,
        ConnectionDestinationColumn.Minutes => Minutes,
        ConnectionDestinationColumn.FirstSeen => FirstSeen,
        ConnectionDestinationColumn.LastSeen => LastSeen,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    /// <summary>One column's tooltip, or null.</summary>
    public string? Tip(ConnectionDestinationColumn column) => column switch
    {
        ConnectionDestinationColumn.Destination => DestinationTip,
        ConnectionDestinationColumn.Class => ClassTip,
        ConnectionDestinationColumn.Location => LocationTip,
        ConnectionDestinationColumn.Apps => AppsTip,
        ConnectionDestinationColumn.Ports => PortsTip,
        ConnectionDestinationColumn.Download => DownloadTip,
        ConnectionDestinationColumn.Upload => UploadTip,
        _ => null,
    };

    public static DestinationCells Build(ConnectionDestination d, DestinationCellContext c)
    {
        var kind = d.Class.Kind;
        bool incomplete = c.History ? d.Complete == false : !c.Complete;
        var destinationTip = new List<string> { d.Address };
        if (!c.History) destinationTip.Add(DestinationCopy.ObservedSince(d.FirstSeen));
        if (incomplete) destinationTip.Add(c.History ? DestinationCopy.HistoryIncomplete : DestinationCopy.LiveIncomplete);
        string location; string? locationTip;
        switch (kind)
        {
            case NetworkAddressKind.Local: location = Dash; locationTip = DestinationCopy.LocalNotLocated; break;
            case NetworkAddressKind.Special: location = Dash; locationTip = DestinationCopy.SpecialNotLocated; break;
            default:
                if (d.Location is { } at)
                {
                    location = string.Join(", ", new[] { at.City, at.Region, at.Country }.Where(s => !string.IsNullOrEmpty(s)));
                    locationTip = d.Points.Count == 0 ? DestinationCopy.CountryOnly : d.Unlocated ? DestinationCopy.SomeMinutesUnlocated : null;
                }
                else
                {
                    location = Dash;
                    locationTip = c.History ? DestinationCopy.NoSavedLocation : c.HasLocationDatabase ? DestinationCopy.NotInDatabase : DestinationCopy.NoDatabase;
                }
                break;
        }
        // An app in Don't record these apps carries the " · not recorded" suffix after its name, in Live only.
        bool Excluded(string name) => !c.History && c.IsExcluded is { } excluded && excluded(name);
        bool busiestExcluded = d.Apps.Count > 0 && Excluded(d.Apps[0].Name);
        string apps = d.Apps.Count == 0 ? "" : d.Apps[0].Name + (busiestExcluded ? ProcessCopy.NotRecordedSuffix : "") + More(d.AppCount);
        string? appsTip = d.Apps.Count == 0 ? null : Lines(d.Apps.Select(a => Line(Excluded(a.Name) ? a.Name + ProcessCopy.NotRecordedSuffix : a.Name, a.ReceivedBytes, a.SentBytes)), d.AppCount - d.Apps.Count);
        string ports = d.Ports.Count == 0 ? "" : Port(d.Ports[0]) + More(d.PortCount);
        string? portsTip = d.Ports.Count == 0 ? null : Lines(d.Ports.Select(p => Line(Port(p), p.ReceivedBytes, p.SentBytes)), d.PortCount - d.Ports.Count);
        string download = c.History ? "" : HistoryChartModel.Bytes(d.DownloadBytesPerSecond, true), upload = c.History ? "" : HistoryChartModel.Bytes(d.UploadBytesPerSecond, true);
        string? downloadTip = !c.History && d.DownloadBytesPerSecond is null ? DestinationCopy.RateUnavailable : null;
        string? uploadTip = !c.History && d.UploadBytesPerSecond is null ? DestinationCopy.RateUnavailable : null;
        string received = HistoryChartModel.Bytes(d.ReceivedBytes), sent = HistoryChartModel.Bytes(d.SentBytes);
        string connections = d.Connections?.ToString("N0", CultureInfo.CurrentCulture) ?? "", minutes = d.Minutes?.ToString("N0", CultureInfo.CurrentCulture) ?? "";
        string firstSeen = c.History ? d.FirstSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
        string lastSeen = c.History ? d.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
        var spoken = new List<string> { d.Address, d.Class.Label, location == Dash ? $"location unknown, {locationTip!.TrimEnd('.')}" : location };
        spoken.Add(d.Apps.Count == 0 ? "no apps" : $"{Count(d.AppCount, "app", "apps")}: {d.Apps[0].Name}{(busiestExcluded ? ", " + ProcessCopy.NotRecorded : "")}{SpokenMore(d.AppCount)}");
        spoken.Add(d.Ports.Count == 0 ? "no ports" : $"port {Port(d.Ports[0])}{SpokenMore(d.PortCount)}");
        spoken.Add($"received {received}"); spoken.Add($"sent {sent}");
        if (c.History)
        {
            if (d.Minutes is { } count) spoken.Add(Count(count, "minute", "minutes"));
            spoken.Add($"last seen {lastSeen}");
        }
        else
        {
            spoken.Add($"download {(download == Dash ? "unknown" : download)}"); spoken.Add($"upload {(upload == Dash ? "unknown" : upload)}");
            if (d.Connections is { } count) spoken.Add(Count(count, "connection", "connections"));
        }
        if (incomplete) spoken.Add("incomplete");
        return new(d.Address, d.Class.Label, location, apps, ports, download, upload, received, sent, connections, minutes, firstSeen, lastSeen,
            string.Join("\n", destinationTip), DestinationCopy.ClassTip(kind), locationTip, appsTip, portsTip, downloadTip, uploadTip, string.Join(", ", spoken));
    }

    /// <summary>The column headers, with each column's share of the width.</summary>
    public static (string Header, double Weight, bool Value) Column(ConnectionDestinationColumn column) => column switch
    {
        ConnectionDestinationColumn.Destination => ("DESTINATION", 1.5, false),
        ConnectionDestinationColumn.Class => ("CLASS", 1.1, false),
        ConnectionDestinationColumn.Location => ("LOCATION", 1.6, false),
        ConnectionDestinationColumn.Apps => ("APPS", 1.3, false),
        ConnectionDestinationColumn.Ports => ("PORTS", 1, false),
        ConnectionDestinationColumn.Download => ("↓ DOWNLOAD", .8, true),
        ConnectionDestinationColumn.Upload => ("↑ UPLOAD", .8, true),
        ConnectionDestinationColumn.Received => ("RECEIVED", .8, true),
        ConnectionDestinationColumn.Sent => ("SENT", .8, true),
        ConnectionDestinationColumn.Connections => ("CONNECTIONS", .8, true),
        ConnectionDestinationColumn.Minutes => ("MINUTES", .8, true),
        ConnectionDestinationColumn.FirstSeen => ("FIRST SEEN", 1.1, true),
        ConnectionDestinationColumn.LastSeen => ("LAST SEEN", 1.1, true),
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    private static string Port(ConnectionDestinationPort port) => $"{port.Port} {port.Protocol}";
    private static string More(int count) => count > 1 ? $" +{count - 1:N0}" : "";
    private static string SpokenMore(int count) => count > 1 ? $" and {count - 1:N0} more" : "";
    private static string Count(long count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";
    private static string Line(string name, long received, long sent) => $"{name} · ↓ {HistoryChartModel.Bytes(received)} · ↑ {HistoryChartModel.Bytes(sent)}";
    private static string Lines(IEnumerable<string> lines, int more) => string.Join("\n", more > 0 ? lines.Append(DestinationCopy.MoreLine(more)) : lines);
}

/// <summary>Texts and keys the Connections page builds from its data, kept apart from any UI framework.</summary>
public static class ConnectionsPresenter
{
    public const string LimitWarning = "This interval holds too many saved rows to show. Choose a shorter interval.";
    public const string UnavailableSummary = "History · minute-bucket average rates · — destinations · — countries · — local · — unlocated · ↓ — received / ↑ — sent";
    public const string ExportUnavailable = "Connection export unavailable.";
    public const string ExportExists = "That file already exists; exports never overwrite. Choose a new file name.";
    public const string ExportDone = "CSV exported to a new file. Saved history is unchanged.";
    public const string IntervalWarning = "Choose valid local times (yyyy-MM-dd HH:mm), from before exclusive to, at most 31 days. Avoid ambiguous or skipped daylight-saving minutes.";
    public const string LocalTimeFormat = "yyyy-MM-dd HH:mm";

    public static string ExportFailed(Exception error) => $"Export failed ({error.GetType().Name}); choose a new writable file.";

    private sealed record HistoryPeerKey(long MinuteUtcTicks, ProcessIdentity Process, string RemoteAddress,
        int RemotePort, ConnectionProtocol Protocol, ConnectionLocation? Location, GeoPoint? Origin);

    /// <summary>A live peer's row key.</summary>
    public static string Key(ConnectionPeer peer) => "live:" + JsonSerializer.Serialize(peer);

    /// <summary>A saved row's key, matching the store's grouping identity; aggregates and first/last observations may change on refresh.</summary>
    public static string Key(ConnectionHistoryRow row) => "history:" + JsonSerializer.Serialize(new HistoryPeerKey(
        row.Minute.UtcTicks, row.Process, row.RemoteAddress, row.RemotePort, row.Protocol, row.Location, row.Origin));

    /// <summary>The key of a grouped saved location that has no representative row.</summary>
    public static string Key(ConnectionHistoryMarker marker) => "saved:" + JsonSerializer.Serialize(new { marker.Location, marker.Origin });

    /// <summary>"address:port protocol", with an IPv6 address in brackets.</summary>
    public static string Endpoint(string address, int port, ConnectionProtocol protocol) =>
        $"{(IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address)}:{port} {protocol}";

    public static NetworkAddressKind Kind(string address) => IPAddress.TryParse(address, out var ip) ? NetworkAddress.Classify(ip) : NetworkAddressKind.Special;

    /// <summary>"City, Region, Country", or why there is none.</summary>
    public static string Location(string address, ConnectionLocation? location) => Kind(address) switch
    {
        NetworkAddressKind.Local => "Local",
        NetworkAddressKind.Special => "Special / unlocated",
        _ => location is null ? "Unlocated" : string.Join(", ", new[] { location.City, location.Region, location.Country }.Where(s => !string.IsNullOrEmpty(s))),
    };

    /// <summary>A count with its noun, the singular for 1.</summary>
    public static string Count(long count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";

    /// <summary>Adds a byte count, ignoring negatives and saturating.</summary>
    public static long Add(long left, long right) => right <= 0 ? left : left > long.MaxValue - right ? long.MaxValue : left + right;

    /// <summary>The origin every marker shares, or null when they differ.</summary>
    public static GeoPoint? CommonOrigin(IReadOnlyList<ConnectionHistoryMarker> markers)
    {
        var origins = markers.Select(m => m.Origin).Distinct().Take(2).ToArray();
        return origins.Length == 1 ? origins[0] : null;
    }

    /// <summary>Parses "yyyy-MM-dd HH:mm" as a local time in the zone; an invalid (skipped) or ambiguous daylight-saving minute fails.</summary>
    public static bool TryParseLocalTime(string text, TimeZoneInfo zone, out DateTimeOffset utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(text.Trim(), LocalTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(parsed) || zone.IsAmbiguousTime(parsed)) return false;
        try { utc = new DateTimeOffset(parsed, zone.GetUtcOffset(parsed)).ToUniversalTime(); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>The from/to box text for [start, end) in the zone; an invalid or ambiguous local start moves earlier and end later, at
    /// most 180 minutes each, so the boxes always hold the interval.</summary>
    public static (string From, string To) LocalBoxes(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        static string Text(DateTime local) => local.ToString(LocalTimeFormat, CultureInfo.InvariantCulture);
        string Widen(DateTimeOffset time, int step)
        {
            var local = TimeZoneInfo.ConvertTime(time, zone).DateTime;
            for (int i = 0; i < 180 && !TryParseLocalTime(Text(local), zone, out _); i++) local = local.AddMinutes(step);
            return Text(local);
        }
        return (Widen(start, -1), Widen(end, 1));
    }

    /// <summary>Two history queries ask for the same interval and filters, whatever their paging.</summary>
    public static bool SameContext(ConnectionHistoryQuery left, ConnectionHistoryQuery? right) => right is not null &&
        left.From == right.From && left.To == right.To && left.Direction == right.Direction &&
        string.Equals(left.Search, right.Search, StringComparison.OrdinalIgnoreCase) && string.Equals(left.CountryCode, right.CountryCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Application, right.Application, StringComparison.Ordinal);

    /// <summary>A view with nothing in it, for a failed or impossible read.</summary>
    public static ConnectionHistoryView Empty(ConnectionHistoryQuery query, string status) => new(query, [], 0, new(0, 0, 0, 0, 0, 0), [], [], [], null, 0, status);
}

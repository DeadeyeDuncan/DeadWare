using System.Net;

namespace DeadWare.Core;

/// <summary>The Destinations table's rows: one per remote address, capped per kind, with exact counts.</summary>
public static class ConnectionDestinations
{
    public const int MaximumRowsPerScope = 1000, MaximumApps = 10, MaximumPorts = 10, MaximumPoints = 8, ExportMaximumApps = 100, ExportMaximumPorts = 1000;

    /// <summary>Groups the filtered live view by remote address. Rates are null when the view is not available.</summary>
    public static ConnectionDestinationList FromLive(IReadOnlyList<LocatedConnection> connections, bool available)
    {
        var rows = connections.GroupBy(c => c.Traffic.Peer.RemoteAddress, StringComparer.Ordinal).Select(g => Row(g.Key, g.ToList(), available)).ToList();
        long internet = 0, local = 0, special = 0, unlocated = 0;
        foreach (var row in rows)
        {
            switch (row.Class.Kind)
            {
                case NetworkAddressKind.Public: internet++; if (row.Unlocated) unlocated++; break;
                case NetworkAddressKind.Local: local++; break;
                default: special++; break;
            }
        }
        var capped = rows.GroupBy(r => r.Class.Kind).SelectMany(kind => Ranked(kind).Take(MaximumRowsPerScope));
        return new(Ranked(capped).ToList(), new(rows.Count, internet, local, special, unlocated));
    }

    /// <summary>All is the top 1,000 overall; Internet lists Public rows and Local lists Local rows. Special rows appear under All only.</summary>
    public static IReadOnlyList<ConnectionDestination> InScope(ConnectionDestinationList list, DestinationScope scope) => scope switch
    {
        DestinationScope.Internet => Ranked(list.Rows.Where(r => r.Class.Kind == NetworkAddressKind.Public)).Take(MaximumRowsPerScope).ToList(),
        DestinationScope.Local => Ranked(list.Rows.Where(r => r.Class.Kind == NetworkAddressKind.Local)).Take(MaximumRowsPerScope).ToList(),
        _ => Ranked(list.Rows).Take(MaximumRowsPerScope).ToList(),
    };

    /// <summary>"Internet" and "Local", compared ordinally; anything else is All.</summary>
    public static DestinationScope ParseScope(string? value) => value switch
    {
        "Internet" => DestinationScope.Internet,
        "Local" => DestinationScope.Local,
        _ => DestinationScope.All,
    };

    /// <summary>The cap's rank: received + sent as double, descending, then the address text, ordinal.</summary>
    private static IEnumerable<ConnectionDestination> Ranked(IEnumerable<ConnectionDestination> rows) =>
        rows.OrderByDescending(r => (double)r.ReceivedBytes + r.SentBytes).ThenBy(r => r.Address, StringComparer.Ordinal);

    private static ConnectionDestination Row(string address, IReadOnlyList<LocatedConnection> peers, bool available)
    {
        var addressClass = NetworkAddress.Describe(address);
        var apps = new Dictionary<string, (string Name, long Received, long Sent)>(StringComparer.Ordinal);
        var ports = new Dictionary<(int Port, ConnectionProtocol Protocol), (long Received, long Sent)>();
        long received = 0, sent = 0;
        double down = 0, up = 0;
        DateTimeOffset first = DateTimeOffset.MaxValue, last = DateTimeOffset.MinValue;
        ConnectionLocation? location = null;
        foreach (var c in peers)
        {
            var t = c.Traffic;
            var key = ProcessGrouping.Key(t.Peer.Process.Name);
            var app = apps.TryGetValue(key, out var a) ? a : (UsageMetrics.NormalizeName(t.Peer.Process.Name), 0L, 0L);
            apps[key] = (app.Item1, Add(app.Item2, t.ReceivedBytes), Add(app.Item3, t.SentBytes));
            var portKey = (t.Peer.RemotePort, t.Peer.Protocol);
            var port = ports.TryGetValue(portKey, out var p) ? p : (0L, 0L);
            ports[portKey] = (Add(port.Item1, t.ReceivedBytes), Add(port.Item2, t.SentBytes));
            received = Add(received, t.ReceivedBytes); sent = Add(sent, t.SentBytes);
            down += t.DownloadBytesPerSecond; up += t.UploadBytesPerSecond;
            if (t.FirstSeen < first) first = t.FirstSeen;
            if (t.LastSeen > last) last = t.LastSeen;
            location ??= c.Location;
        }
        var busiestApps = apps.OrderByDescending(x => Add(x.Value.Received, x.Value.Sent)).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(MaximumApps).Select(x => new ConnectionDestinationApp(x.Value.Name, x.Value.Received, x.Value.Sent)).ToList();
        var busiestPorts = ports.OrderByDescending(x => Add(x.Value.Received, x.Value.Sent)).ThenBy(x => x.Key.Port).ThenBy(x => x.Key.Protocol)
            .Take(MaximumPorts).Select(x => new ConnectionDestinationPort(x.Key.Port, x.Key.Protocol, x.Value.Received, x.Value.Sent)).ToList();
        IReadOnlyList<GeoPoint> points = location?.Point is { } point && ConnectionProjection.IsValid(point) ? [point] : [];
        return new(address, addressClass, location, points, addressClass.Kind == NetworkAddressKind.Public && points.Count == 0,
            busiestApps, apps.Count, busiestPorts, ports.Count, received, sent,
            available ? down : null, available ? up : null, peers.Count, null, first, last, null);
    }

    private static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}

public enum ConnectionDestinationColumn { Destination, Class, Location, Apps, Ports, Download, Upload, Received, Sent, Connections, Minutes, FirstSeen, LastSeen }

/// <summary>The Destinations table's sort keys. "—" and null sort last in both directions; ties go by address text, ordinal.</summary>
public static class ConnectionDestinationSort
{
    private static readonly ConnectionDestinationColumn[] LiveColumns =
    [
        ConnectionDestinationColumn.Destination, ConnectionDestinationColumn.Class, ConnectionDestinationColumn.Location, ConnectionDestinationColumn.Apps,
        ConnectionDestinationColumn.Ports, ConnectionDestinationColumn.Download, ConnectionDestinationColumn.Upload, ConnectionDestinationColumn.Received,
        ConnectionDestinationColumn.Sent, ConnectionDestinationColumn.Connections,
    ];
    private static readonly ConnectionDestinationColumn[] HistoryColumns =
    [
        ConnectionDestinationColumn.Destination, ConnectionDestinationColumn.Class, ConnectionDestinationColumn.Location, ConnectionDestinationColumn.Apps,
        ConnectionDestinationColumn.Ports, ConnectionDestinationColumn.Received, ConnectionDestinationColumn.Sent, ConnectionDestinationColumn.Minutes,
        ConnectionDestinationColumn.FirstSeen, ConnectionDestinationColumn.LastSeen,
    ];

    public static IReadOnlyList<ConnectionDestinationColumn> Columns(bool history) => history ? HistoryColumns : LiveColumns;

    /// <summary>The key when the mode shows its column; otherwise RECEIVED, the default.</summary>
    public static ConnectionDestinationColumn InMode(ConnectionDestinationColumn key, bool history) =>
        Array.IndexOf(history ? HistoryColumns : LiveColumns, key) >= 0 ? key : ConnectionDestinationColumn.Received;

    /// <summary>Number and time columns start descending; the text columns start ascending.</summary>
    public static bool DescendingFirst(ConnectionDestinationColumn key) => key is not (ConnectionDestinationColumn.Destination or ConnectionDestinationColumn.Class
        or ConnectionDestinationColumn.Location or ConnectionDestinationColumn.Apps or ConnectionDestinationColumn.Ports);

    public static int Compare(ConnectionDestination a, ConnectionDestination b, ConnectionDestinationColumn key, bool descending)
    {
        // A missing value ("—" or null) is last in both directions, so it is never flipped.
        bool missingA = Missing(a, key), missingB = Missing(b, key);
        int c = missingA || missingB ? 0 : key switch
        {
            ConnectionDestinationColumn.Destination => CompareAddress(AddressKey(a.Address)!, AddressKey(b.Address)!),
            ConnectionDestinationColumn.Class => CompareClass(a.Class, b.Class),
            ConnectionDestinationColumn.Location => CompareLocation(Shown(a)!, Shown(b)!),
            ConnectionDestinationColumn.Apps => CompareApps(a, b),
            ConnectionDestinationColumn.Ports => ComparePort(a.Ports[0], b.Ports[0]),
            ConnectionDestinationColumn.Download => a.DownloadBytesPerSecond!.Value.CompareTo(b.DownloadBytesPerSecond!.Value),
            ConnectionDestinationColumn.Upload => a.UploadBytesPerSecond!.Value.CompareTo(b.UploadBytesPerSecond!.Value),
            ConnectionDestinationColumn.Received => a.ReceivedBytes.CompareTo(b.ReceivedBytes),
            ConnectionDestinationColumn.Sent => a.SentBytes.CompareTo(b.SentBytes),
            ConnectionDestinationColumn.Connections => a.Connections!.Value.CompareTo(b.Connections!.Value),
            ConnectionDestinationColumn.Minutes => a.Minutes!.Value.CompareTo(b.Minutes!.Value),
            ConnectionDestinationColumn.FirstSeen => a.FirstSeen.CompareTo(b.FirstSeen),
            ConnectionDestinationColumn.LastSeen => a.LastSeen.CompareTo(b.LastSeen),
            _ => 0,
        };
        if (missingA != missingB) return missingA ? 1 : -1;
        if (descending) c = -c;
        return c != 0 ? c : string.CompareOrdinal(a.Address, b.Address);
    }

    private static bool Missing(ConnectionDestination row, ConnectionDestinationColumn key) => key switch
    {
        ConnectionDestinationColumn.Destination => AddressKey(row.Address) is null,
        ConnectionDestinationColumn.Location => Shown(row) is null,
        ConnectionDestinationColumn.Apps => row.Apps.Count == 0,
        ConnectionDestinationColumn.Ports => row.Ports.Count == 0,
        ConnectionDestinationColumn.Download => row.DownloadBytesPerSecond is null,
        ConnectionDestinationColumn.Upload => row.UploadBytesPerSecond is null,
        ConnectionDestinationColumn.Connections => row.Connections is null,
        ConnectionDestinationColumn.Minutes => row.Minutes is null,
        _ => false,
    };

    private static byte[]? AddressKey(string address) =>
        IPAddress.TryParse(address, out var ip) ? NetworkAddress.Normalize(ip).GetAddressBytes() : null;

    // IPv4 (4 bytes) before IPv6 (16 bytes), then the bytes.
    private static int CompareAddress(byte[] x, byte[] y)
    {
        int c = x.Length.CompareTo(y.Length);
        return c != 0 ? c : x.AsSpan().SequenceCompareTo(y);
    }

    private static int CompareClass(NetworkAddressClass x, NetworkAddressClass y)
    {
        int c = x.Kind.CompareTo(y.Kind);
        return c != 0 ? c : string.CompareOrdinal(x.Label, y.Label);
    }

    // The LOCATION cell shows "—" for Local and Special, even when a fixture gives them a location.
    private static ConnectionLocation? Shown(ConnectionDestination row) => row.Class.Kind == NetworkAddressKind.Public ? row.Location : null;

    private static int CompareLocation(ConnectionLocation x, ConnectionLocation y)
    {
        int c = StringComparer.OrdinalIgnoreCase.Compare(x.Country, y.Country);
        if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(x.Region ?? "", y.Region ?? "");
        return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(x.City ?? "", y.City ?? "");
    }

    private static int CompareApps(ConnectionDestination x, ConnectionDestination y)
    {
        int c = StringComparer.OrdinalIgnoreCase.Compare(x.Apps[0].Name, y.Apps[0].Name);
        return c != 0 ? c : y.AppCount.CompareTo(x.AppCount);
    }

    private static int ComparePort(ConnectionDestinationPort x, ConnectionDestinationPort y)
    {
        int c = x.Port.CompareTo(y.Port);
        return c != 0 ? c : x.Protocol.CompareTo(y.Protocol);
    }
}

/// <summary>Every fixed string of the Destinations table, so the exact copy is tested on Linux.</summary>
public static class DestinationCopy
{
    public const string UnavailableCountLine = "— destinations: — Internet (— unlocated), — local, — special-purpose.";
    public const string InvalidInterval = "Choose a valid interval to list saved destinations.";
    public const string Reading = "Reading saved destinations…";
    public const string Unavailable = "Destinations unavailable; no saved observations shown.";
    public const string CountryOnly = "Country or region only: not drawn on the map; counted as unlocated.";
    public const string SomeMinutesUnlocated = "Some saved minutes have no map position; the address also counts as unlocated.";
    public const string NoDatabase = "No location database. Use Download / update locations at the bottom of the page.";
    public const string NotInDatabase = "Not in the location database, or its lookup has not finished.";
    public const string NoSavedLocation = "No location was saved with this address.";
    public const string LocalNotLocated = "Local addresses are not located.";
    public const string SpecialNotLocated = "Special-purpose addresses are not located.";
    public const string LiveIncomplete = "Capture is incomplete; totals may be low.";
    public const string HistoryIncomplete = "Some minutes were captured incompletely; totals may be low.";
    public const string RateUnavailable = "Endpoint capture is stale or unavailable; no current rate.";
    public const string NotAmongDrawn = "Not on the map: its location is not among the 256 drawn.";
    public const string Copied = "Copied to the clipboard.";
    public const string ExportLiveOnly = "Export is available in History.";
    public const string ExportLimit = "This interval holds too many saved rows to export destinations. Choose a shorter interval.";
    private const string FootnoteText = "A destination is one remote address; its numbers add every app and port that reached it under the current filters. Special-purpose addresses (shared CGNAT, multicast, broadcast, documentation and other reserved ranges) are listed under All only and count as neither local nor unlocated. Locations are approximate.";

    /// <summary>Every count whatever the scope; continues with the cap note when the scope's own count is over 1,000.</summary>
    public static string CountLine(ConnectionDestinationCounts counts, DestinationScope scope, bool history)
    {
        string line = $"{counts.All:N0} {(counts.All == 1 ? "destination" : "destinations")}: {counts.Internet:N0} Internet ({counts.Unlocated:N0} unlocated), {counts.Local:N0} local, {counts.Special:N0} special-purpose.";
        long inScope = scope switch { DestinationScope.Internet => counts.Internet, DestinationScope.Local => counts.Local, _ => counts.All };
        if (inScope <= ConnectionDestinations.MaximumRowsPerScope) return line;
        return line + (history
            ? " Showing the top 1,000 by traffic (received + sent); search or narrow the interval to see the rest."
            : " Showing the top 1,000 by traffic (received + sent); search to see the rest.");
    }

    public static string Footnote(bool history) => FootnoteText + (history
        ? " History lists saved minutes; a minute appears a few seconds after it ends."
        : " Live rows cover peers active in the last 60 s.");

    /// <summary>The Live empty text, for a table with no rows. <paramref name="anyConnections"/>: the view has connections.
    /// <paramref name="scope"/>: the scope that emptied the table, or All when the filtered connections are themselves empty.</summary>
    public static string LiveEmpty(bool available, bool anyConnections, bool filtersSet, DestinationScope scope) => scope switch
    {
        _ when !anyConnections => available
            ? "No destinations yet. Apps that send or receive traffic appear here."
            : "No destinations: per-app capture is not running. The capture status above says why.",
        DestinationScope.Internet => "No Internet destinations right now.",
        DestinationScope.Local => "No local destinations right now.",
        _ => filtersSet ? "No destinations match the filters." : "No destinations yet. Apps that send or receive traffic appear here.",
    };

    /// <summary>The History empty text for a read that returned a list with no rows in the table. <paramref name="scope"/>: the scope that
    /// emptied the table, or All when the list itself has no rows. The invalid, reading and unavailable states are the constants.</summary>
    public static string HistoryEmpty(bool minute, bool filtersSet, DestinationScope scope)
    {
        string span = minute ? "minute" : "interval";
        return scope switch
        {
            DestinationScope.Internet => $"No saved Internet destinations in this {span}.",
            DestinationScope.Local => $"No saved local destinations in this {span}.",
            _ => filtersSet ? $"No saved destinations match the filters in this {span}." : $"No saved destinations in this {span}.",
        };
    }

    public static string ClassTip(NetworkAddressKind kind) => kind switch
    {
        NetworkAddressKind.Public => "Public Internet address.",
        NetworkAddressKind.Local => "Local address: counted under Local.",
        _ => "Special-purpose address: listed under All only and counted as neither local nor unlocated.",
    };

    /// <summary>The RECEIVED, SENT, CONNECTIONS and MINUTES header tooltips for the mode; null for the other columns and for a column the mode lacks.</summary>
    public static string? HeaderTip(ConnectionDestinationColumn column, bool history) => column switch
    {
        ConnectionDestinationColumn.Received or ConnectionDestinationColumn.Sent => history ? "Saved in the shown interval." : "Since each flow was first observed.",
        ConnectionDestinationColumn.Connections when !history => "Observed flows to this address: one per app process, local address and port, remote port and protocol. A flow idle for 60 s drops out.",
        ConnectionDestinationColumn.Minutes when history => "Saved minutes with traffic to this address in the shown interval.",
        _ => null,
    };

    public static string ObservedSince(DateTimeOffset first) => $"Observed since {first.ToLocalTime():g}.";

    public static string MoreLine(int n) => $"and {n:N0} more";

    public static string Highlighted(int nodes) => nodes == 1 ? "Highlighted 1 map node." : $"Highlighted {nodes:N0} map nodes.";

    public static string NotOnMap(NetworkAddressKind kind) => kind switch
    {
        NetworkAddressKind.Local => "Not on the map: local addresses are not located.",
        NetworkAddressKind.Special => "Not on the map: special-purpose addresses are not located.",
        _ => "Not on the map: no map position for this address.",
    };

    public static string CopyFailed(string exceptionType) => $"Copy failed ({exceptionType}); try again.";
}

namespace DeadWare.Core;

public enum ConnectionProtocol { Tcp, Udp }
public enum ConnectionDirection { Both, Received, Sent }
public enum NetworkAddressKind { Public, Local, Special }
public sealed record GeoPoint(double Latitude, double Longitude);
public sealed record ConnectionLocation(string CountryCode, string Country,
    string? Region, string? City, GeoPoint? Point, string Source);
public sealed record ConnectionPeer(ProcessIdentity Process, string LocalAddress,
    int LocalPort, string RemoteAddress, int RemotePort, ConnectionProtocol Protocol);
public sealed record ConnectionDelta(DateTimeOffset Minute, ConnectionPeer Peer,
    long ReceivedBytes, long SentBytes, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);
public sealed record ConnectionTraffic(ConnectionPeer Peer, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, long ReceivedBytes, long SentBytes,
    double DownloadBytesPerSecond, double UploadBytesPerSecond);
public sealed record ConnectionSample(DateTimeOffset Timestamp, double MonotonicSeconds,
    long Sequence, string Segment, IReadOnlyList<ConnectionTraffic> Live,
    IReadOnlyList<ConnectionDelta> Deltas, DateTimeOffset? CoverageFrom,
    bool Available, bool Complete, long DroppedEvents, long LostEvents,
    long UnattributedEvents, string Status);
public sealed record LocatedConnection(ConnectionTraffic Traffic, ConnectionLocation? Location);
public sealed record ConnectionLiveView(DateTimeOffset Timestamp,
    IReadOnlyList<LocatedConnection> Connections, GeoPoint? Origin,
    bool Available, bool Complete, string Status);
public sealed record ConnectionHistoryRecord(string BatchId, string Segment,
    ConnectionDelta Delta, ConnectionLocation? Location, GeoPoint? Origin, bool Complete);
public sealed record ConnectionCoverage(string BatchId, string Segment,
    DateTimeOffset From, DateTimeOffset To, bool Complete);
public sealed record ConnectionHistoryQuery(DateTimeOffset From, DateTimeOffset To,
    string Search = "", string CountryCode = "", ConnectionDirection Direction = ConnectionDirection.Both,
    int Offset = 0, int Limit = 200);

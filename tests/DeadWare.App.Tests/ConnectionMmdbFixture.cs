using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DeadWare.App.Tests;

// Original synthetic fixture encoder, licensed with DeadWare. No real IP/location data.
// One IPv6 tree node points both halves at a deliberately fictional city record.
internal static class ConnectionMmdbFixture
{
    internal static Dictionary<string, object> Record(double latitude = 12.5, double longitude = -45.25,
        string city = "Example City") => new()
    {
        ["country"] = new Dictionary<string, object> { ["iso_code"] = "ZZ", ["names"] = new Dictionary<string, object> { ["en"] = "Example Country" } },
        ["city"] = new Dictionary<string, object> { ["names"] = new Dictionary<string, object> { ["en"] = city } },
        ["subdivisions"] = new object[] { new Dictionary<string, object> { ["names"] = new Dictionary<string, object> { ["en"] = "Example Region" } } },
        ["location"] = new Dictionary<string, object> { ["latitude"] = latitude, ["longitude"] = longitude }
    };

    internal static byte[] Create(string type = "DBIP-City-Lite", Dictionary<string, object>? record = null)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0, 0, 17, 0, 0, 17 });
        stream.Write(new byte[16]);
        Encode(stream, record ?? Record());
        stream.Write(new byte[] { 0xab, 0xcd, 0xef });
        stream.Write(Encoding.ASCII.GetBytes("MaxMind.com"));
        Encode(stream, new Dictionary<string, object>
        {
            ["binary_format_major_version"] = (ushort)2, ["binary_format_minor_version"] = (ushort)0,
            ["build_epoch"] = 1704067200UL, ["database_type"] = type,
            ["description"] = new Dictionary<string, object> { ["en"] = "Synthetic DeadWare test data" },
            ["ip_version"] = (ushort)6, ["languages"] = new object[] { "en" },
            ["node_count"] = 1U, ["record_size"] = (ushort)24
        });
        return stream.ToArray();
    }

    internal static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(data);
        return output.ToArray();
    }

    private static void Header(Stream stream, int type, int size)
    {
        int code = size < 29 ? size : 29;
        stream.WriteByte((byte)((type < 8 ? type : 0) << 5 | code));
        if (type >= 8) stream.WriteByte((byte)(type - 7));
        if (code == 29) stream.WriteByte(checked((byte)(size - 29)));
    }

    private static void Encode(Stream stream, object value)
    {
        Span<byte> number = stackalloc byte[8];
        switch (value)
        {
            case string text:
                byte[] utf8 = Encoding.UTF8.GetBytes(text); Header(stream, 2, utf8.Length); stream.Write(utf8); break;
            case Dictionary<string, object> map:
                Header(stream, 7, map.Count); foreach (var pair in map) { Encode(stream, pair.Key); Encode(stream, pair.Value); } break;
            case object[] array:
                Header(stream, 11, array.Length); foreach (object item in array) Encode(stream, item); break;
            case double d:
                Header(stream, 3, 8); BinaryPrimitives.WriteInt64BigEndian(number, BitConverter.DoubleToInt64Bits(d)); stream.Write(number); break;
            case ushort u:
                Header(stream, 5, 2); BinaryPrimitives.WriteUInt16BigEndian(number, u); stream.Write(number[..2]); break;
            case uint u:
                Header(stream, 6, 4); BinaryPrimitives.WriteUInt32BigEndian(number, u); stream.Write(number[..4]); break;
            case ulong u:
                Header(stream, 9, 8); BinaryPrimitives.WriteUInt64BigEndian(number, u); stream.Write(number); break;
            default: throw new ArgumentException("Unsupported fixture value");
        }
    }
}

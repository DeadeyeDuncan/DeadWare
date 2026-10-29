using System.IO;
using System.Net;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public sealed class ConnectionGeolocationTests
{
    [Fact]
    public void Loads_real_mmdb_on_reload_and_resolves_ipv4_and_ipv6()
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create());
        using var resolver = new ConnectionGeolocation(file.Path);
        Assert.Null(resolver.DatabaseDate);
        resolver.Reload();
        foreach (string address in new[] { "8.8.8.8", "2606:4700:4700::1111" })
        {
            var location = Assert.IsType<DeadWare.Core.ConnectionLocation>(resolver.Lookup(address));
            Assert.Equal("ZZ", location.CountryCode); Assert.Equal("Example Region", location.Region);
            Assert.Equal(12.5, location.Point!.Latitude); Assert.Equal(-45.25, location.Point.Longitude);
        }
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), resolver.DatabaseDate);
        Assert.Contains("outdated", resolver.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.1.2.3")] [InlineData("192.0.2.1")]
    [InlineData("::1")] [InlineData("fe80::1")] [InlineData("2001:db8::1")]
    [InlineData("bad")] [InlineData("")]
    public void Non_public_and_malformed_addresses_never_reach_reader(string address)
    {
        using var reader = new FakeReader(); using var resolver = Resolver(reader); resolver.Reload();
        Assert.Null(resolver.Lookup(address)); Assert.Equal(0, reader.Calls);
        Assert.Null(resolver.Lookup(new string('x', 1000000))); Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(double.NaN, 0)] [InlineData(91, 0)] [InlineData(0, -181)] [InlineData(double.PositiveInfinity, 0)]
    public void Invalid_coordinates_preserve_country_without_inventing_point(double lat, double lon)
    {
        using var reader = new FakeReader { Record = ConnectionMmdbFixture.Record(lat, lon) };
        using var resolver = Resolver(reader); resolver.Reload();
        var location = resolver.Lookup("8.8.8.8"); Assert.NotNull(location); Assert.Null(location.Point); Assert.Equal("ZZ", location.CountryCode);
    }

    [Fact]
    public void Missing_country_is_unknown_and_country_only_is_retained_and_strings_are_bounded()
    {
        using var reader = new FakeReader { Record = new() }; using var resolver = Resolver(reader); resolver.Reload();
        Assert.Null(resolver.Lookup("8.8.8.8"));
        reader.Record = ConnectionMmdbFixture.Record(city: new string('x', 10000)); reader.Record.Remove("location");
        var location = resolver.Lookup("1.1.1.1"); Assert.NotNull(location); Assert.Null(location.Point); Assert.Equal(160, location.City!.Length);
    }

    [Fact]
    public void Lookup_cache_including_unknown_is_bounded_to_8192_and_reset_on_reload()
    {
        using var reader = new FakeReader(); using var resolver = Resolver(reader); resolver.Reload();
        var first = resolver.Lookup("8.0.0.1"); Assert.Same(first, resolver.Lookup("8.0.0.1")); Assert.Equal(1, reader.Calls);
        for (int i = 2; i <= 8194; i++) resolver.Lookup(new IPAddress(new byte[] { 8, (byte)(i >> 16), (byte)(i >> 8), (byte)i }).ToString());
        resolver.Lookup("8.0.0.1"); Assert.Equal(8195, reader.Calls);
        reader.Record = null; resolver.Lookup("9.9.9.9"); int calls = reader.Calls; resolver.Lookup("9.9.9.9"); Assert.Equal(calls, reader.Calls);
    }

    [Fact]
    public async Task Reload_and_dispose_wait_for_an_active_lookup_and_failed_reload_retains_old_reader()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var old = new FakeReader { Entered = entered, Release = release }; var next = new FakeReader(); int opens = 0;
        using var resolver = new ConnectionGeolocation("unused", _ => ++opens == 1 ? old : next); resolver.Reload();
        var lookup = Task.Run(() => resolver.Lookup("8.8.8.8")); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var reload = Task.Run(resolver.Reload);
        try { await Task.Delay(50); Assert.False(old.Disposed); }
        finally { release.Set(); }
        Assert.NotNull(await lookup); await reload; Assert.True(old.Disposed); Assert.NotNull(resolver.Lookup("1.1.1.1"));
        using var file = new GeoFile(ConnectionMmdbFixture.Create()); using var real = new ConnectionGeolocation(file.Path); real.Reload();
        string invalid = file.Path + ".invalid"; File.WriteAllText(invalid, "invalid"); File.Move(invalid, file.Path, true);
        real.Reload(); Assert.NotNull(real.Lookup("8.8.8.8")); Assert.Contains("retained", real.Status);
    }

    [Fact]
    public async Task Dispose_does_not_close_reader_during_active_lookup()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var reader = new FakeReader { Entered = entered, Release = release }; var resolver = Resolver(reader); resolver.Reload();
        var lookup = Task.Run(() => resolver.Lookup("8.8.8.8")); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var close = Task.Run(resolver.Dispose);
        try { await Task.Delay(50); Assert.False(reader.Disposed); }
        finally { release.Set(); }
        Assert.NotNull(await lookup); await close; Assert.True(reader.Disposed); Assert.Null(resolver.Lookup("8.8.8.8"));
    }

    [Theory]
    [InlineData("USA")] [InlineData("1X")] [InlineData("U")]
    public void Malformed_country_codes_are_unknown(string code)
    {
        var record = ConnectionMmdbFixture.Record(); ((Dictionary<string, object>)record["country"])["iso_code"] = code;
        using var reader = new FakeReader { Record = record }; using var resolver = Resolver(reader); resolver.Reload();
        Assert.Null(resolver.Lookup("8.8.8.8"));
    }

    [Fact]
    public void Successful_reload_invalidates_cached_locations()
    {
        using var first = new FakeReader(); using var second = new FakeReader { Record = ConnectionMmdbFixture.Record(city: "Replacement") };
        int opens = 0; using var resolver = new ConnectionGeolocation("unused", _ => ++opens == 1 ? first : second); resolver.Reload();
        Assert.Equal("Example City", resolver.Lookup("8.8.8.8")!.City); resolver.Reload();
        Assert.Equal("Replacement", resolver.Lookup("8.8.8.8")!.City);
    }

    [Fact]
    public void Missing_database_is_reported_and_constructor_never_reads()
    {
        int opens = 0; using var resolver = new ConnectionGeolocation("missing", _ => { opens++; throw new FileNotFoundException(); });
        Assert.Equal(0, opens); resolver.Reload(); Assert.Equal(1, opens); Assert.Null(resolver.Lookup("8.8.8.8")); Assert.Contains("missing", resolver.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectionGeolocation Resolver(FakeReader reader) => new("unused", _ => reader);
    private sealed class FakeReader : IConnectionGeoReader
    {
        public DateTimeOffset DatabaseDate => new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        internal Dictionary<string, object>? Record = ConnectionMmdbFixture.Record(); internal int Calls; internal bool Disposed;
        internal ManualResetEventSlim? Entered, Release;
        public Dictionary<string, object>? Find(IPAddress address) { Assert.False(Disposed); Calls++; Entered?.Set(); Release?.Wait(); Assert.False(Disposed); return Record; }
        public void Dispose() => Disposed = true;
    }

    internal sealed class GeoFile : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-geo-" + Guid.NewGuid().ToString("N"), "city.mmdb");
        internal GeoFile(byte[]? data = null) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); if (data is not null) File.WriteAllBytes(Path, data); }
        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true);
    }
}

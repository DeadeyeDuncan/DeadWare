using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ConnectionLedgerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static ConnectionPeer Peer(int port = 443) => new(new(42, 100, "app"), "192.168.1.5", 5000, "8.8.8.8", port, ConnectionProtocol.Tcp);

    [Fact] public void Peer_expires_after_60_seconds()
    {
        var ledger = new ConnectionLedger();
        ledger.Record(Peer(), Epoch, 0, false);
        var first = ledger.Take(Epoch, 1);
        Assert.Equal(0, Assert.Single(first.Live).ReceivedBytes);
        Assert.Single(ledger.Take(Epoch.AddSeconds(59), 60).Live);
        Assert.Empty(ledger.Take(Epoch.AddSeconds(60), 61).Live);
        Assert.Single(first.Live);
        Assert.Throws<NotSupportedException>(() => ((IList<ConnectionTraffic>)first.Live).Clear());
    }

    [Fact] public void Rates_use_actual_elapsed_seconds_and_zero_without_new_bytes()
    {
        var ledger = new ConnectionLedger();
        ledger.Record(Peer(), Epoch, 100, false);
        Assert.Equal(0, Assert.Single(ledger.Take(Epoch, 10).Live).DownloadBytesPerSecond);
        ledger.Record(Peer(), Epoch.AddSeconds(2), 900, false);
        ledger.Record(Peer(), Epoch.AddSeconds(2), 300, true);
        var sample = ledger.Take(Epoch.AddSeconds(3), 13);
        var row = Assert.Single(sample.Live);
        Assert.Equal(1000, row.ReceivedBytes);
        Assert.Equal(300, row.SentBytes);
        Assert.Equal(300, row.DownloadBytesPerSecond);
        Assert.Equal(100, row.UploadBytesPerSecond);
        Assert.Equal(Epoch, sample.CoverageFrom);
        var quiet = Assert.Single(ledger.Take(Epoch.AddSeconds(5), 15).Live);
        Assert.Equal(0, quiet.DownloadBytesPerSecond);
        Assert.Equal(0, quiet.UploadBytesPerSecond);
    }

    [Fact] public void Event_minute_survives_publication_across_boundary()
    {
        var ledger = new ConnectionLedger();
        ledger.Take(Epoch.AddSeconds(58), 1);
        ledger.Record(Peer(), Epoch.AddSeconds(59), 11, false);
        ledger.Record(Peer(), Epoch.AddMinutes(1).AddSeconds(1), 13, true);
        var sample = ledger.Take(Epoch.AddMinutes(1).AddSeconds(2), 5);
        Assert.Equal(2, sample.Deltas.Count);
        var first = Assert.Single(sample.Deltas, d => d.Minute == Epoch);
        Assert.Equal(11, first.ReceivedBytes);
        Assert.Equal(Epoch.AddSeconds(59), first.FirstSeen);
        var second = Assert.Single(sample.Deltas, d => d.Minute == Epoch.AddMinutes(1));
        Assert.Equal(13, second.SentBytes);
        Assert.Empty(ledger.Take(Epoch.AddMinutes(1).AddSeconds(3), 6).Deltas);
        Assert.Equal(2, sample.Deltas.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<ConnectionDelta>)sample.Deltas).Clear());
    }

    [Fact] public void Limits_report_drops_without_unbounded_growth()
    {
        var ledger = new ConnectionLedger(2, 2);
        ledger.Take(Epoch.AddSeconds(58), 1);
        ledger.Record(Peer(1), Epoch.AddSeconds(59), 11, false);
        ledger.Record(Peer(2), Epoch.AddSeconds(59), 12, false);
        for (int port = 3; port < 1003; port++) ledger.Record(Peer(port), Epoch.AddSeconds(59), 1, false);
        // A new minute also needs a bounded delta key, even for an existing peer.
        ledger.Record(Peer(1), Epoch.AddMinutes(1), 50, false);
        var sample = ledger.Take(Epoch.AddMinutes(1), 3);
        Assert.Equal(2, sample.Live.Count);
        Assert.Equal(2, sample.Deltas.Count);
        Assert.Equal(1001, sample.DroppedEvents);
        Assert.False(sample.Complete);
        Assert.Equal(23, sample.Live.Sum(x => x.ReceivedBytes));
        Assert.True(ledger.Take(Epoch.AddMinutes(1).AddSeconds(1), 4).Complete);
    }

    [Fact] public void Break_and_clock_discontinuity_do_not_bridge_rates_or_coverage()
    {
        var ledger = new ConnectionLedger();
        var baseline = ledger.Take(Epoch, 1);
        ledger.Record(Peer(), Epoch.AddSeconds(1), 50, false);
        ledger.Break(7);
        var broken = ledger.Take(Epoch.AddSeconds(2), 3);
        Assert.NotEqual(baseline.Segment, broken.Segment);
        Assert.Empty(broken.Live);
        Assert.Empty(broken.Deltas);
        Assert.Null(broken.CoverageFrom);
        Assert.False(broken.Complete);
        Assert.Equal(1, broken.DroppedEvents);
        Assert.Equal(7, broken.LostEvents);
        ledger.Record(Peer(), Epoch.AddSeconds(3), 40, true);
        var resumed = ledger.Take(Epoch.AddSeconds(3), 4);
        Assert.Equal(40, Assert.Single(resumed.Live).UploadBytesPerSecond);
        Assert.Equal(Epoch.AddSeconds(2), resumed.CoverageFrom);
        ledger.Record(Peer(), Epoch.AddSeconds(4), 70, false);
        var jumped = ledger.Take(Epoch.AddHours(1), 5);
        Assert.NotEqual(resumed.Segment, jumped.Segment);
        Assert.Null(jumped.CoverageFrom);
        Assert.Empty(jumped.Live);
        Assert.Empty(jumped.Deltas);
        Assert.False(jumped.Complete);
        Assert.True(jumped.Sequence > resumed.Sequence);
        Assert.Equal(2, jumped.DroppedEvents);
    }

    [Fact] public void Invalid_inputs_and_future_observations_do_not_invent_rows()
    {
        var ledger = new ConnectionLedger();
        ledger.Take(Epoch, 1);
        ledger.Record(Peer(-1), Epoch, 1, false);
        ledger.Record(Peer(65536), Epoch, 1, false);
        ledger.Record(Peer(), Epoch, -1, false);
        ledger.Record(Peer(), Epoch, int.MaxValue, false);
        ledger.Record(Peer() with { RemoteAddress = "invalid" }, Epoch, 1, false);
        ledger.Record(Peer(), Epoch.AddSeconds(10), 50, false);
        var sample = ledger.Take(Epoch.AddSeconds(1), 2);
        Assert.Empty(sample.Live);
        Assert.Empty(sample.Deltas);
        Assert.Equal(6, sample.DroppedEvents);
        Assert.False(sample.Complete);
        var invalidTime = ledger.Take(Epoch.AddSeconds(2), double.NaN);
        Assert.False(invalidTime.Available);
        Assert.Null(invalidTime.CoverageFrom);
        Assert.False(invalidTime.Complete);
        Assert.Empty(invalidTime.Live);
    }

    [Fact] public void Mapped_addresses_share_a_peer_and_out_of_order_events_preserve_observation_bounds()
    {
        var ledger = new ConnectionLedger();
        ledger.Record(Peer(), Epoch.AddSeconds(2), 10, false);
        ledger.Record(Peer() with { RemoteAddress = "::ffff:8.8.8.8", LocalAddress = "::ffff:192.168.1.5" }, Epoch, 20, false);
        var row = Assert.Single(ledger.Take(Epoch.AddSeconds(3), 1).Live);
        Assert.Equal(30, row.ReceivedBytes);
        Assert.Equal(Epoch, row.FirstSeen);
        Assert.Equal(Epoch.AddSeconds(2), row.LastSeen);
    }

    [Fact] public void Small_future_skew_keeps_actual_event_time_across_a_minute_boundary()
    {
        var ledger = new ConnectionLedger();
        ledger.Take(Epoch.AddSeconds(58), 1);
        ledger.Record(Peer(), Epoch.AddMinutes(1).AddMilliseconds(250), 10, false);
        var sample = ledger.Take(Epoch.AddSeconds(59), 2);
        Assert.True(sample.Complete);
        Assert.Equal(Epoch.AddMinutes(1), Assert.Single(sample.Deltas).Minute);
        Assert.Equal(Epoch.AddMinutes(1).AddMilliseconds(250), Assert.Single(sample.Live).LastSeen);
    }

    [Theory]
    [InlineData(2, 1)] // monotonic clock did not advance
    [InlineData(-1, 2)] // wall clock moved backward
    [InlineData(61, 62)] // sleep/long collection gap
    public void Discontinuous_samples_reset_pending_state(double wallSeconds, double monotonicSeconds)
    {
        var ledger = new ConnectionLedger();
        var before = ledger.Take(Epoch, 1);
        ledger.Record(Peer(), Epoch, 10, false);
        var after = ledger.Take(Epoch.AddSeconds(wallSeconds), monotonicSeconds);
        Assert.NotEqual(before.Segment, after.Segment);
        Assert.Null(after.CoverageFrom);
        Assert.Empty(after.Live);
        Assert.Empty(after.Deltas);
        Assert.Equal(1, after.DroppedEvents);
        Assert.False(after.Complete);
    }

    [Fact] public void Late_events_are_bounded_and_process_generations_remain_distinct()
    {
        var ledger = new ConnectionLedger();
        ledger.Take(Epoch, 1);
        ledger.Record(Peer(), Epoch.AddSeconds(-61), 99, false);
        ledger.Record(Peer(), Epoch, 10, false);
        ledger.Record(Peer() with { Process = new(42, 200, "replacement") }, Epoch, 20, false);
        var sample = ledger.Take(Epoch.AddSeconds(1), 2);
        Assert.Equal(2, sample.Live.Count);
        Assert.Equal(30, sample.Live.Sum(x => x.ReceivedBytes));
        Assert.Equal(1, sample.DroppedEvents);
        Assert.False(sample.Complete);
    }

    [Fact] public void Rates_remain_finite_when_elapsed_time_is_extremely_small()
    {
        var ledger = new ConnectionLedger();
        ledger.Take(Epoch, 0);
        ledger.Record(Peer(), Epoch.AddTicks(1), 10, true);
        var row = Assert.Single(ledger.Take(Epoch.AddTicks(1), double.Epsilon).Live);
        Assert.True(double.IsFinite(row.UploadBytesPerSecond));
    }
}

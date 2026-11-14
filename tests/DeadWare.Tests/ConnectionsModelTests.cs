using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ConnectionsModelTests
{
    // Numbers and dates are written in the user's locale; the expected texts here are for the invariant one.
    public ConnectionsModelTests() => System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

    static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact] public void Geography_parts_fail_on_their_own()
    {
        var geography = MapGeography.Load(name => name switch
        {
            "land" => "[[[0,0],[10,0],[10,10],[0,0]]]",
            "borders" => "[[[0,0],[5,5]]]",
            "cities" => "[{\"id\":\"a\",\"name\":\"Oslo\",\"country\":\"Norway\",\"latitude\":59.9,\"longitude\":10.7}]",
            _ => throw new FileNotFoundException(name),
        });
        Assert.Single(geography.Land);
        Assert.Single(geography.Borders);
        Assert.Empty(geography.StateBorders);
        Assert.Equal("Oslo", Assert.Single(geography.Cities).Name);
        Assert.Equal("State borders unavailable", geography.Status);
    }

    [Fact] public void An_unclosed_land_ring_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => MapGeography.ParseLand("[[[0,0],[10,0],[10,10],[5,5]]]"));
    }

    [Fact] public void The_camera_centre_is_drawn_at_the_middle_of_the_bounds()
    {
        var frame = new MapFrame(420, 255, new MapCamera(10, 20, 1, false), false);
        Assert.Equal(new PlotRect(10, 21, 400, 200), frame.Bounds);
        var centre = frame.Position(new GeoPoint(20, 10));
        Assert.NotNull(centre);
        Assert.Equal(210, centre!.Value.X, 6);
        Assert.Equal(121, centre.Value.Y, 6);
        Assert.Null(frame.Position(new GeoPoint(-20, -170)));
    }

    [Fact] public void A_hit_takes_the_nearest_node_within_the_radius()
    {
        var frame = new MapFrame(420, 255, new MapCamera(0, 0, 1, false), false);
        var nodes = new[] { (new PlotPoint(100, 100), "a"), (new PlotPoint(110, 100), "b") };
        Assert.Equal("b", frame.Hit(nodes, new PlotPoint(108, 100))!.Value.Key);
        Assert.Null(frame.Hit(nodes, new PlotPoint(130, 100)));
    }

    [Fact] public void A_route_runs_from_the_origin_to_its_node()
    {
        var frame = new MapFrame(420, 255, new MapCamera(0, 0, 1, false), false);
        var point = new ConnectionMapPoint("k", new GeoPoint(10, 20), 1000, 0, false);
        var route = Assert.Single(frame.Routes([point], new GeoPoint(0, 0)));
        Assert.Equal(6, route.Radius, 6);
        var start = frame.Sample(route, 0); var end = frame.Sample(route, 1);
        Assert.Equal(frame.Position(new GeoPoint(0, 0))!.Value.X, start!.Value.X, 6);
        Assert.Equal(frame.Position(point.Point)!.Value.X, end!.Value.X, 6);
        Assert.Empty(Assert.Single(frame.Routes([point], null)).Samples);
    }

    [Fact] public void An_offset_lane_runs_beside_its_arc()
    {
        PlotPoint[][] arc = [[new(0, 0), new(10, 0), new(20, 0)]];
        var lane = MapFrame.Offset(arc, 1);
        Assert.Equal([new PlotPoint(0, 1), new PlotPoint(10, 1), new PlotPoint(20, 1)], lane[0]);
        Assert.Same(arc, MapFrame.Offset(arc, 0));
    }

    static TrafficStripSlot Slot(int minute, long seconds, long received, long sent, long otherReceived = 0) =>
        new(T0.AddMinutes(minute), T0.AddMinutes(minute + 1), 1, seconds, false, [received, otherReceived], [sent, 0]);

    static TrafficStrip Strip(params TrafficStripSlot[] slots) => new(T0, T0.AddMinutes(10), 60_000, TrafficMeasure.Total,
        [new("app.exe", "app.exe", slots.Sum(s => s.Received[0]), slots.Sum(s => s.Sent[0]))], slots.Sum(s => s.Received[1]), 0, 2, slots);

    [Fact] public void A_strip_stacks_rates_and_marks_slot_states()
    {
        var model = new TrafficStripModel(TrafficStripKind.Connections);
        model.SetData(Strip(Slot(0, 60, 6000, 0, 600), Slot(1, 60, 0, 0), Slot(2, 20, 100, 100)), T0.AddMinutes(10), T0.AddMinutes(10));
        model.Layout(1020, 134);
        Assert.Equal(TrafficSlotState.Full, model.State(0));
        Assert.Equal(TrafficSlotState.NoTraffic, model.State(1));
        Assert.Equal(TrafficSlotState.Partial, model.State(2));
        Assert.Equal(2, model.SeriesCount);
        Assert.Equal(110 * 1.15, model.Maximum, 6);
        Assert.Equal(new PlotRect(12, 10, 1000, 100), model.Plot);
        // Minutes 3 to 10 hold no slot and are due, so they are hatched.
        Assert.Equal(new PlotRect(312, 10, 700, 100), Assert.Single(model.Hatch));
        Assert.Contains(model.Bands, b => b.Series == 0 && b.Full);
        Assert.Contains(model.Bands, b => b.Series == 0 && !b.Full);
    }

    [Fact] public void A_filtered_not_recorded_app_s_empty_slots_are_not_saved()
    {
        var model = new TrafficStripModel(TrafficStripKind.Connections);
        model.SetData(Strip(Slot(0, 60, 0, 0)), T0.AddMinutes(10), T0.AddMinutes(10), filterNotRecorded: true);
        model.Layout(1020, 134);
        Assert.Equal(TrafficSlotState.NotSaved, model.State(0));
        Assert.Equal([TrafficStripModel.NotRecordedReason], model.NotSavedReasons(0));
        var hover = model.Hover(new PlotPoint(60, 50));
        Assert.Equal(0, hover!.Index);
        Assert.Equal(TrafficStripModel.NoSavedTraffic, hover.Lines[1]);
    }

    [Fact] public void The_legend_gives_totals_then_the_shown_slot()
    {
        var model = new TrafficStripModel(TrafficStripKind.Connections);
        model.SetData(Strip(Slot(0, 60, 6000, 0, 2000)), T0.AddMinutes(10), T0.AddMinutes(10));
        var totals = model.LegendRows(null);
        Assert.Equal("Totals for this interval", totals[0].Text);
        Assert.Equal("app.exe · 6.0 KB · 75%", totals[1].Text);
        Assert.Equal("Other (2 more) · 2.0 KB · 25%", totals[2].Text);
        Assert.Null(totals[2].Application);
        var slot = model.LegendRows(0);
        Assert.Equal("app.exe · 6.0 KB", slot[1].Text);
    }

    [Fact] public void Left_and_right_step_from_the_selection_or_the_mark()
    {
        var model = new TrafficStripModel(TrafficStripKind.Connections);
        model.SetData(Strip(Slot(0, 60, 1, 0), Slot(1, 60, 1, 0), Slot(5, 60, 1, 0)), T0.AddMinutes(10), T0.AddMinutes(10));
        Assert.Equal(0, model.Step(true, 1, null));
        Assert.Null(model.Step(true, 0, null));
        Assert.Equal(2, model.Step(false, null, T0.AddMinutes(3)));
        Assert.Equal(1, model.Step(true, null, T0.AddMinutes(3)));
        Assert.Equal(2, model.Step(true, null, null));
    }

    [Fact] public void Local_times_parse_and_refuse_skipped_minutes()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.True(ConnectionsPresenter.TryParseLocalTime("2026-11-12 10:00", zone, out var utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.Zero), utc);
        Assert.False(ConnectionsPresenter.TryParseLocalTime("2026-03-08 02:30", zone, out _));
        Assert.False(ConnectionsPresenter.TryParseLocalTime("yesterday", zone, out _));
        var boxes = ConnectionsPresenter.LocalBoxes(T0, T0.AddMinutes(15), TimeZoneInfo.Utc);
        Assert.Equal(("2026-11-12 10:00", "2026-11-12 10:15"), boxes);
    }

    [Fact] public void Endpoints_and_locations_read_plainly()
    {
        Assert.Equal("[2001:db8::1]:443 Tcp", ConnectionsPresenter.Endpoint("2001:db8::1", 443, ConnectionProtocol.Tcp));
        Assert.Equal("1.2.3.4:53 Udp", ConnectionsPresenter.Endpoint("1.2.3.4", 53, ConnectionProtocol.Udp));
        Assert.Equal("Local", ConnectionsPresenter.Location("192.168.1.5", null));
        Assert.Equal("Unlocated", ConnectionsPresenter.Location("8.8.8.8", null));
        Assert.Equal("Oslo, Norway", ConnectionsPresenter.Location("8.8.8.8", new ConnectionLocation("NO", "Norway", null, "Oslo", null, "test")));
        Assert.Equal("1 destination", ConnectionsPresenter.Count(1, "destination", "destinations"));
    }

    [Fact] public void A_live_destination_row_names_why_a_cell_is_empty()
    {
        var destination = new ConnectionDestination("8.8.8.8", new NetworkAddressClass(NetworkAddressKind.Public, "Internet"), null, [], true,
            [new("app.exe", 2000, 1000), new("other.exe", 10, 10)], 3, [new(443, ConnectionProtocol.Tcp, 2010, 1010)], 1, 2010, 1010, null, 500, 2, null, T0, T0, null);
        var cells = DestinationCells.Build(destination, new DestinationCellContext(false, true, true, false, name => name == "app.exe"));
        Assert.Equal("—", cells.Location);
        Assert.Equal(DestinationCopy.NoDatabase, cells.LocationTip);
        Assert.Equal("app.exe · not recorded +2", cells.Apps);
        Assert.Equal("443 Tcp", cells.Ports);
        Assert.Equal("—", cells.Download);
        Assert.Equal(DestinationCopy.RateUnavailable, cells.Tip(ConnectionDestinationColumn.Download));
        Assert.Equal("500.0 B/s", cells.Cell(ConnectionDestinationColumn.Upload));
        Assert.Equal("2", cells.Connections);
        Assert.Equal("", cells.LastSeen);
        Assert.EndsWith("and 1 more", cells.AppsTip);
    }
}

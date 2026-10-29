using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ConnectionProjectionTests
{
    private static readonly MapCamera Globe = new(0, 0, 1, false);

    [Fact] public void Center_rear_and_horizon_have_correct_depth_and_visibility()
    {
        var center = ConnectionProjection.Project(new(0, 0), Globe);
        Assert.Equal(0, center.X, 8); Assert.Equal(0, center.Y, 8); Assert.Equal(1, center.Depth, 8); Assert.True(center.Visible);
        Assert.False(ConnectionProjection.Project(new(0, 180), Globe).Visible);
        var edge = ConnectionProjection.Project(new(0, 90), Globe);
        Assert.Equal(1, edge.X, 8); Assert.Equal(0, edge.Depth, 8); Assert.True(edge.Visible);
    }

    [Fact] public void Camera_center_pole_and_wrapped_longitudes_project_consistently()
    {
        var camera = new MapCamera(179, 80, 1, false);
        var center = ConnectionProjection.Project(new(80, 179), camera);
        Assert.Equal(0, center.X, 8); Assert.Equal(0, center.Y, 8); Assert.Equal(1, center.Depth, 8);
        Assert.Equal(ConnectionProjection.Project(new(10, 180), Globe), ConnectionProjection.Project(new(10, -180), Globe));
        var pole = ConnectionProjection.Project(new(90, 20), new(0, 90, 1, false));
        Assert.Equal(0, pole.Y, 8); Assert.Equal(1, pole.Depth, 8);
    }

    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity, double.NaN)]
    [InlineData(900, 999, double.PositiveInfinity)]
    [InlineData(-900, -999, -5)]
    public void Camera_normalization_keeps_projection_finite(double lon, double lat, double zoom)
    {
        var camera = ConnectionProjection.Normalize(new(lon, lat, zoom, true));
        Assert.InRange(camera.Longitude, -180, 180); Assert.InRange(camera.Latitude, -90, 90); Assert.InRange(camera.Zoom, 1, 8);
        var point = ConnectionProjection.Project(new(45, 60), camera);
        Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
        Assert.False(ConnectionProjection.Project(new(double.NaN, 0), camera).Visible);
    }

    [Fact] public void Great_circle_uses_short_arc_and_handles_antipodes_without_nan()
    {
        var arc = ConnectionProjection.GreatCircle(new(0, 170), new(0, -170), 16);
        Assert.Equal(new GeoPoint(0, 170), arc[0]); Assert.Equal(new GeoPoint(0, -170), arc[^1]);
        Assert.All(arc, p => Assert.True(Math.Abs(p.Longitude) >= 169.99));
        var polar = ConnectionProjection.GreatCircle(new(60, -60), new(60, 60), 16);
        Assert.True(polar[8].Latitude > 70);
        Assert.All(ConnectionProjection.GreatCircle(new(0, 0), new(0, 180), 16), p => Assert.True(double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude)));
    }

    [Fact] public void Flat_dateline_splits_at_both_edges_without_cross_screen_segment()
    {
        var paths = ConnectionProjection.ProjectPath([new(0, 170), new(0, -170)], Globe with { Flat = true });
        Assert.Equal(2, paths.Count);
        Assert.Equal(1, paths[0][^1].X, 8); Assert.Equal(-1, paths[1][0].X, 8);
        Assert.All(paths, path => Assert.True(Math.Abs(path[^1].X - path[0].X) < .1));
    }

    [Fact] public void Globe_crossing_is_clipped_to_horizon_and_rear_path_is_absent()
    {
        var paths = ConnectionProjection.ProjectPath([new(0, 60), new(0, 120)], Globe);
        Assert.Single(paths); Assert.Equal(1, paths[0][^1].X, 6); Assert.Equal(0, paths[0][^1].Depth, 6);
        Assert.Empty(ConnectionProjection.ProjectPath([new(0, 120), new(0, 150)], Globe));
    }

    [Fact] public void Grouping_preserves_selection_keys_saturates_totals_and_bounds_markers()
    {
        var points = Enumerable.Range(0, 300).Select(i => new ConnectionMapPoint($"p{i:000}", new(i % 80, i), 1, 2, false)).ToList();
        points.Add(new("alias", new(0, 0), long.MaxValue, 3, true));
        var result = ConnectionProjection.Group(points);
        Assert.Equal(256, result.Points.Count); Assert.Equal(44, result.Overflow);
        var selected = Assert.Single(result.Points, p => p.Selected);
        Assert.Equal("alias", selected.Key); Assert.Equal(long.MaxValue, selected.ReceivedBytes); Assert.Equal(5, selected.SentBytes);
        Assert.Equal(result.Points, ConnectionProjection.Group(points.AsEnumerable().Reverse()).Points);
    }
}

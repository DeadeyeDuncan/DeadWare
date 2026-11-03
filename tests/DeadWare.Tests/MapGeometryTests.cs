using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class MapGeometryTests
{
    [Fact] public void Back_hemisphere_ring_points_are_clamped_to_the_limb()
    {
        var camera=new MapCamera(-55,28,1,false);
        GeoPoint[] ring=[new(40,-100),new(40,100),new(-40,100),new(-40,-100),new(40,-100)];
        var projected=ConnectionProjection.ProjectRing(ring,camera);
        Assert.Equal(ring.Length,projected.Count);
        var back=projected[1];Assert.False(back.Visible);Assert.Equal(1,Math.Sqrt(back.X*back.X+back.Y*back.Y),3);
        Assert.True(projected[0].Visible);
    }
    [Fact] public void Limb_clamp_scales_with_zoom()
    {
        var camera=new MapCamera(0,0,2.5,false);
        var back=ConnectionProjection.ProjectRing([new GeoPoint(0,180)],camera)[0];
        Assert.False(back.Visible);Assert.Equal(2.5,Math.Sqrt(back.X*back.X+back.Y*back.Y),6);
    }
    [Fact] public void Flat_ring_projection_matches_point_projection()
    {
        var camera=new MapCamera(0,0,1,true);GeoPoint[] ring=[new(10,10),new(10,20),new(0,20),new(10,10)];
        Assert.Equal(ring.Select(p=>ConnectionProjection.Project(p,camera)),ConnectionProjection.ProjectRing(ring,camera));
    }
    [Fact] public void Particle_fractions_stay_in_range_and_advance()
    {
        var a=ArcParticles.Fractions(0,.25,3,false);var b=ArcParticles.Fractions(1,.25,3,false);
        Assert.All(a,f=>Assert.InRange(f,0,1));Assert.Equal(3,a.Count);
        Assert.Equal(a[0]+.25,b[0],6);
        Assert.Equal(1-a[0],ArcParticles.Fractions(0,.25,3,true)[0],6);
        Assert.Empty(ArcParticles.Fractions(0,.25,0,false));
    }
    [Fact] public void Particle_count_follows_activity_level()
    {Assert.Equal(0,ArcParticles.Count(0));Assert.Equal(1,ArcParticles.Count(.2));Assert.Equal(2,ArcParticles.Count(.5));Assert.Equal(3,ArcParticles.Count(1));Assert.Equal(0,ArcParticles.Count(double.NaN));}
    [Fact] public void Scalar_fraction_matches_the_array_form()
    {
        Assert.Equal(ArcParticles.Fractions(1.7,.33,3,true)[2],ArcParticles.Fraction(1.7,.33,3,2,true),10);
        Assert.Equal(ArcParticles.Fractions(4.2,.25,2,false)[1],ArcParticles.Fraction(4.2,.25,2,1,false),10);
    }
    [Fact] public void Horizon_crossing_lies_on_the_limb()
    {
        var camera=new MapCamera(0,0,1,false);
        var crossing=ConnectionProjection.HorizonCrossing(new GeoPoint(0,60),new GeoPoint(60,120),camera);
        Assert.True(crossing.Visible);Assert.Equal(0,crossing.Depth,5);Assert.Equal(1,Math.Sqrt(crossing.X*crossing.X+crossing.Y*crossing.Y),3);
        var reversed=ConnectionProjection.HorizonCrossing(new GeoPoint(60,120),new GeoPoint(0,60),camera);
        Assert.Equal(crossing.X,reversed.X,3);Assert.Equal(crossing.Y,reversed.Y,3);
    }
    [Fact] public void Map_point_labels_default_to_null()
    {var p=new ConnectionMapPoint("k",new GeoPoint(1,2),1,1,false);Assert.Null(p.Label);Assert.Null(p.Detail);}
}

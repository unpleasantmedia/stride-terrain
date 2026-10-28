using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Stride.Terrain;
using Xunit;

namespace Stride.Terrain.Tests;

public class TerrainControlTests
{
    [Fact]
    public void TexturesPackAndKeepTheOtherBits()
    {
        var control = TerrainControl.With(0u, TerrainControl.HoleBit, true);
        control = TerrainControl.WithTextures(control, 3, 7, 0.5f);
        Assert.Equal(3, TerrainControl.Base(control));
        Assert.Equal(7, TerrainControl.Overlay(control));
        Assert.Equal(0.5f, TerrainControl.Blend(control), 2);
        Assert.True(TerrainControl.IsHole(control));
    }
}

public class TerrainDataTests
{
    private static TerrainData Empty(int size = 16, float spacing = 2f) => new(size, spacing, Array.Empty<(int, int)>(), (_, _) => null);

    [Fact]
    public void RegionsRoundTripThroughTheirFiles()
    {
        var region = new TerrainRegion(-1, 2, 8);
        region.Heights[5] = 12.5f;
        region.Control[9] = TerrainControl.WithTextures(0u, 4, 2, 1f);
        using var stream = new MemoryStream();
        region.Write(stream);
        stream.Position = 0;
        var read = TerrainRegion.Read(stream);
        Assert.Equal((-1, 2, 8), (read.X, read.Z, read.Size));
        Assert.Equal(12.5f, read.Heights[5]);
        Assert.Equal(region.Control[9], read.Control[9]);
        Assert.Equal("Region-X-01-Z+02", TerrainRegion.NameOf(-1, 2));
    }

    [Fact]
    public void HeightsBlendBetweenSamplesAndNoneOutsideOrInHoles()
    {
        var data = Empty();
        var region = data.Add(0, 0);
        region.Heights[0] = 0f;
        region.Heights[1] = 10f;
        Assert.Equal(5f, data.HeightAt(new Vector3(1f, 0f, 0f)), 3);
        Assert.True(float.IsNaN(data.HeightAt(new Vector3(-5f, 0f, -5f))));
        region.Control[0] = TerrainControl.With(0u, TerrainControl.HoleBit, true);
        Assert.True(float.IsNaN(data.HeightAt(new Vector3(0.5f, 0f, 0.5f))));
    }

    [Fact]
    public void RaiseMakesARegionAndLiftsTheMiddleMost()
    {
        var data = Empty();
        var stroke = new TerrainStroke(data, TerrainBrushKind.Raise, false, 0f);
        var (regions, samples) = stroke.Work(new Vector3(8f, 0f, 8f), 1f, new TerrainBrushSettings(Radius: 6f, Strength: 2f));
        Assert.Contains((0, 0), stroke.Added);
        Assert.NotEmpty(regions);
        Assert.NotNull(samples);
        Assert.Equal(2f, data.HeightOf(4, 4), 3);
        Assert.Equal(0f, data.HeightOf(0, 0), 3);
        Assert.Equal(0f, stroke.HeightsBefore[(4, 4)]);
        // Undone: all back as it was.
        TerrainStroke.SetHeights(data, stroke.HeightsBefore);
        Assert.Equal(0f, data.HeightOf(4, 4));
    }

    [Fact]
    public void PaintBlendsATextureInUntilItIsAll()
    {
        var control = TerrainControl.WithTextures(0u, 1, 1, 0f);
        control = TerrainStroke.Painted(control, 3, 0.4f, 1f);
        Assert.Equal((1, 3), (TerrainControl.Base(control), TerrainControl.Overlay(control)));
        control = TerrainStroke.Painted(control, 3, 0.8f, 1f);
        Assert.Equal((3, 3), (TerrainControl.Base(control), TerrainControl.Overlay(control)));
    }

    [Fact]
    public void RayMeetsTheGround()
    {
        var data = Empty(64);
        var region = data.Add(0, 0);
        Array.Fill(region.Heights, 3f);
        var hit = data.Intersect(new Vector3(20f, 50f, 20f), -Vector3.UnitY, 1000f);
        Assert.NotNull(hit);
        Assert.Equal(3f, hit!.Value.Y, 1);
    }
}

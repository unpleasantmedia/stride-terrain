using System.Collections.Generic;
using System.Linq;
using Stride.Terrain;
using Xunit;

namespace Stride.Terrain.Tests;

public class TerrainWholeBrushTests
{
    private static TerrainData TwoRegions()
    {
        var regions = new[] { new TerrainRegion(0, 0, 4), new TerrainRegion(1, 0, 4) }.ToDictionary(r => (r.X, r.Z));
        return new TerrainData(4, 1f, regions.Keys, (x, z) => regions.GetValueOrDefault((x, z)));
    }

    [Fact]
    public void RaisesAllOfItBySecondsStrength()
    {
        var data = TwoRegions();
        var changed = TerrainWholeBrush.Apply(data, TerrainBrushKind.Raise, false, 0f, new TerrainBrushSettings(Strength: 3f));
        Assert.Equal(2, changed.Count);
        Assert.All(data.All.SelectMany(r => r.Heights), h => Assert.Equal(3f, h));
        Assert.All(changed, r => Assert.True(r.Modified));
    }

    [Fact]
    public void SmoothsAcrossRegionsEdges()
    {
        var data = TwoRegions();
        // A ridge along the first region's last column, against the second region's first.
        var first = data.Region(0, 0)!;
        for (var z = 0; z < 4; z++)
            first.Heights[z * 4 + 3] = 9f;
        TerrainWholeBrush.Apply(data, TerrainBrushKind.Smooth, false, 0f, new TerrainBrushSettings(Strength: 100f));
        // The second region's first column takes some of the ridge from across the edge.
        Assert.True(data.Region(1, 0)!.Heights[1 * 4 + 0] > 0f);
        Assert.True(first.Heights[1 * 4 + 3] < 9f);
    }

    [Fact]
    public void PaintsAllOfItOffTheRules()
    {
        var data = TwoRegions();
        TerrainWholeBrush.Apply(data, TerrainBrushKind.Paint, false, 0f, new TerrainBrushSettings(Strength: 1f, Texture: 3));
        Assert.All(data.All.SelectMany(r => r.Control), c =>
        {
            Assert.False(TerrainControl.IsAuto(c));
            Assert.Equal(3, TerrainControl.Base(c));
        });
    }
}

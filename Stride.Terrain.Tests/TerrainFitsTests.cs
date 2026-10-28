using System.Collections.Generic;
using System.Linq;
using Stride.Terrain;
using Xunit;

namespace Stride.Terrain.Tests;

public class TerrainFitsTests
{
    private static TerrainData Flat(float height)
    {
        var region = new TerrainRegion(0, 0, 4);
        System.Array.Fill(region.Heights, height);
        var regions = new Dictionary<(int X, int Z), TerrainRegion> { [(0, 0)] = region };
        return new TerrainData(4, 1f, regions.Keys, (x, z) => regions.GetValueOrDefault((x, z)));
    }

    [Fact]
    public void FitsByWeightAndPaints()
    {
        var data = Flat(10f);
        var fits = new TerrainFits();
        fits.Fit(data, "road", new[] { (1, 1), (2, 1) }, new[] { 4f, 4f }, new[] { 1f, 0.5f }, 3, new[] { true, false });
        Assert.Equal(4f, data.HeightOf(1, 1));
        Assert.Equal(7f, data.HeightOf(2, 1));
        Assert.Equal(3, TerrainControl.Base(data.ControlOf(1, 1)));
        Assert.False(TerrainControl.IsAuto(data.ControlOf(1, 1)));
        Assert.True(TerrainControl.IsAuto(data.ControlOf(2, 1)));
    }

    [Fact]
    public void RefittingPutsBackTheLastFitFirst()
    {
        var data = Flat(10f);
        var fits = new TerrainFits();
        fits.Fit(data, "road", new[] { (1, 1) }, new[] { 4f }, new[] { 1f }, -1, new[] { false });
        // The road moved: its old place is as it was, its new one fitted.
        fits.Fit(data, "road", new[] { (2, 2) }, new[] { 6f }, new[] { 1f }, -1, new[] { false });
        Assert.Equal(10f, data.HeightOf(1, 1));
        Assert.Equal(6f, data.HeightOf(2, 2));
        Assert.Equal(10f, fits.BaseHeightOf(data, 2, 2));
    }

    [Fact]
    public void UnfitPutsBackHeightAndPaint()
    {
        var data = Flat(10f);
        var fits = new TerrainFits();
        var control = data.ControlOf(1, 1);
        fits.Fit(data, "road", new[] { (1, 1) }, new[] { 4f }, new[] { 1f }, 2, new[] { true });
        var changed = fits.Unfit(data, "road");
        Assert.Single(changed);
        Assert.Equal(10f, data.HeightOf(1, 1));
        Assert.Equal(control, data.ControlOf(1, 1));
        Assert.False(fits.Has("road"));
    }
}

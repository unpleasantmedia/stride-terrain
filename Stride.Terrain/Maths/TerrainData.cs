using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Terrain;

/// <summary>
/// A terrain's ground: square regions of samples, <see cref="RegionSize"/> across and <see cref="VertexSpacing"/>
/// metres apart, each read when first asked for (by <see cref="Load"/>). Its heights, normals and control can be read
/// anywhere in its regions; outside them, and in holes, there is no ground. Sample (x, z) is at (x, z) times the
/// spacing, in the terrain's own space.
/// </summary>
public sealed class TerrainData
{
    private readonly Dictionary<(int X, int Z), TerrainRegion?> regions = new();

    public TerrainData(int regionSize, float vertexSpacing, IEnumerable<(int X, int Z)> locations, Func<int, int, TerrainRegion?> load)
    {
        RegionSize = Math.Max(regionSize, 2);
        VertexSpacing = Math.Max(vertexSpacing, 0.01f);
        Locations = new HashSet<(int, int)>(locations);
        Load = load;
    }

    public int RegionSize { get; }

    public float VertexSpacing { get; }

    /// <summary>Which regions it has.</summary>
    public HashSet<(int X, int Z)> Locations { get; }

    /// <summary>Reads a region, if there is one there to read.</summary>
    public Func<int, int, TerrainRegion?> Load { get; }

    /// <summary>How many metres across a region is.</summary>
    public float RegionWidth => RegionSize * VertexSpacing;

    /// <summary>A region, if it has one there.</summary>
    public TerrainRegion? Region(int x, int z)
    {
        if (regions.TryGetValue((x, z), out var known))
            return known;
        var region = Locations.Contains((x, z)) ? Load(x, z) : null;
        if (region != null && region.Size != RegionSize)
            region = null;
        return regions[(x, z)] = region;
    }

    /// <summary>Its regions read so far.</summary>
    public IEnumerable<TerrainRegion> Loaded => regions.Values.OfType<TerrainRegion>();

    /// <summary>Its regions, all read.</summary>
    public IEnumerable<TerrainRegion> All => Locations.Select(l => Region(l.X, l.Z)).OfType<TerrainRegion>();

    /// <summary>A new flat region there (none if it has one already): its ground at nought, painted with the first texture.</summary>
    public TerrainRegion Add(int x, int z)
    {
        if (Region(x, z) is { } had)
            return had;
        var region = new TerrainRegion(x, z, RegionSize) { Modified = true };
        Locations.Add((x, z));
        regions[(x, z)] = region;
        return region;
    }

    /// <summary>Which region a sample is in.</summary>
    public (int X, int Z) RegionOf(int x, int z) => (FloorDiv(x, RegionSize), FloorDiv(z, RegionSize));

    /// <summary>Which region a sample is in, and where in it.</summary>
    public (TerrainRegion? Region, int Index) Sample(int x, int z)
    {
        var (rx, rz) = RegionOf(x, z);
        return (Region(rx, rz), (z - rz * RegionSize) * RegionSize + (x - rx * RegionSize));
    }

    /// <summary>A sample's height (holes keep theirs), or NaN where there is no region.</summary>
    public float HeightOf(int x, int z)
    {
        var (region, index) = Sample(x, z);
        return region?.Heights[index] ?? float.NaN;
    }

    /// <summary>A sample's control word, or 0 where there is no region.</summary>
    public uint ControlOf(int x, int z)
    {
        var (region, index) = Sample(x, z);
        return region?.Control[index] ?? 0u;
    }

    /// <summary>How high the ground is at a place (between its samples, blended from the four round it), or NaN where there is none.</summary>
    public float HeightAt(Vector3 at)
    {
        if (TerrainControl.IsHole(ControlAt(at)))
            return float.NaN;
        var x = at.X / VertexSpacing;
        var z = at.Z / VertexSpacing;
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var (fx, fz) = (x - x0, z - z0);
        var (a, b) = (HeightOf(x0, z0), HeightOf(x0 + 1, z0));
        var (c, d) = (HeightOf(x0, z0 + 1), HeightOf(x0 + 1, z0 + 1));
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fz);
    }

    /// <summary>Which way is square to the ground at a place (from its slope a sample's spacing along x and z), or NaN where there is none.</summary>
    public Vector3 NormalAt(Vector3 at)
    {
        var height = HeightAt(at);
        var alongX = HeightAt(at + new Vector3(VertexSpacing, 0f, 0f));
        var alongZ = HeightAt(at + new Vector3(0f, 0f, VertexSpacing));
        return Vector3.Normalize(new Vector3(height - alongX, VertexSpacing, height - alongZ));
    }

    /// <summary>The control word of the sample at or before a place.</summary>
    public uint ControlAt(Vector3 at) => ControlOf((int)MathF.Floor(at.X / VertexSpacing), (int)MathF.Floor(at.Z / VertexSpacing));

    /// <summary>
    /// Where a ray first meets the ground (in its space): stepped along, by half its height over the ground (but half a
    /// sample's spacing at least, so a steep slope is not stepped through far), until under it, then narrowed down to a centimetre.
    /// </summary>
    public Vector3? Intersect(Vector3 from, Vector3 direction, float reach)
    {
        if (direction.LengthSquared() < 1e-12f)
            return null;
        direction = Vector3.Normalize(direction);
        var (travelled, before) = (0f, 0f);
        while (travelled < reach)
        {
            var at = from + direction * travelled;
            var height = HeightAt(at);
            var above = float.IsNaN(height) ? float.NaN : at.Y - height;
            if (above <= 0f)
            {
                var (low, high) = (before, travelled);
                while (high - low > 0.01f)
                {
                    var middle = (low + high) / 2f;
                    var point = from + direction * middle;
                    var ground = HeightAt(point);
                    if (!float.IsNaN(ground) && point.Y <= ground)
                        high = middle;
                    else
                        low = middle;
                }
                return from + direction * high;
            }
            before = travelled;
            travelled += float.IsNaN(above) ? VertexSpacing * 4f : MathF.Max(above * 0.5f, VertexSpacing * 0.5f);
        }
        return null;
    }

    /// <summary>
    /// Changes samples, each to what <paramref name="change"/> makes of it (from its sample coordinates, region and index
    /// in it; true if it changed). Samples outside the regions are left out. Marks the regions changed as modified, and
    /// gives them and the block of samples changed.
    /// </summary>
    public (HashSet<TerrainRegion> Regions, (int X0, int Z0, int X1, int Z1)? Samples) Edit(IEnumerable<(int X, int Z)> samples,
        Func<int, int, TerrainRegion, int, bool> change)
    {
        var changed = new HashSet<TerrainRegion>();
        var (x0, z0, x1, z1) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        foreach (var (x, z) in samples)
        {
            var (region, index) = Sample(x, z);
            if (region == null || !change(x, z, region, index))
                continue;
            region.Modified = true;
            changed.Add(region);
            (x0, z0, x1, z1) = (Math.Min(x0, x), Math.Min(z0, z), Math.Max(x1, x), Math.Max(z1, z));
        }
        return (changed, changed.Count > 0 ? (x0, z0, x1, z1) : null);
    }

    /// <summary>The samples of a block, from one corner to the other, both in.</summary>
    public static IEnumerable<(int X, int Z)> Block(int x0, int z0, int x1, int z1)
    {
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
                yield return (x, z);
        }
    }

    public static int FloorDiv(int value, int by) => (int)MathF.Floor(value / (float)by);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

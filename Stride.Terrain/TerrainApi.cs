using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Plugins;

namespace Stride.Terrain;

/// <summary>
/// What a scene's terrains offer other plugins (see <see cref="PluginApi"/>), by name, each asked of a scene (its
/// entity manager), in the world's space:
/// <list type="bullet">
/// <item><c>Terrain.HasGround</c> <c>Func&lt;EntityManager, bool&gt;</c>: whether it has a terrain with ground.</item>
/// <item><c>Terrain.HeightAt</c> <c>Func&lt;EntityManager, Vector3, float&gt;</c>: the ground's height under a place (NaN for none).</item>
/// <item><c>Terrain.BaseHeightAt</c> <c>Func&lt;EntityManager, Vector3, float&gt;</c>: the ground's height under a place as it was before any fits (for what fits it to follow, without sinking into its own fit).</item>
/// <item><c>Terrain.Intersect</c> <c>Func&lt;EntityManager, Vector3, Vector3, Vector3?&gt;</c>: where a ray (from, direction) first meets the ground.</item>
/// <item><c>Terrain.Grid</c> <c>Func&lt;EntityManager, Vector3, Vector4?&gt;</c>: the grid of the terrain under a place: its origin (x, y, z) and how far apart its samples are (w).</item>
/// <item><c>Terrain.Bounds</c> <c>Func&lt;EntityManager, Vector4?&gt;</c>: how far its terrains reach, along x and z (min x, min z, max x, max z).</item>
/// <item><c>Terrain.Version</c> <c>Func&lt;EntityManager, int&gt;</c>: changes whenever its ground does.</item>
/// <item><c>Terrain.Fit</c> <c>Func&lt;EntityManager, string, Vector3, int[], int[], float[], float[], int, bool[], bool&gt;</c>:
/// fits the terrain under a place to targets, by a key (as a road's id), after putting back the key's last fit: samples
/// (x and z indices on its grid), each's height (in the world) and weight (0 to 1), a layer to paint (below 0 none) and
/// where to paint it. Whether anything changed. Written to its files in the editor.</item>
/// <item><c>Terrain.Unfit</c> <c>Func&lt;EntityManager, string, bool&gt;</c>: puts back what a key's fit found.</item>
/// </list>
/// </summary>
public static class TerrainApi
{
    internal static void Register()
    {
        PluginApi.Register("Terrain.HasGround", (Func<EntityManager, bool>)HasGround);
        PluginApi.Register("Terrain.HeightAt", (Func<EntityManager, Vector3, float>)HeightAt);
        PluginApi.Register("Terrain.BaseHeightAt", (Func<EntityManager, Vector3, float>)BaseHeightAt);
        PluginApi.Register("Terrain.Intersect",(Func<EntityManager, Vector3, Vector3, Vector3?>)Intersect);
        PluginApi.Register("Terrain.Grid", (Func<EntityManager, Vector3, Vector4?>)Grid);
        PluginApi.Register("Terrain.Bounds", (Func<EntityManager, Vector4?>)Bounds);
        PluginApi.Register("Terrain.Version", (Func<EntityManager, int>)Version);
        PluginApi.Register("Terrain.Fit", (Func<EntityManager, string, Vector3, int[], int[], float[], float[], int, bool[], bool>)Fit);
        PluginApi.Register("Terrain.Unfit", (Func<EntityManager, string, bool>)Unfit);
    }

    private static IReadOnlyCollection<TerrainComponent> Terrains(EntityManager scene) =>
        scene.GetProcessor<TerrainProcessor>()?.Terrains ?? (IReadOnlyCollection<TerrainComponent>)Array.Empty<TerrainComponent>();

    private static bool HasGround(EntityManager scene) => Terrains(scene).Any(t => t.Data is { Locations.Count: > 0 });

    private static float HeightAt(EntityManager scene, Vector3 at)
    {
        foreach (var terrain in Terrains(scene))
        {
            var height = terrain.HeightAt(at);
            if (!float.IsNaN(height))
                return height;
        }
        return float.NaN;
    }

    private static float BaseHeightAt(EntityManager scene, Vector3 at)
    {
        foreach (var terrain in Terrains(scene))
        {
            if (terrain.Data is not { } data)
                continue;
            var local = terrain.ToLocal(at);
            var height = data.HeightAt(local, (x, z) => terrain.Fits.BaseHeightOf(data, x, z));
            if (!float.IsNaN(height))
                return height + terrain.Origin.Y;
        }
        return float.NaN;
    }

    private static Vector3? Intersect(EntityManager scene, Vector3 from, Vector3 direction)
    {
        Vector3? nearest = null;
        foreach (var terrain in Terrains(scene))
        {
            if (terrain.Intersect(from, direction) is { } hit && (nearest is not { } had || Vector3.DistanceSquared(from, hit) < Vector3.DistanceSquared(from, had)))
                nearest = hit;
        }
        return nearest;
    }

    /// <summary>The terrain with ground under a place, else its first with any.</summary>
    private static TerrainComponent? TerrainAt(EntityManager scene, Vector3 at)
    {
        var terrains = Terrains(scene);
        return terrains.FirstOrDefault(t => !float.IsNaN(t.HeightAt(at))) ?? terrains.FirstOrDefault(t => t.Data != null);
    }

    private static Vector4? Grid(EntityManager scene, Vector3 at) =>
        TerrainAt(scene, at) is { Data: { } data } terrain ? new Vector4(terrain.Origin, data.VertexSpacing) : null;

    private static Vector4? Bounds(EntityManager scene)
    {
        Vector4? bounds = null;
        foreach (var terrain in Terrains(scene))
        {
            if (terrain.Data is not { Locations.Count: > 0 } data)
                continue;
            var width = data.RegionWidth;
            var origin = terrain.Origin;
            var these = new Vector4(origin.X + data.Locations.Min(l => l.X) * width, origin.Z + data.Locations.Min(l => l.Z) * width,
                origin.X + (data.Locations.Max(l => l.X) + 1) * width, origin.Z + (data.Locations.Max(l => l.Z) + 1) * width);
            bounds = bounds is { } had ? new Vector4(MathF.Min(had.X, these.X), MathF.Min(had.Y, these.Y), MathF.Max(had.Z, these.Z), MathF.Max(had.W, these.W)) : these;
        }
        return bounds;
    }

    private static int Version(EntityManager scene)
    {
        var hash = new HashCode();
        foreach (var terrain in Terrains(scene))
        {
            hash.Add(terrain.Data);
            hash.Add(terrain.Version);
            hash.Add(terrain.Origin);
        }
        return hash.ToHashCode();
    }

    private static bool Fit(EntityManager scene, string key, Vector3 at, int[] xs, int[] zs, float[] heights, float[] weights, int layer, bool[] paint)
    {
        if (TerrainAt(scene, at) is not { Data: { } data } terrain)
            return false;
        var origin = terrain.Origin.Y;
        var samples = new (int X, int Z)[xs.Length];
        var local = new float[xs.Length];
        for (var i = 0; i < xs.Length; i++)
            (samples[i], local[i]) = ((xs[i], zs[i]), heights[i] - origin);
        return Changed(terrain, terrain.Fits.Fit(data, key, samples, local, weights, layer, paint));
    }

    private static bool Unfit(EntityManager scene, string key)
    {
        var any = false;
        foreach (var terrain in Terrains(scene))
        {
            if (terrain.Data is { } data)
                any |= Changed(terrain, terrain.Fits.Unfit(data, key));
        }
        return any;
    }

    private static bool Changed(TerrainComponent terrain, HashSet<TerrainRegion> regions)
    {
        if (regions.Count == 0)
            return false;
        terrain.MarkFitted(regions);
        return true;
    }
}

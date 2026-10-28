using System;
using System.Collections.Generic;

namespace Stride.Terrain;

/// <summary>
/// A terrain's ground fitted to things on it (as roads), each by a key: what each fit found under it, so fitting it
/// again (as its road moves) first puts that back, and taking it off leaves the ground as it was. Kept while its terrain
/// is loaded, not in its files: a fit made in an earlier session is not put back.
/// </summary>
public sealed class TerrainFits
{
    private readonly Dictionary<string, Dictionary<(int X, int Z), (float Height, uint Control)>> before = new(StringComparer.Ordinal);

    /// <summary>
    /// Fits the ground to this key's targets, after putting back what its last fit found: each sample to its height
    /// (from the terrain's origin) by its weight (0 none, 1 all of it), and painted with <paramref name="layer"/> where
    /// asked (none if below 0). Samples outside its regions are left out. Gives the regions changed.
    /// </summary>
    public HashSet<TerrainRegion> Fit(TerrainData data, string key, IReadOnlyList<(int X, int Z)> samples, IReadOnlyList<float> heights,
        IReadOnlyList<float> weights, int layer, IReadOnlyList<bool> paint)
    {
        var changed = Unfit(data, key);
        var had = new Dictionary<(int X, int Z), (float Height, uint Control)>();
        for (var i = 0; i < samples.Count; i++)
        {
            var (x, z) = samples[i];
            var (region, index) = data.Sample(x, z);
            if (region == null)
                continue;
            var (height, control) = (region.Heights[index], region.Control[index]);
            had.TryAdd((x, z), (height, control));
            var weight = Math.Clamp(weights[i], 0f, 1f);
            var fitted = height + (heights[i] - height) * weight;
            var painted = layer >= 0 && paint[i] ? TerrainControl.WithTextures(TerrainControl.With(control, TerrainControl.AutoBit, false), layer, layer, 0f) : control;
            if (fitted == height && painted == control)
                continue;
            region.Heights[index] = fitted;
            region.Control[index] = painted;
            region.Modified = true;
            changed.Add(region);
        }
        if (had.Count > 0)
            before[key] = had;
        return changed;
    }

    /// <summary>Puts back what this key's fit found under it, and forgets it. Gives the regions changed.</summary>
    public HashSet<TerrainRegion> Unfit(TerrainData data, string key)
    {
        var changed = new HashSet<TerrainRegion>();
        if (!before.Remove(key, out var had))
            return changed;
        foreach (var ((x, z), (height, control)) in had)
        {
            var (region, index) = data.Sample(x, z);
            if (region == null || (region.Heights[index] == height && region.Control[index] == control))
                continue;
            region.Heights[index] = height;
            region.Control[index] = control;
            region.Modified = true;
            changed.Add(region);
        }
        return changed;
    }

    /// <summary>A sample's height as it was before any fit (as the first fit over it found it), or its height now.</summary>
    public float BaseHeightOf(TerrainData data, int x, int z)
    {
        foreach (var had in before.Values)
        {
            if (had.TryGetValue((x, z), out var found))
                return found.Height;
        }
        return data.HeightOf(x, z);
    }

    /// <summary>Whether a fit is kept by this key.</summary>
    public bool Has(string key) => before.ContainsKey(key);
}

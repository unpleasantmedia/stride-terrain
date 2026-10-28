using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stride.Terrain;

/// <summary>What a terrain brush does to the ground under it.</summary>
public enum TerrainBrushKind
{
    /// <summary>Raises it (inverted: lowers it), by its strength in metres a second.</summary>
    Raise,
    /// <summary>Lowers it (inverted: raises it).</summary>
    Lower,
    /// <summary>Evens it out: each sample towards the mean of it and its neighbours.</summary>
    Smooth,
    /// <summary>Brings it to the height where the stroke started.</summary>
    Flatten,
    /// <summary>Brings it to a set height.</summary>
    SetHeight,
    /// <summary>Paints a texture onto it.</summary>
    Paint,
    /// <summary>Opens holes in it (inverted: closes them).</summary>
    Holes,
}

/// <summary>How a brush is set: its size, falloff and strength, and what it paints or sets the ground to.</summary>
public readonly record struct TerrainBrushSettings(float Radius = 10f, float Falloff = 0.5f, float Strength = 5f, int Texture = 0, float Height = 0f);

/// <summary>
/// One stroke of a brush, from the mouse going down to it coming up: the brush works where it is for as long as it is
/// held, strongest at its middle and fading to nothing at its edge. Keeps what each sample it touched was before, for
/// it to be undone as one step. Sculpting or painting where the terrain has no region yet makes one there.
/// </summary>
public sealed class TerrainStroke
{
    private readonly TerrainData data;

    public TerrainStroke(TerrainData data, TerrainBrushKind kind, bool inverted, float startHeight)
    {
        this.data = data;
        Kind = kind;
        Inverted = inverted;
        FlattenTo = startHeight;
    }

    public TerrainBrushKind Kind { get; }

    /// <summary>Whether it does the opposite (ctrl held as it began): lowers for raise, closes holes for holes.</summary>
    public bool Inverted { get; }

    /// <summary>The height the flatten brush brings the ground to: where the stroke started.</summary>
    public float FlattenTo { get; }

    /// <summary>What each sample it has touched was before it, by sample.</summary>
    public Dictionary<(int X, int Z), float> HeightsBefore { get; } = new();
    public Dictionary<(int X, int Z), uint> ControlBefore { get; } = new();

    /// <summary>The regions it made, where there were none.</summary>
    public List<(int X, int Z)> Added { get; } = new();

    /// <summary>
    /// The brush's work for this long, at a place (in the terrain's space). Gives the regions it changed, and the block
    /// of samples it changed (none if nothing).
    /// </summary>
    public (HashSet<TerrainRegion> Regions, (int X0, int Z0, int X1, int Z1)? Samples) Work(Vector3 at, float seconds, TerrainBrushSettings brush)
    {
        var spacing = data.VertexSpacing;
        var radius = MathF.Max(brush.Radius, spacing);
        var reach = (int)MathF.Ceiling(radius / spacing);
        var (mx, mz) = (at.X / spacing, at.Z / spacing);
        var (x0, z0) = ((int)MathF.Floor(mx) - reach, (int)MathF.Floor(mz) - reach);
        var (x1, z1) = (x0 + 2 * reach + 1, z0 + 2 * reach + 1);
        if (Kind != TerrainBrushKind.Holes)
            AddRegions(x0, z0, x1, z1);

        // How much the brush works at a sample: all of it inside the falloff, easing to nothing at its edge.
        var fade = Math.Clamp(brush.Falloff, 0.001f, 1f);
        float Falloff(int x, int z)
        {
            var distance = MathF.Sqrt((x - mx) * (x - mx) + (z - mz) * (z - mz)) * spacing / radius;
            if (distance >= 1f)
                return 0f;
            if (distance <= 1f - fade)
                return 1f;
            return 0.5f + 0.5f * MathF.Cos((distance - (1f - fade)) / fade * MathF.PI);
        }
        // How far towards a target a sample moves now: by strength a second, at most all the way.
        float Towards(float falloff) => Math.Clamp(falloff * brush.Strength * seconds, 0f, 1f);

        var block = TerrainData.Block(x0, z0, x1, z1);
        switch (Kind)
        {
            case TerrainBrushKind.Raise or TerrainBrushKind.Lower:
                var sign = (Kind == TerrainBrushKind.Raise) != Inverted ? 1f : -1f;
                return Heights(block, (x, z, h) => h + sign * brush.Strength * seconds * Falloff(x, z));
            case TerrainBrushKind.Smooth:
                // Towards the mean of the sample and its eight neighbours, as they were before this touch.
                var before = new Dictionary<(int X, int Z), float>();
                foreach (var (x, z) in TerrainData.Block(x0 - 1, z0 - 1, x1 + 1, z1 + 1))
                    before[(x, z)] = data.HeightOf(x, z);
                return Heights(block, (x, z, h) =>
                {
                    var (sum, count) = (0f, 0);
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var n = before[(x + dx, z + dz)];
                            if (float.IsNaN(n))
                                continue;
                            sum += n;
                            count++;
                        }
                    }
                    return count == 0 ? h : Lerp(h, sum / count, Towards(Falloff(x, z)));
                });
            case TerrainBrushKind.Flatten:
                return Heights(block, (x, z, h) => Lerp(h, FlattenTo, Towards(Falloff(x, z))));
            case TerrainBrushKind.SetHeight:
                return Heights(block, (x, z, h) => Lerp(h, brush.Height, Towards(Falloff(x, z))));
            case TerrainBrushKind.Paint:
                return Control(block, (x, z, c) => Painted(c, brush.Texture, Falloff(x, z) * Math.Clamp(brush.Strength * seconds * 4f, 0f, 1f), Falloff(x, z)));
            case TerrainBrushKind.Holes:
                return Control(block, (x, z, c) => Falloff(x, z) > 0.3f ? TerrainControl.With(c, TerrainControl.HoleBit, !Inverted) : c);
            default:
                return (new HashSet<TerrainRegion>(), null);
        }
    }

    /// <summary>
    /// A sample painted with a texture this much: off the auto-shader, with the texture blended in over what it had (as
    /// its overlay), until it is all that texture (its base). A sample still under the auto-shader (whose textures only
    /// the shader knows) takes the texture outright where the brush is at least half strength (by its falloff).
    /// </summary>
    public static uint Painted(uint control, int id, float amount, float falloff)
    {
        if (amount <= 0f)
            return control;
        var manual = TerrainControl.With(control, TerrainControl.AutoBit, false);
        int first = TerrainControl.Base(control), second = TerrainControl.Overlay(control);
        var blend = TerrainControl.Blend(control);
        // On the auto-shader, or already this texture underneath: it all goes to the texture.
        if (TerrainControl.IsAuto(control) || first == id && (second == id || blend <= 0f))
            return falloff >= 0.5f || !TerrainControl.IsAuto(control) ? TerrainControl.WithTextures(manual, id, id, 0f) : control;
        if (second != id)
        {
            // The texture comes in over whichever of the two shows most now.
            first = blend < 0.5f ? first : second;
            second = id;
            blend = 0f;
        }
        blend = MathF.Min(blend + amount, 1f);
        return blend >= 1f ? TerrainControl.WithTextures(manual, id, id, 0f) : TerrainControl.WithTextures(manual, first, second, blend);
    }

    /// <summary>Regions over a block of samples where there are none, made flat.</summary>
    private void AddRegions(int x0, int z0, int x1, int z1)
    {
        var (r0, r1) = (data.RegionOf(x0, z0), data.RegionOf(x1, z1));
        for (var rz = r0.Z; rz <= r1.Z; rz++)
        {
            for (var rx = r0.X; rx <= r1.X; rx++)
            {
                if (data.Region(rx, rz) != null)
                    continue;
                data.Add(rx, rz);
                Added.Add((rx, rz));
            }
        }
    }

    private (HashSet<TerrainRegion>, (int, int, int, int)?) Heights(IEnumerable<(int X, int Z)> block, Func<int, int, float, float> change) =>
        data.Edit(block, (x, z, region, index) =>
        {
            var was = region.Heights[index];
            var now = change(x, z, was);
            if (now == was)
                return false;
            HeightsBefore.TryAdd((x, z), was);
            region.Heights[index] = now;
            return true;
        });

    private (HashSet<TerrainRegion>, (int, int, int, int)?) Control(IEnumerable<(int X, int Z)> block, Func<int, int, uint, uint> change) =>
        data.Edit(block, (x, z, region, index) =>
        {
            var was = region.Control[index];
            var now = change(x, z, was);
            if (now == was)
                return false;
            ControlBefore.TryAdd((x, z), was);
            region.Control[index] = now;
            return true;
        });

    /// <summary>Sets samples' heights back (or on again): for undo and redo. Gives the regions changed and the block of samples.</summary>
    public static (HashSet<TerrainRegion>, (int X0, int Z0, int X1, int Z1)?) SetHeights(TerrainData data, IReadOnlyDictionary<(int X, int Z), float> heights) =>
        data.Edit(heights.Keys, (x, z, region, index) =>
        {
            var now = heights[(x, z)];
            if (region.Heights[index] == now)
                return false;
            region.Heights[index] = now;
            return true;
        });

    /// <summary>Sets samples' control words back (or on again), as <see cref="SetHeights"/>.</summary>
    public static (HashSet<TerrainRegion>, (int X0, int Z0, int X1, int Z1)?) SetControl(TerrainData data, IReadOnlyDictionary<(int X, int Z), uint> control) =>
        data.Edit(control.Keys, (x, z, region, index) =>
        {
            var now = control[(x, z)];
            if (region.Control[index] == now)
                return false;
            region.Control[index] = now;
            return true;
        });

    /// <summary>What the samples it touched are now, to redo it.</summary>
    public Dictionary<(int X, int Z), float> HeightsAfter() => HeightsBefore.Keys.ToDictionary(p => p, p => data.HeightOf(p.X, p.Z));

    public Dictionary<(int X, int Z), uint> ControlAfter() => ControlBefore.Keys.ToDictionary(p => p, p => data.ControlOf(p.X, p.Z));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

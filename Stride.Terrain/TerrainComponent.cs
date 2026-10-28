using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using NVector3 = System.Numerics.Vector3;

namespace Stride.Terrain;

/// <summary>
/// A terrain: ground of square regions of samples (<see cref="RegionSize"/> across, <see cref="VertexSpacing"/> metres
/// apart), each in a file of its own in <see cref="Folder"/>, drawn round the camera as a clipmap and textured by its
/// control map (see <see cref="TerrainProcessor"/>), and collided with in the game. Its sample (0, 0) is where its
/// entity is; it is not turned or scaled with it. In the editor, its brushes sculpt and paint it; sculpting where it has
/// no region yet makes one.
/// </summary>
/// <summary>What a terrain draws in place of its material (see <c>TerrainDebugColor.sdsl</c>).</summary>
public enum TerrainDebugView
{
    None,

    /// <summary>Green unpainted, red painted, blue no region, magenta a hole.</summary>
    Control,

    /// <summary>Black flat to white upright.</summary>
    Slope,

    /// <summary>Red at 0 m, through yellow, green and blue to 500 m, banded every 50 m.</summary>
    Height,
}

[DataContract("TerrainComponent")]
[Display("Terrain", Expand = ExpandRule.Once)]
[ComponentCategory("Terrain")]
[ComponentOrder(30100)]
[DefaultEntityComponentProcessor(typeof(TerrainProcessor), ExecutionMode = ExecutionMode.Runtime | ExecutionMode.Editor)]
public sealed class TerrainComponent : EntityComponent, ITerrainGround
{
    /// <summary>The folder its region files are in, under the project's assets (as an asset url, as Terrain/Main).</summary>
    [DataMember(0), DefaultValue("Terrain")]
    public string Folder { get; set; } = "Terrain";

    /// <summary>How many samples across a region is. Not to be changed once it has regions.</summary>
    [DataMember(10), DataMemberRange(64, 2048, 64, 256, 0), DefaultValue(512)]
    public int RegionSize { get; set; } = 512;

    /// <summary>How many metres apart its samples are. Not to be changed once it has regions.</summary>
    [DataMember(20), DataMemberRange(0.25, 10, 0.25, 1, 2), DefaultValue(2f)]
    public float VertexSpacing { get; set; } = 2f;

    /// <summary>Which regions it has, by how many regions from its origin they are along x and z. Kept by its brushes.</summary>
    [DataMember(30)]
    public List<Int2> Regions { get; } = new();

    /// <summary>How many rings it is drawn with: each doubles how far it reaches.</summary>
    [DataMember(40), DataMemberRange(1, 12, 1, 1, 0), DefaultValue(8)]
    public int Levels { get; set; } = 8;

    /// <summary>
    /// What its ground looks like: any material, lifted to its heights automatically. For painted ground, give it Layers,
    /// each with a Terrain paint blend map of its number (the material's own look is layer 0).
    /// </summary>
    [DataMember(50)]
    public Material? Material { get; set; }

    /// <summary>Draws what its shaders read in place of its material, to find what is wrong with how it looks.</summary>
    [DataMember(55), Display("Debug view"), DefaultValue(TerrainDebugView.None)]
    public TerrainDebugView DebugView { get; set; }

    /// <summary>Whether it collides in the game.</summary>
    [DataMember(70), DefaultValue(true)]
    public bool Collision { get; set; } = true;

    // Its brushes' settings, for sculpting and painting it in the editor.
    [DataMember(100), Display("Radius", category: "Brush"), DataMemberRange(0.5, 500, 0.5, 5, 1), DefaultValue(10f)]
    public float BrushRadius { get; set; } = 10f;

    /// <summary>How much of the brush, from its edge in, fades out.</summary>
    [DataMember(110), Display("Falloff", category: "Brush"), DataMemberRange(0, 1, 0.01, 0.1, 2), DefaultValue(0.5f)]
    public float BrushFalloff { get; set; } = 0.5f;

    /// <summary>How hard it works: metres a second to raise or lower, or how fast it brings the ground to a height or paints.</summary>
    [DataMember(120), Display("Strength", category: "Brush"), DataMemberRange(0.01, 100, 0.1, 1, 2), DefaultValue(5f)]
    public float BrushStrength { get; set; } = 5f;

    /// <summary>The layer of its material painting paints with (0 for the material's own look).</summary>
    [DataMember(130), Display("Paint layer", category: "Brush"), DataMemberRange(0, 31, 1, 1, 0), DefaultValue(0)]
    public int PaintLayer { get; set; }

    /// <summary>The height Set Height brings the ground to, in metres in the world (as the sea and its material's layers are).</summary>
    [DataMember(140), Display("Set height", category: "Brush"), DefaultValue(0f)]
    public float SetHeight { get; set; }

    /// <summary>
    /// A height map to make its ground from: a 16-bit PNG, or a square 16-bit RAW (.raw, .r16) file, little-endian. Its
    /// path on disk, whole or from the scene's folder.
    /// </summary>
    [DataMember(200), Display("Height map", category: "Import")]
    public string? HeightMap { get; set; }

    /// <summary>The height black is imported at, in metres above its origin.</summary>
    [DataMember(210), Display("Lowest", category: "Import"), DefaultValue(0f)]
    public float ImportLowest { get; set; }

    /// <summary>The height white is imported at, in metres above its origin.</summary>
    [DataMember(220), Display("Highest", category: "Import"), DefaultValue(100f)]
    public float ImportHighest { get; set; } = 100f;

    /// <summary>
    /// Ticked, it imports <see cref="HeightMap"/> in the editor, a pixel a sample with its middle on its origin, in
    /// place of all the ground it had (its region files deleted), then unticks itself.
    /// </summary>
    [DataMember(230), Display("Import (replaces all ground)", category: "Import")]
    public bool Import { get; set; }

    /// <summary>Its brush settings, as its maths takes them (its height from its origin).</summary>
    [DataMemberIgnore]
    public TerrainBrushSettings Brush => new(BrushRadius, BrushFalloff, BrushStrength, PaintLayer, SetHeight - Origin.Y);

    /// <summary>Its ground, as last loaded: none before its processor has it.</summary>
    [DataMemberIgnore]
    public TerrainData? Data { get; internal set; }

    /// <summary>Takes this ground in place of what it had (as an import makes): drawn and collided with anew.</summary>
    public void ReplaceData(TerrainData data)
    {
        Data = data;
        Version++;
    }

    /// <summary>Regions changed since they were last put on the GPU and collided with, for its processor to take up.</summary>
    [DataMemberIgnore]
    internal HashSet<(int X, int Z)> Changed { get; } = new();

    /// <summary>Tells it (its processor) these regions of its ground changed: drawn and collided with again.</summary>
    public void MarkChanged(IEnumerable<TerrainRegion> regions)
    {
        foreach (var region in regions)
            Changed.Add((region.X, region.Z));
        Version++;
    }

    /// <summary>
    /// How many times its ground has changed (sculpted, painted, imported) since it was made: for what reads it (as a
    /// sea's shore) to tell when to read it again.
    /// </summary>
    [DataMemberIgnore]
    public int Version { get; private set; }

    /// <summary>Its ground changed in this area: x and z in metres, in the world.</summary>
    public event Action<RectangleF>? GroundChanged;

    internal void RaiseGroundChanged(RectangleF area) => GroundChanged?.Invoke(area);

    /// <summary>Where its sample (0, 0) is, in the world.</summary>
    [DataMemberIgnore]
    public Vector3 Origin => Entity?.Transform.WorldMatrix.TranslationVector ?? Vector3.Zero;

    /// <summary>A place in the world, in its own space (from its origin).</summary>
    public NVector3 ToLocal(Vector3 world) => new(world.X - Origin.X, world.Y - Origin.Y, world.Z - Origin.Z);

    public float HeightAt(Vector3 at) => Data?.HeightAt(ToLocal(at)) is { } h && !float.IsNaN(h) ? h + Origin.Y : float.NaN;

    public Vector3 NormalAt(Vector3 at) => Data?.NormalAt(ToLocal(at)) is { } n ? new Vector3(n.X, n.Y, n.Z) : new Vector3(float.NaN);

    public uint ControlAt(Vector3 at) => Data?.ControlAt(ToLocal(at)) ?? 0u;

    public Vector3? Intersect(Vector3 from, Vector3 direction)
    {
        if (Data?.Intersect(ToLocal(from), new NVector3(direction.X, direction.Y, direction.Z), Data.RegionWidth * 64f) is not { } hit)
            return null;
        return new Vector3(hit.X, hit.Y, hit.Z) + Origin;
    }

    /// <summary>Writes its regions changed since they were read or last written to their files: in the editor only.</summary>
    public void Save()
    {
        foreach (var region in Data?.Loaded.Where(r => r.Modified).ToList() ?? new List<TerrainRegion>())
            TerrainStorage.Save(Folder, region);
    }
}

/// <summary>
/// A level's ground as roads, towns and the editor's tools use it: read anywhere (places in the world), NaN where there
/// is none.
/// </summary>
public interface ITerrainGround
{
    float HeightAt(Vector3 at);

    Vector3 NormalAt(Vector3 at);

    uint ControlAt(Vector3 at);

    Vector3? Intersect(Vector3 from, Vector3 direction);
}

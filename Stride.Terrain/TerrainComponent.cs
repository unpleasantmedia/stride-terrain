using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Graphics;
using NVector3 = System.Numerics.Vector3;

namespace Stride.Terrain;

/// <summary>One of a terrain's ground textures, by its place in its list (the id the control map paints with).</summary>
[DataContract("TerrainTexture")]
public sealed class TerrainTexture
{
    /// <summary>Its colour, with its height in alpha (for blending one texture into another). All a terrain's are the same size and format.</summary>
    [DataMember(0)]
    public Texture? Albedo { get; set; }

    /// <summary>How many times it repeats a metre.</summary>
    [DataMember(10), DataMemberRange(0.001, 10, 0.01, 0.1, 3), DefaultValue(0.1f)]
    public float UvScale { get; set; } = 0.1f;

    [DataMember(20)]
    public Color3 Tint { get; set; } = new(1f, 1f, 1f);
}

/// <summary>
/// A terrain: ground of square regions of samples (<see cref="RegionSize"/> across, <see cref="VertexSpacing"/> metres
/// apart), each in a file of its own in <see cref="Folder"/>, drawn round the camera as a clipmap and textured by its
/// control map (see <see cref="TerrainProcessor"/>), and collided with in the game. Its sample (0, 0) is where its
/// entity is; it is not turned or scaled with it. In the editor, its brushes sculpt and paint it; sculpting where it has
/// no region yet makes one.
/// </summary>
[DataContract("TerrainComponent")]
[Display("Terrain", Expand = ExpandRule.Once)]
[ComponentCategory("Terrain")]
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

    /// <summary>Its ground textures: the control map paints with their places in this list.</summary>
    [DataMember(50), Display(category: "Textures")]
    public List<TerrainTexture> Textures { get; } = new();

    /// <summary>How sharp the blend between textures is, 0 to 1.</summary>
    [DataMember(60), Display(category: "Textures"), DataMemberRange(0, 1, 0.01, 0.1, 2), DefaultValue(0.5f)]
    public float BlendSharpness { get; set; } = 0.5f;

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

    /// <summary>The texture painting paints with: its place in <see cref="Textures"/>.</summary>
    [DataMember(130), Display("Paint texture", category: "Brush"), DataMemberRange(0, 31, 1, 1, 0), DefaultValue(0)]
    public int PaintTexture { get; set; }

    /// <summary>The height Set Height brings the ground to, in metres.</summary>
    [DataMember(140), Display("Set height", category: "Brush"), DefaultValue(0f)]
    public float SetHeight { get; set; }

    /// <summary>Its brush settings, as its maths takes them.</summary>
    [DataMemberIgnore]
    public TerrainBrushSettings Brush => new(BrushRadius, BrushFalloff, BrushStrength, PaintTexture, SetHeight);

    /// <summary>Its ground, as last loaded: none before its processor has it.</summary>
    [DataMemberIgnore]
    public TerrainData? Data { get; internal set; }

    /// <summary>Regions changed since they were last put on the GPU and collided with, for its processor to take up.</summary>
    [DataMemberIgnore]
    internal HashSet<(int X, int Z)> Changed { get; } = new();

    /// <summary>Tells it (its processor) these regions of its ground changed: drawn and collided with again.</summary>
    public void MarkChanged(IEnumerable<TerrainRegion> regions)
    {
        foreach (var region in regions)
            Changed.Add((region.X, region.Z));
    }

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

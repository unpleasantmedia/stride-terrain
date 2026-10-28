using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Graphics;
using Stride.Physics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain;

/// <summary>
/// Draws each <see cref="TerrainComponent"/>'s ground as a clipmap, as the Godot terrain did: a square of
/// <see cref="RingCells"/> cells each way round the camera, then <see cref="TerrainComponent.Levels"/> - 1 square rings
/// round it, each with cells twice the size of the one inside it. The rings are flat grids; the ground's material
/// (<see cref="TerrainRingFeature"/>, <c>TerrainGroundColor.sdsl</c>) lifts them to the heights it reads from the regions'
/// maps, which this puts on the GPU as texture arrays, along with the ground textures. The rings follow the camera in steps
/// of their cells' size. Regions changed (<see cref="TerrainComponent.MarkChanged"/>) are put on the GPU again, and in
/// the game, collided with again. What it makes is on an entity of its own at the scene's root, never saved.
/// </summary>
public sealed class TerrainProcessor : EntityProcessor<TerrainComponent, TerrainProcessor.Built>
{
    public const string BuiltName = "Terrain (built)";

    /// <summary>How many of its own cells each ring reaches out from its middle; the shader's morphing is set to it.</summary>
    public const int RingCells = 80;

    /// <summary>How many regions across its region map is: the terrain's origin in its middle.</summary>
    public const int RegionMapSize = 32;

    /// <summary>How many samples across each collision tile is, so an edit collides again only with the tiles it touched.</summary>
    public const int CollisionTile = 128;

    private static readonly Logger Log = GlobalLogger.GetLogger("Terrain");

    /// <summary>Where the camera is, in the editor (set by its plugin); none in the game, where it is the graphics compositor's first camera.</summary>
    public static Func<Vector3?>? EditorCamera { get; set; }

    public sealed class Built
    {
        public Entity? Holder;
        public int? State;
        public Material? Material;
        public Mesh[] Meshes = Array.Empty<Mesh>();
        public List<(Entity Ring, Model Model)> Rings = new();
        public Texture? Heights, Control, Colors, RegionMap, Albedo;
        public Dictionary<(int X, int Z), int> Layers = new();
        public Dictionary<(int X, int Z), Entity> Tiles = new();
        public Vector3? Followed;
    }

    private IGraphicsDeviceService? graphics;

    protected override void OnSystemAdd() => graphics = Services.GetService<IGraphicsDeviceService>();

    protected override Built GenerateComponentData(Entity entity, TerrainComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, TerrainComponent component, Built associatedData) => true;

    protected override void OnEntityComponentRemoved(Entity entity, TerrainComponent component, Built data)
    {
        TearDown(data);
        if (data.Holder != null)
            data.Holder.Scene = null;
        component.Data = null;
    }

    public override void Update(GameTime time)
    {
        var device = graphics?.GraphicsDevice;
        var context = Services.GetService<GraphicsContext>();
        var root = (EntityManager as SceneInstance)?.RootScene;
        if (device == null || context == null || root == null)
            return;
        var camera = Camera();
        foreach (var (terrain, data) in ComponentDatas)
        {
            var state = State(terrain);
            if (state != data.State)
            {
                data.State = state;
                Build(terrain, data, device, context.CommandList, root);
            }
            else if (terrain.Changed.Count > 0)
            {
                Upload(terrain, data, device, context.CommandList);
            }
            if (data.Material != null)
                Follow(terrain, data, camera ?? Vector3.Zero);
        }
    }

    /// <summary>Everything it is built from, hashed: its settings and textures, and which regions it has.</summary>
    private static int State(TerrainComponent terrain)
    {
        var hash = new HashCode();
        hash.Add(terrain.Folder);
        hash.Add(terrain.RegionSize);
        hash.Add(terrain.VertexSpacing);
        hash.Add(terrain.Levels);
        hash.Add(terrain.BlendSharpness);
        hash.Add(terrain.Collision);
        foreach (var region in terrain.Regions)
            hash.Add(region);
        foreach (var texture in terrain.Textures)
        {
            hash.Add(texture?.Albedo);
            hash.Add(texture?.UvScale);
            hash.Add(texture?.Tint);
        }
        // Regions made since (by its brushes, before its list has them).
        hash.Add(terrain.Data?.Locations.Count);
        return hash.ToHashCode();
    }

    private Vector3? Camera()
    {
        if (EditorCamera?.Invoke() is { } editor)
            return editor;
        var compositor = Services.GetService<SceneSystem>()?.GraphicsCompositor;
        return compositor?.Cameras.FirstOrDefault()?.Camera?.Entity?.Transform.WorldMatrix.TranslationVector;
    }

    /// <summary>Builds it all anew: its ground read (keeping regions already read, and made since), its maps, material, rings and collision.</summary>
    private void Build(TerrainComponent terrain, Built data, GraphicsDevice device, CommandList commandList, Scene root)
    {
        TearDown(data);
        var locations = terrain.Regions.Select(r => (r.X, r.Y)).ToList();
        var old = terrain.Data;
        if (old == null || old.RegionSize != terrain.RegionSize || old.VertexSpacing != terrain.VertexSpacing)
        {
            var content = Services.GetService<Stride.Core.Serialization.Contents.IContentManager>();
            var folder = terrain.Folder;
            terrain.Data = new TerrainData(terrain.RegionSize, terrain.VertexSpacing, locations, (x, z) => TerrainStorage.Load(content, folder, x, z));
        }
        else
        {
            foreach (var location in locations)
                old.Locations.Add(location);
        }
        terrain.Changed.Clear();

        if (data.Holder == null)
        {
            data.Holder = new Entity(BuiltName);
            data.Holder.Tags.Set(GeneratedEntity.OwnerKey, terrain.Entity);
            root.Entities.Add(data.Holder);
        }

        var ground = terrain.Data!;
        var regions = ground.All.ToList();
        data.Material = Material(device, commandList, terrain);
        if (data.Material == null)
            return;
        Maps(data, device, commandList, ground, regions);
        Parameters(terrain, data);

        data.Meshes = new[] { Grid(device, null) }.Concat(Enumerable.Range(0, 4).Select(o => Grid(device, new Int2(o % 2, o / 2)))).ToArray();
        for (var level = 0; level < Math.Clamp(terrain.Levels, 1, 12); level++)
        {
            var model = new Model { data.Material, data.Meshes[level == 0 ? 0 : 1] };
            var ring = new Entity($"Ring {level}") { new ModelComponent { Model = model } };
            ring.Tags.Set(GeneratedEntity.OwnerKey, terrain.Entity);
            data.Holder.AddChild(ring);
            data.Rings.Add((ring, model));
        }
        data.Followed = null;

        if (terrain.Collision && (EntityManager.ExecutionMode & ExecutionMode.Runtime) != 0)
        {
            var tiles = ground.RegionSize / CollisionTile;
            foreach (var region in regions)
            {
                for (var z = 0; z < tiles; z++)
                {
                    for (var x = 0; x < tiles; x++)
                        Collide(terrain, data, (region.X * tiles + x, region.Z * tiles + z));
                }
            }
        }
    }

    private static void TearDown(Built data)
    {
        foreach (var (ring, _) in data.Rings)
            ring.Scene = null;
        data.Holder?.Transform.Children.Clear();
        data.Rings.Clear();
        foreach (var mesh in data.Meshes)
        {
            mesh.Draw.IndexBuffer?.Buffer.Dispose();
            foreach (var binding in mesh.Draw.VertexBuffers)
                binding.Buffer.Dispose();
        }
        data.Meshes = Array.Empty<Mesh>();
        foreach (var texture in new[] { data.Heights, data.Control, data.Colors, data.RegionMap, data.Albedo })
            texture?.Dispose();
        (data.Heights, data.Control, data.Colors, data.RegionMap, data.Albedo) = (null, null, null, null, null);
        data.Layers.Clear();
        data.Tiles.Clear();
        data.Material = null;
    }

    /// <summary>
    /// Its ground's material: the rings lifted by <see cref="TerrainRingFeature"/>, coloured by <c>TerrainGroundColor</c>.
    /// </summary>
    private static Material Material(GraphicsDevice device, CommandList commandList, TerrainComponent terrain) => Rendering.Material.New(device, new MaterialDescriptor
    {
        Attributes =
        {
            Displacement = new TerrainRingFeature(),
            Diffuse = new MaterialDiffuseMapFeature(new ComputeShaderClassColor { MixinReference = "TerrainGroundColor" }),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.1f)),
            Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
            SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
        },
    });

    /// <summary>Puts its regions' heights, control and colours on the GPU, one layer a region, and the map of which layer each is.</summary>
    private static void Maps(Built data, GraphicsDevice device, CommandList commandList, TerrainData ground, List<TerrainRegion> regions)
    {
        var size = ground.RegionSize;
        var layers = Math.Max(regions.Count, 1);
        data.Heights = Texture.New2D(device, size, size, PixelFormat.R32_Float, TextureFlags.ShaderResource, layers);
        data.Control = Texture.New2D(device, size, size, PixelFormat.R32_Float, TextureFlags.ShaderResource, layers);
        data.Colors = Texture.New2D(device, size, size, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource, layers);
        var map = new float[RegionMapSize * RegionMapSize];
        for (var layer = 0; layer < regions.Count; layer++)
        {
            var region = regions[layer];
            var (mx, mz) = (region.X + RegionMapSize / 2, region.Z + RegionMapSize / 2);
            if (mx < 0 || mz < 0 || mx >= RegionMapSize || mz >= RegionMapSize)
            {
                Log.Warning($"Terrain region ({region.X}, {region.Z}) is further from its origin than {RegionMapSize / 2} regions: not drawn.");
                continue;
            }
            map[mz * RegionMapSize + mx] = layer + 1;
            data.Layers[(region.X, region.Z)] = layer;
            data.Heights.SetData(commandList, region.Heights, layer);
            data.Control.SetData(commandList, region.Control, layer);
            data.Colors.SetData(commandList, region.Colors, layer);
        }
        data.RegionMap = Texture.New2D(device, RegionMapSize, RegionMapSize, PixelFormat.R32_Float, map);
    }

    /// <summary>
    /// Its ground textures as one array: copied on the GPU, so each must be the same size, format and number of
    /// mipmaps as the first (others are left out, and said so). A white one where it has none.
    /// </summary>
    private static Texture Albedo(GraphicsDevice device, CommandList commandList, TerrainComponent terrain)
    {
        var textures = terrain.Textures.Select(t => t?.Albedo).ToList();
        var first = textures.FirstOrDefault(t => t != null);
        if (first == null)
        {
            var white = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource, 1);
            white.SetData(commandList, new[] { 0xFFFFFFFFu });
            return white;
        }
        var array = Texture.New2D(device, first.Width, first.Height, first.MipLevelCount, first.Format, TextureFlags.ShaderResource, Math.Max(textures.Count, 1));
        for (var i = 0; i < textures.Count; i++)
        {
            var texture = textures[i];
            if (texture == null || texture.Width != first.Width || texture.Height != first.Height || texture.Format != first.Format
                || texture.MipLevelCount != first.MipLevelCount || texture.ArraySize != 1)
            {
                if (texture != null)
                    Log.Warning($"Terrain texture {i} ({texture.Name}) is not the same size, format and mipmaps as the first: left out.");
                continue;
            }
            for (var mip = 0; mip < first.MipLevelCount; mip++)
                commandList.CopyRegion(texture, texture.GetSubResourceIndex(0, mip), null, array, array.GetSubResourceIndex(i, mip));
        }
        return array;
    }

    private void Parameters(TerrainComponent terrain, Built data)
    {
        var device = graphics!.GraphicsDevice;
        var commandList = Services.GetService<GraphicsContext>()!.CommandList;
        data.Albedo = Albedo(device, commandList, terrain);
        var parameters = data.Material!.Passes[0].Parameters;
        parameters.Set(TerrainMapsKeys.HeightMaps, data.Heights);
        parameters.Set(TerrainMapsKeys.ControlMaps, data.Control);
        parameters.Set(TerrainMapsKeys.ColorMaps, data.Colors);
        parameters.Set(TerrainMapsKeys.RegionMap, data.RegionMap);
        parameters.Set(TerrainMapsKeys.AlbedoMaps, data.Albedo);
        parameters.Set(TerrainMapsKeys.VertexSpacing, terrain.VertexSpacing);
        parameters.Set(TerrainMapsKeys.RegionSize, (float)terrain.RegionSize);
        parameters.Set(TerrainMapsKeys.RegionMapSize, (float)RegionMapSize);
        parameters.Set(TerrainMapsKeys.RingCells, (float)RingCells);
        parameters.Set(TerrainMapsKeys.BlendSharpness, terrain.BlendSharpness);
        var scales = new Vector4[8];
        var tints = new Vector4[32];
        for (var i = 0; i < 32; i++)
        {
            var texture = i < terrain.Textures.Count ? terrain.Textures[i] : null;
            scales[i / 4][i % 4] = texture?.UvScale ?? 0.1f;
            tints[i] = texture != null ? new Vector4(texture.Tint.R, texture.Tint.G, texture.Tint.B, 1f) : Vector4.One;
        }
        parameters.Set(TerrainMapsKeys.TextureUvScales, scales);
        parameters.Set(TerrainMapsKeys.TextureTints, tints);
    }

    /// <summary>Puts the regions changed on the GPU again (new ones build it all anew), and collides with them again.</summary>
    private void Upload(TerrainComponent terrain, Built data, GraphicsDevice device, CommandList commandList)
    {
        var ground = terrain.Data;
        var changed = terrain.Changed.ToList();
        terrain.Changed.Clear();
        if (ground == null || data.Heights == null)
            return;
        if (changed.Any(c => !data.Layers.ContainsKey(c)))
        {
            data.State = null;
            return;
        }
        foreach (var location in changed)
        {
            if (ground.Region(location.X, location.Z) is not { } region)
                continue;
            var layer = data.Layers[location];
            data.Heights.SetData(commandList, region.Heights, layer);
            data.Control!.SetData(commandList, region.Control, layer);
            data.Colors!.SetData(commandList, region.Colors, layer);
            if (data.Tiles.Count > 0)
            {
                var tiles = ground.RegionSize / CollisionTile;
                for (var z = 0; z < tiles; z++)
                {
                    for (var x = 0; x < tiles; x++)
                        Collide(terrain, data, (location.X * tiles + x, location.Z * tiles + z));
                }
            }
        }
    }

    /// <summary>Moves the rings round a place: each to its own cells' doubled step, with the hole for the ring inside it where that ring is.</summary>
    private static void Follow(TerrainComponent terrain, Built data, Vector3 camera)
    {
        var origin = terrain.Origin;
        var parameters = data.Material!.Passes[0].Parameters;
        parameters.Set(TerrainMapsKeys.TerrainOrigin, origin);
        parameters.Set(TerrainMapsKeys.TerrainCamera, camera);
        if (data.Followed is { } was && Vector3.DistanceSquared(was, camera) < 1e-4f)
            return;
        data.Followed = camera;
        var at = camera - origin;
        var inner = Vector2.Zero;
        for (var level = 0; level < data.Rings.Count; level++)
        {
            var size = terrain.VertexSpacing * (1 << level);
            var step = 2f * size;
            var middle = new Vector2(MathF.Floor(at.X / step) * step, MathF.Floor(at.Z / step) * step);
            var (ring, model) = data.Rings[level];
            if (level > 0)
            {
                var offset = new Int2((int)MathF.Round((inner.X - middle.X) / size), (int)MathF.Round((inner.Y - middle.Y) / size));
                var mesh = data.Meshes[1 + Math.Clamp(offset.X, 0, 1) + 2 * Math.Clamp(offset.Y, 0, 1)];
                if (model.Meshes[0] != mesh)
                    model.Meshes[0] = mesh;
            }
            ring.Transform.Scale = new Vector3(size, 1f, size);
            ring.Transform.Position = new Vector3(origin.X + middle.X, 0f, origin.Z + middle.Y);
            inner = middle;
        }
    }

    /// <summary>
    /// A flat grid of unit cells, <see cref="RingCells"/> each way from its middle: whole, or a ring round a hole half as
    /// wide, as far off its middle as the ring inside it can be (0 or 1 cells each way). Bounded as high and low as ground
    /// can be, as its vertices are lifted where it is drawn.
    /// </summary>
    private static Mesh Grid(GraphicsDevice device, Int2? hole)
    {
        const int across = 2 * RingCells + 1;
        var vertices = new VertexPositionNormalTexture[across * across];
        for (var z = 0; z < across; z++)
        {
            for (var x = 0; x < across; x++)
                vertices[z * across + x] = new VertexPositionNormalTexture(new Vector3(x - RingCells, 0f, z - RingCells), Vector3.UnitY, Vector2.Zero);
        }
        var indices = new List<int>();
        for (var z = 0; z < across - 1; z++)
        {
            for (var x = 0; x < across - 1; x++)
            {
                if (hole is { } h && InHole(x - RingCells, h.X) && InHole(z - RingCells, h.Y))
                    continue;
                var a = z * across + x;
                // Clockwise seen from above (x right, z towards the viewer), as Stride draws front faces.
                indices.AddRange(new[] { a, a + 1, a + across + 1, a, a + across + 1, a + across });
            }
        }
        var bounds = new BoundingBox(new Vector3(-RingCells - 2, -5000f, -RingCells - 2), new Vector3(RingCells + 2, 5000f, RingCells + 2));
        var draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.TriangleList,
            DrawCount = indices.Count,
            IndexBuffer = new IndexBufferBinding(Buffer.Index.New(device, indices.ToArray()), true, indices.Count),
            VertexBuffers = [new VertexBufferBinding(Buffer.Vertex.New(device, vertices), VertexPositionNormalTexture.Layout, vertices.Length)],
        };
        return new Mesh { Draw = draw, BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds) };
    }

    private static bool InHole(int cell, int offset) => cell >= offset - RingCells / 2 && cell < offset + RingCells / 2;

    /// <summary>
    /// Makes (or makes again) a collision tile: its <see cref="CollisionTile"/> samples each way and the first row and
    /// column of the tiles after it, so they meet, as a static mesh, its cells split as the drawn ground's are, and left
    /// open in holes.
    /// </summary>
    private static void Collide(TerrainComponent terrain, Built data, (int X, int Z) tile)
    {
        var ground = terrain.Data;
        if (ground == null || data.Holder == null)
            return;
        if (data.Tiles.Remove(tile, out var old))
            old.Scene = null;
        const int across = CollisionTile + 1;
        var vertices = new List<Vector3>(across * across);
        var heights = new float[across * across];
        var holes = new bool[across * across];
        var spacing = ground.VertexSpacing;
        for (var oz = 0; oz < across; oz++)
        {
            for (var ox = 0; ox < across; ox++)
            {
                var (sx, sz) = (tile.X * CollisionTile + ox, tile.Z * CollisionTile + oz);
                var height = ground.HeightOf(sx, sz);
                var i = oz * across + ox;
                holes[i] = float.IsNaN(height) || TerrainControl.IsHole(ground.ControlOf(sx, sz));
                vertices.Add(new Vector3(sx * spacing, float.IsNaN(height) ? 0f : height, sz * spacing));
            }
        }
        var indices = new List<int>();
        for (var z = 0; z < CollisionTile; z++)
        {
            for (var x = 0; x < CollisionTile; x++)
            {
                var a = z * across + x;
                if (holes[a] || holes[a + 1] || holes[a + across] || holes[a + across + 1])
                    continue;
                indices.AddRange(new[] { a, a + across + 1, a + 1, a, a + across, a + across + 1 });
            }
        }
        if (indices.Count == 0)
            return;
        var entity = new Entity($"Collision {tile.X}, {tile.Z}") { new StaticColliderComponent { ColliderShape = new StaticMeshColliderShape(vertices, indices) } };
        entity.Transform.Position = terrain.Origin;
        data.Holder.AddChild(entity);
        data.Tiles[tile] = entity;
    }
}

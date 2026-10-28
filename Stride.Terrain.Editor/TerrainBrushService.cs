using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.BuildEngine;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Dirtiables;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;
using NVector3 = System.Numerics.Vector3;

namespace Stride.Terrain.Editor;

/// <summary>
/// Sculpts and paints terrains in the scene editor, as the Godot terrain's editor did. With a terrain selected, the
/// viewport toolbar (as an <see cref="IEditorGameToolService"/>) has its brushes: Raise, Lower, Smooth, Flatten, Set
/// Height, Paint and Holes, one on at a time (Escape turns it off). Hold the mouse on the ground to work it: the brush
/// keeps working while held, strongest at its middle and fading to nothing at its edge (its size, falloff, strength,
/// texture and height are the terrain's Brush settings). Ctrl turns Raise into Lower (and back), and holes into filling
/// them; ctrl-click with Set Height takes the ground's height there. Sculpting where the terrain has no region yet makes
/// one. Each stroke is one step to undo, and is written to the terrain's region files as it ends. A circle on the ground
/// shows the brush, and inside it, where its falloff starts.
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController))]
public sealed class TerrainBrushService : EditorGameMouseServiceBase, IEditorGameToolService
{
    private static readonly Logger Logger = GlobalLogger.GetLogger("Terrain");

    /// <summary>How many segments the brush's circles are drawn with.</summary>
    private const int CircleSegments = 64;

    private static readonly Color CircleColor = new(1f, 0.85f, 0.2f, 1f);

    private readonly EntityHierarchyEditorViewModel editor;
    private readonly IEditorGameController controller;
    private readonly List<EditorTool> tools = new();
    private EntityHierarchyEditorGame? game;
    private IEditorGameEntitySelectionService? selection;
    private IEditorGameCameraService? camera;
    private volatile bool available;

    /// <summary>The brush on, if any.</summary>
    private TerrainBrushKind? current;

    /// <summary>A brush turned on (its index) or off (-2) by its toolbar button, on the UI thread, for the next frame; -1 for neither.</summary>
    private int requested = -1;

    private TerrainStroke? stroke;
    private TerrainComponent? stroking;
    private Entity? cursor;
    private Mesh? cursorMesh;

    public TerrainBrushService(EntityHierarchyEditorViewModel editor, IEditorGameController controller)
    {
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        foreach (var kind in Enum.GetValues<TerrainBrushKind>())
        {
            var index = (int)kind;
            tools.Add(new EditorTool($"Stride.Terrain.{kind}", $"Terrain: {Name(kind)}", TerrainEditorPlugin.LoadResource($"Terrain{kind}.png"),
                () => current == kind, active => Interlocked.Exchange(ref requested, active ? index : -2), () => available));
        }

        // A terrain's region files are under the scene's package's assets.
        TerrainStorage.DiskFolder = folder =>
            editor.Asset.AssetItem.Package is { RootDirectory: { } root } package
                ? Path.Combine(UPath.Combine(root, package.AssetFolders.FirstOrDefault()?.Path ?? new UDirectory("Assets")).ToOSPath(),
                    folder.Replace('/', Path.DirectorySeparatorChar))
                : null;
    }

    private static string Name(TerrainBrushKind kind) => kind == TerrainBrushKind.SetHeight ? "Set Height" : kind.ToString();

    public IReadOnlyList<EditorTool> Tools => tools;

    public override bool IsControllingMouse { get; protected set; }

    protected override Task<bool> Initialize(EditorServiceGame editorGame)
    {
        game = (EntityHierarchyEditorGame)editorGame;
        camera = game.EditorServices.Get<IEditorGameCameraService>();
        // The terrain follows the editor's camera.
        TerrainProcessor.EditorCamera = () => camera is { } c ? Matrix.Invert(c.ViewMatrix).TranslationVector : null;
        game.Script.AddTask(Execute);
        return Task.FromResult(true);
    }

    private async Task Execute()
    {
        MicrothreadLocalDatabases.MountCommonDatabase();
        while (game!.IsRunning)
        {
            await game.Script.NextFrame();
            try
            {
                Step((float)game.UpdateTime.Elapsed.TotalSeconds);
            }
            catch (Exception e)
            {
                Logger.Error("Terrain brush failed; the brush is off.", e);
                EndStroke();
                SetBrush(null);
            }
        }
    }

    private void Step(float seconds)
    {
        var terrain = SelectedTerrain();
        var nowAvailable = IsActive && terrain != null;
        if (nowAvailable != available)
        {
            available = nowAvailable;
            foreach (var tool in tools)
                tool.RaiseChanged();
        }
        var fromButton = Interlocked.Exchange(ref requested, -1);
        if (!available)
        {
            EndStroke();
            SetBrush(null);
            ShowCursor(null, 0f, 0f);
            return;
        }
        if (fromButton >= 0)
            SetBrush((TerrainBrushKind)fromButton);
        else if (fromButton == -2)
            SetBrush(null);

        var input = game!.Input;
        if (current != null && input.IsKeyPressed(Keys.Escape))
            SetBrush(null);
        if (current is not { } kind || terrain == null)
        {
            EndStroke();
            ShowCursor(null, 0f, 0f);
            return;
        }

        var at = Ground(terrain, input.MousePosition);
        ShowCursor(at, terrain.BrushRadius, terrain.BrushRadius * (1f - terrain.BrushFalloff));
        var ctrl = input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl);

        if (stroke == null && input.IsMouseButtonPressed(MouseButton.Left) && IsMouseAvailable && !input.IsMouseButtonDown(MouseButton.Right)
            && at is { } start && terrain.Data != null)
        {
            // Ctrl-click with Set Height takes the ground's height there.
            if (kind == TerrainBrushKind.SetHeight && ctrl)
            {
                var height = MathF.Round((start.Y - terrain.Origin.Y) * 10f) / 10f;
                editor.Dispatcher.InvokeAsync(() => SetSetting(terrain, nameof(TerrainComponent.SetHeight), height));
                return;
            }
            IsControllingMouse = true;
            stroking = terrain;
            stroke = new TerrainStroke(terrain.Data, kind, ctrl, start.Y - terrain.Origin.Y);
            Work(terrain, start, 1f / 30f);
            return;
        }
        if (stroke == null)
            return;
        if (!input.IsMouseButtonDown(MouseButton.Left))
        {
            EndStroke();
            return;
        }
        if (at is { } here && seconds > 0f)
            Work(terrain, here, MathF.Min(seconds, 0.1f));
    }

    /// <summary>The brush's work for this long, at a place in the world.</summary>
    private void Work(TerrainComponent terrain, Vector3 at, float seconds)
    {
        var (regions, _) = stroke!.Work(terrain.ToLocal(at), seconds, terrain.Brush);
        terrain.MarkChanged(regions);
    }

    /// <summary>
    /// The stroke done: one step to undo (from what the samples it touched were to what they are now), its regions written
    /// to their files, and any it made added to the terrain's list of them.
    /// </summary>
    private void EndStroke()
    {
        if (stroke == null || stroking == null)
        {
            stroke = null;
            IsControllingMouse = false;
            return;
        }
        var (done, terrain) = (stroke, stroking);
        (stroke, stroking) = (null, null);
        IsControllingMouse = false;
        terrain.Save();
        var heightsBefore = done.HeightsBefore.ToDictionary(p => p.Key, p => p.Value);
        var heightsAfter = done.HeightsAfter();
        var controlBefore = done.ControlBefore.ToDictionary(p => p.Key, p => p.Value);
        var controlAfter = done.ControlAfter();
        var added = done.Added.ToList();
        var entityId = terrain.Entity.Id;
        if (heightsBefore.Count == 0 && controlBefore.Count == 0 && added.Count == 0)
            return;
        editor.Dispatcher.InvokeAsync(() =>
        {
            using var transaction = editor.UndoRedoService.CreateTransaction();
            // Regions it made: in the terrain's list (as saved), so it is built and read with them.
            if (added.Count > 0 && editor.Asset.Asset.Hierarchy.Parts.TryGetValue(entityId, out var design)
                && design.Entity.Get<TerrainComponent>() is { } asset)
            {
                var list = editor.Session.AssetNodeContainer.GetOrCreateNode(asset)[nameof(TerrainComponent.Regions)].Target!;
                foreach (var (x, z) in added.Where(a => !asset.Regions.Contains(new Int2(a.X, a.Z))))
                    list.Add(new Int2(x, z));
            }
            editor.UndoRedoService.PushOperation(new StrokeOperation(this, entityId, heightsBefore, heightsAfter, controlBefore, controlAfter));
            editor.UndoRedoService.SetName(transaction, $"Terrain: {Name(done.Kind)}");
        });
    }

    /// <summary>Puts samples back as they were (or on again), on the game's side, and writes them to their files.</summary>
    private void Apply(Guid entityId, IReadOnlyDictionary<(int X, int Z), float> heights, IReadOnlyDictionary<(int X, int Z), uint> control)
    {
        controller.InvokeAsync(() =>
        {
            if (FindTerrain(entityId) is not { Data: { } data } terrain)
                return;
            var (changed, _) = TerrainStroke.SetHeights(data, heights);
            var (changedControl, _) = TerrainStroke.SetControl(data, control);
            changed.UnionWith(changedControl);
            terrain.MarkChanged(changed);
            terrain.Save();
        });
    }

    /// <summary>A stroke, to undo and redo.</summary>
    private sealed class StrokeOperation : DirtyingOperation
    {
        private readonly TerrainBrushService service;
        private readonly Guid entityId;
        private readonly IReadOnlyDictionary<(int X, int Z), float> heightsBefore, heightsAfter;
        private readonly IReadOnlyDictionary<(int X, int Z), uint> controlBefore, controlAfter;

        public StrokeOperation(TerrainBrushService service, Guid entityId, IReadOnlyDictionary<(int X, int Z), float> heightsBefore,
            IReadOnlyDictionary<(int X, int Z), float> heightsAfter, IReadOnlyDictionary<(int X, int Z), uint> controlBefore,
            IReadOnlyDictionary<(int X, int Z), uint> controlAfter)
            : base(service.editor.Asset.Dirtiables)
        {
            (this.service, this.entityId) = (service, entityId);
            (this.heightsBefore, this.heightsAfter, this.controlBefore, this.controlAfter) = (heightsBefore, heightsAfter, controlBefore, controlAfter);
        }

        protected override void Undo() => service.Apply(entityId, heightsBefore, controlBefore);

        protected override void Redo() => service.Apply(entityId, heightsAfter, controlAfter);
    }

    /// <summary>Sets one of a terrain's settings (asset side), as one step to undo.</summary>
    private void SetSetting(TerrainComponent terrain, string name, object value)
    {
        if (!editor.Asset.Asset.Hierarchy.Parts.TryGetValue(terrain.Entity.Id, out var design) || design.Entity.Get<TerrainComponent>() is not { } asset)
            return;
        using var transaction = editor.UndoRedoService.CreateTransaction();
        editor.Session.AssetNodeContainer.GetOrCreateNode(asset)[name].Update(value);
        editor.UndoRedoService.SetName(transaction, $"Terrain: {name}");
    }

    private void SetBrush(TerrainBrushKind? brush)
    {
        if (current == brush)
            return;
        EndStroke();
        current = brush;
        foreach (var tool in tools)
            tool.RaiseChanged();
    }

    /// <summary>The terrain selected (its entity, or one of its children's), game side.</summary>
    private TerrainComponent? SelectedTerrain()
    {
        selection ??= game!.EditorServices.Get<IEditorGameEntitySelectionService>();
        if (selection == null)
            return null;
        foreach (var id in selection.GetSelectedIds().ToList())
        {
            for (var entity = FindEntity(id); entity != null; entity = entity.GetParent())
            {
                if (entity.Get<TerrainComponent>() is { } terrain)
                    return terrain;
            }
        }
        return null;
    }

    private TerrainComponent? FindTerrain(Guid entityId) => FindEntity(new AbsoluteId(editor.Asset.Id, entityId))?.Get<TerrainComponent>();

    private Entity? FindEntity(AbsoluteId id)
    {
        try
        {
            return controller.FindGameSidePart(id) as Entity;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the mouse is on the terrain's ground, in the world; where it has none (none yet, or past its regions), on the
    /// level of its origin, where sculpting makes new ground.
    /// </summary>
    private Vector3? Ground(TerrainComponent terrain, Vector2 mouse)
    {
        if (RayAt(mouse) is not { } ray)
            return null;
        if (terrain.Intersect(ray.Position, ray.Direction) is { } hit)
            return hit;
        var plane = new Plane(terrain.Origin, Vector3.UnitY);
        return ray.Intersects(in plane, out Vector3 point) ? point : null;
    }

    /// <summary>The camera's ray through this place in the viewport, as the editor makes it.</summary>
    private Ray? RayAt(Vector2 mouse)
    {
        if (camera?.Component is not { } cam)
            return null;
        var view = Matrix.Invert(camera.ViewMatrix);
        var screen = new Vector2(2f * (mouse.X - 0.5f), -2f * (mouse.Y - 0.5f));
        if (cam.Projection == CameraProjectionMode.Perspective)
        {
            var fov = MathUtil.DegreesToRadians(cam.VerticalFieldOfView);
            var direction = Vector3.Normalize(new Vector3(cam.AspectRatio * screen.X, screen.Y, -1 / MathF.Tan(fov / 2f)));
            return new Ray(view.TranslationVector, Vector3.Normalize(Vector3.TransformNormal(direction, view)));
        }
        var half = cam.OrthographicSize / 2f;
        var origin = Vector3.TransformCoordinate(new Vector3(screen.X * cam.AspectRatio * half, screen.Y * half, 0), view);
        return new Ray(origin, Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, view)));
    }

    /// <summary>The brush's circles on the ground round a place (hidden for none): its edge, and where its falloff starts.</summary>
    private void ShowCursor(Vector3? at, float radius, float inner)
    {
        if (game == null)
            return;
        if (at is not { } centre || SelectedTerrain() is not { } terrain)
        {
            if (cursor != null)
                cursor.Get<ModelComponent>()!.Enabled = false;
            return;
        }
        if (cursor == null)
        {
            cursorMesh = new Mesh { Draw = new MeshDraw { PrimitiveType = PrimitiveType.LineList } };
            var material = GizmoEmissiveColorMaterial.Create(game.GraphicsDevice, CircleColor);
            cursor = new Entity("Terrain brush") { new ModelComponent { Model = new Model { material, cursorMesh }, RenderGroup = GizmoBase.DefaultGroup } };
            game.EditorScene.Entities.Add(cursor);
        }
        var points = new List<Vector3>();
        foreach (var r in inner > 0.01f ? new[] { radius, inner } : new[] { radius })
        {
            for (var i = 0; i < CircleSegments; i++)
            {
                foreach (var k in new[] { i, i + 1 })
                {
                    var angle = k * MathF.Tau / CircleSegments;
                    var p = centre + new Vector3(MathF.Cos(angle) * r, 0f, MathF.Sin(angle) * r);
                    var h = terrain.HeightAt(p);
                    points.Add(p with { Y = (float.IsNaN(h) ? centre.Y : h) + 0.15f });
                }
            }
        }
        var draw = cursorMesh!.Draw;
        draw.IndexBuffer?.Buffer.Dispose();
        foreach (var binding in draw.VertexBuffers ?? Array.Empty<VertexBufferBinding>())
            binding.Buffer.Dispose();
        var vertices = points.Select(p => new VertexPositionNormalTexture(p, Vector3.UnitY, Vector2.Zero)).ToArray();
        draw.VertexBuffers = [new VertexBufferBinding(Buffer.Vertex.New(game.GraphicsDevice, vertices), VertexPositionNormalTexture.Layout, vertices.Length)];
        draw.IndexBuffer = null;
        draw.DrawCount = vertices.Length;
        var bounds = BoundingBox.FromPoints(points.ToArray());
        cursorMesh.BoundingBox = bounds;
        cursorMesh.BoundingSphere = BoundingSphere.FromBox(bounds);
        cursor.Get<ModelComponent>()!.Enabled = true;
    }
}

using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Engine;
using Stride.Engine.Gizmos;

namespace Stride.Terrain.Editor;

/// <summary>A billboard at a terrain's entity (its origin) in the scene editor, so it can be found and picked to sculpt it.</summary>
[GizmoComponent(typeof(TerrainComponent), true)]
public class TerrainComponentGizmo : BillboardingGizmo<TerrainComponent>
{
    public TerrainComponentGizmo(EntityComponent component)
        : base(component, "Terrain", TerrainEditorPlugin.LoadResource("TerrainGizmo.png"))
    {
    }
}

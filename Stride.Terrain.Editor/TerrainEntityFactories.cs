using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.EntityFactories;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core;
using Stride.Engine;

namespace Stride.Terrain.Editor;

/// <summary>Add entity > Terrain > Terrain: one with no regions yet, for its brushes to make them.</summary>
[Display(10, "Terrain", "Terrain")]
public class TerrainEntityFactory : EntityFactory
{
    public override Task<Entity> CreateEntity(EntityHierarchyItemViewModel parent) =>
        CreateEntityWithComponent(ComputeNewName(parent, "Terrain"), new TerrainComponent());
}

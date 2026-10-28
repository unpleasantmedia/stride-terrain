using Stride.Core;
using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Stride.Terrain;

/// <summary>
/// The terrain ground material's displacement: its rings' vertices lifted to the ground's heights, and turned with its
/// slope (<c>TerrainRingDisplacement.sdsl</c>), in place of the flat grids they are.
/// </summary>
[DataContract("TerrainRingFeature")]
[Display("Terrain rings")]
public sealed class TerrainRingFeature : MaterialFeature, IMaterialDisplacementFeature
{
    public override void GenerateShader(MaterialGeneratorContext context) =>
        context.SetStreamFinalModifier<TerrainRingFeature>(MaterialShaderStage.Vertex, new ShaderClassSource("TerrainRingDisplacement"));
}

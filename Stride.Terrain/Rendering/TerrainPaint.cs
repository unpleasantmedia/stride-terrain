using System.ComponentModel;
using System.Globalization;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Terrain;

/// <summary>
/// A terrain material's layer's blend map: where its terrain's ground is unpainted, where the ground's height and slope
/// are in its ranges (its edges fading over their fades); where it is painted, where it is painted with this layer (its
/// number, as the terrain's Paint layer brush setting paints with; 0 is the material's own look). Layers later in the
/// material are over those before them, so put the ones that win (as rock on steep slopes) last. See
/// <c>TerrainLayerWeight.sdsl</c>.
/// </summary>
[DataContract("TerrainPaint")]
[Display("Terrain layer")]
public sealed class TerrainPaint : ComputeNode, IComputeScalar
{
    /// <summary>The layer's number, as painting paints with it.</summary>
    [DataMember(10), DataMemberRange(0, 31, 1, 1, 0)]
    public int Layer { get; set; } = 1;

    /// <summary>The lowest ground it is on, in metres in the world.</summary>
    [DataMember(20), Display("Min height", category: "Height"), DefaultValue(-10000f)]
    public float MinHeight { get; set; } = -10000f;

    /// <summary>The highest ground it is on, in metres in the world.</summary>
    [DataMember(30), Display("Max height", category: "Height"), DefaultValue(10000f)]
    public float MaxHeight { get; set; } = 10000f;

    /// <summary>How many metres past its heights it fades out over.</summary>
    [DataMember(40), Display("Height fade", category: "Height"), DataMemberRange(0, 1000, 0.5, 5, 1), DefaultValue(5f)]
    public float HeightFade { get; set; } = 5f;

    /// <summary>The flattest ground it is on, in degrees (0 is flat).</summary>
    [DataMember(50), Display("Min slope", category: "Slope"), DataMemberRange(0, 90, 1, 5, 1), DefaultValue(0f)]
    public float MinSlope { get; set; }

    /// <summary>The steepest ground it is on, in degrees.</summary>
    [DataMember(60), Display("Max slope", category: "Slope"), DataMemberRange(0, 90, 1, 5, 1), DefaultValue(90f)]
    public float MaxSlope { get; set; } = 90f;

    /// <summary>How many degrees past its slopes it fades out over.</summary>
    [DataMember(70), Display("Slope fade", category: "Slope"), DataMemberRange(0, 45, 0.5, 5, 1), DefaultValue(5f)]
    public float SlopeFade { get; set; } = 5f;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys) =>
        new ShaderClassSource("TerrainLayerWeight", Layer, Number(MinHeight), Number(MaxHeight), Number(HeightFade), Number(MinSlope), Number(MaxSlope), Number(SlopeFade));

    // As the shader's code takes it, whatever the culture.
    private static string Number(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}

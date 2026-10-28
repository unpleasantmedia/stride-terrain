using System;
using System.IO;
using System.IO.Compression;

namespace Stride.Terrain;

/// <summary>
/// One square region of a terrain, <see cref="Size"/> samples across, in a file of its own: its heights (one float a
/// sample), its control (one 32-bit word a sample: see <see cref="TerrainControl"/>), and its colour (a tint and
/// wetness a sample, packed RGBA as bytes). Rows run along x, one after another along z.
/// </summary>
public sealed class TerrainRegion
{
    /// <summary>What its files begin with, and the version of their layout.</summary>
    private const uint Magic = 0x4E525453; // "STRN"
    private const int Version = 1;

    public TerrainRegion(int x, int z, int size)
    {
        X = x;
        Z = z;
        Size = size;
        Heights = new float[size * size];
        Control = new uint[size * size];
        Colors = new uint[size * size];
        Array.Fill(Colors, 0xFFFFFFFFu);
    }

    /// <summary>Which region it is: its corner is this many regions from the terrain's origin, along x and z.</summary>
    public int X { get; }
    public int Z { get; }

    /// <summary>How many samples across it is.</summary>
    public int Size { get; }

    public float[] Heights { get; }
    public uint[] Control { get; }
    public uint[] Colors { get; }

    /// <summary>Whether it has edits not yet saved.</summary>
    public bool Modified { get; set; }

    /// <summary>Its lowest and highest heights.</summary>
    public (float Low, float High) HeightRange()
    {
        var (low, high) = (float.MaxValue, float.MinValue);
        foreach (var height in Heights)
        {
            low = Math.Min(low, height);
            high = Math.Max(high, height);
        }
        return low <= high ? (low, high) : (0f, 0f);
    }

    /// <summary>The file a region is kept in, without its extension: as Region-X+01-Z-02.</summary>
    public static string NameOf(int x, int z) => $"Region-X{x:+00;-00}-Z{z:+00;-00}";

    /// <summary>Writes it, compressed: a header, then its heights, control and colours.</summary>
    public void Write(Stream stream)
    {
        using var zip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        using var writer = new BinaryWriter(zip);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(X);
        writer.Write(Z);
        writer.Write(Size);
        WriteAll(writer, Heights);
        WriteAll(writer, Control);
        WriteAll(writer, Colors);
    }

    /// <summary>Reads one written by <see cref="Write"/>.</summary>
    public static TerrainRegion Read(Stream stream)
    {
        using var zip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(zip);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a terrain region.");
        var version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"A terrain region of version {version}; this reads {Version}.");
        var region = new TerrainRegion(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        ReadAll(reader, region.Heights);
        ReadAll(reader, region.Control);
        ReadAll(reader, region.Colors);
        return region;
    }

    private static void WriteAll<T>(BinaryWriter writer, T[] values) where T : unmanaged
    {
        var bytes = new byte[Buffer.ByteLength(values)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        writer.Write(bytes);
    }

    private static void ReadAll<T>(BinaryReader reader, T[] values) where T : unmanaged
    {
        var bytes = reader.ReadBytes(Buffer.ByteLength(values));
        if (bytes.Length != Buffer.ByteLength(values))
            throw new EndOfStreamException("A terrain region cut short.");
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
    }
}

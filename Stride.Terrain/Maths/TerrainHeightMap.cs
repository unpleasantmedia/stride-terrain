using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Stride.Terrain;

/// <summary>
/// A height map read from a file: grayscale, as 0 to 1 a pixel, its first row the terrain's far edge (towards -z).
/// Reads PNGs (grayscale or colour, 8 or 16 bits, not interlaced; colour ones by their red) and headerless 16-bit
/// RAW files (little-endian, square).
/// </summary>
public sealed class TerrainHeightMap
{
    public TerrainHeightMap(int width, int height, float[] values)
    {
        if (values.Length != width * height)
            throw new ArgumentException("A height map's values must be its width times its height.", nameof(values));
        Width = width;
        Height = height;
        Values = values;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Its pixels, 0 to 1, row by row.</summary>
    public float[] Values { get; }

    public float this[int x, int z] => Values[z * Width + x];

    /// <summary>Reads one by its file's extension: .png, or .raw / .r16.</summary>
    public static TerrainHeightMap Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ReadPng(bytes),
            ".raw" or ".r16" => ReadRaw(bytes),
            var other => throw new InvalidDataException($"A height map must be a .png, .raw or .r16 file, not {other}."),
        };
    }

    /// <summary>A headerless square of little-endian 16-bit heights.</summary>
    public static TerrainHeightMap ReadRaw(byte[] bytes)
    {
        var count = bytes.Length / 2;
        var size = (int)Math.Round(Math.Sqrt(count));
        if (bytes.Length % 2 != 0 || size * size != count)
            throw new InvalidDataException($"A RAW height map must be a square of 16-bit heights; this one is {bytes.Length} bytes.");
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * i)) / 65535f;
        return new TerrainHeightMap(size, size, values);
    }

    public static TerrainHeightMap ReadPng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("Not a PNG.");
        int width = 0, height = 0, depth = 0, colour = -1;
        using var compressed = new MemoryStream();
        for (var at = 8; at + 8 <= bytes.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            var data = bytes.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                (depth, colour) = (data[8], data[9]);
                if (data[12] != 0)
                    throw new InvalidDataException("Interlaced PNGs are not read: save it without interlacing.");
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }
            at += 12 + length;
        }
        // Gray, RGB, gray and alpha, RGBA: how many channels a pixel has.
        var channels = colour switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNGs of colour type {colour} (palettes) are not read.") };
        if (depth != 8 && depth != 16)
            throw new InvalidDataException($"PNGs of {depth} bits a channel are not read: 8 or 16.");

        var pixelBytes = channels * depth / 8;
        var stride = width * pixelBytes;
        compressed.Position = 0;
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * height];
        inflate.ReadExactly(raw);

        var values = new float[width * height];
        var previous = new byte[stride];
        var row = new byte[stride];
        for (var z = 0; z < height; z++)
        {
            var filter = raw[z * (stride + 1)];
            Array.Copy(raw, z * (stride + 1) + 1, row, 0, stride);
            Unfilter(filter, row, previous, pixelBytes);
            for (var x = 0; x < width; x++)
            {
                var at = x * pixelBytes;
                values[z * width + x] = depth == 16 ? (row[at] << 8 | row[at + 1]) / 65535f : row[at] / 255f;
            }
            (previous, row) = (row, previous);
        }
        return new TerrainHeightMap(width, height, values);
    }

    private static void Unfilter(byte filter, byte[] row, byte[] previous, int pixelBytes)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= pixelBytes ? row[i - pixelBytes] : 0, up = previous[i], corner = i >= pixelBytes ? previous[i - pixelBytes] : 0;
            row[i] = (byte)(row[i] + filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, corner),
                _ => throw new InvalidDataException($"A PNG row filter of {filter}."),
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// A terrain's regions made from it, a pixel a sample, its middle on the terrain's origin: black at
    /// <paramref name="low"/> metres, white at <paramref name="high"/>, and holes in the regions round it where it
    /// does not reach. All unpainted (under its material's height and slope rules).
    /// </summary>
    public List<TerrainRegion> ToRegions(int regionSize, float low, float high)
    {
        var (x0, z0) = (-Width / 2, -Height / 2);
        var (r0x, r0z) = (FloorDiv(x0, regionSize), FloorDiv(z0, regionSize));
        var (r1x, r1z) = (FloorDiv(x0 + Width - 1, regionSize), FloorDiv(z0 + Height - 1, regionSize));
        var regions = new List<TerrainRegion>();
        for (var rz = r0z; rz <= r1z; rz++)
        {
            for (var rx = r0x; rx <= r1x; rx++)
            {
                var region = new TerrainRegion(rx, rz, regionSize) { Modified = true };
                for (var z = 0; z < regionSize; z++)
                {
                    for (var x = 0; x < regionSize; x++)
                    {
                        var (px, pz) = (rx * regionSize + x - x0, rz * regionSize + z - z0);
                        var i = z * regionSize + x;
                        if (px < 0 || pz < 0 || px >= Width || pz >= Height)
                        {
                            region.Heights[i] = low;
                            region.Control[i] = TerrainControl.With(region.Control[i], TerrainControl.HoleBit, true);
                        }
                        else
                        {
                            region.Heights[i] = low + this[px, pz] * (high - low);
                        }
                    }
                }
                regions.Add(region);
            }
        }
        return regions;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
}

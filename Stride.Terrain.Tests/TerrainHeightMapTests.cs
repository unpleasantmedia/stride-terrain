using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Stride.Terrain;
using Xunit;

namespace Stride.Terrain.Tests;

public class TerrainHeightMapTests
{
    [Fact]
    public void Reads16BitGrayPngsWithEveryRowFilter()
    {
        // 5 rows of 3 pixels, each row written with a different filter (0-4) over the same values.
        var values = new ushort[,] { { 0, 1000, 65535 }, { 30000, 30001, 2 }, { 7, 65000, 12345 }, { 400, 401, 402 }, { 65535, 0, 32768 } };
        var map = TerrainHeightMap.ReadPng(Png(values));
        Assert.Equal(3, map.Width);
        Assert.Equal(5, map.Height);
        for (var z = 0; z < 5; z++)
        {
            for (var x = 0; x < 3; x++)
                Assert.Equal(values[z, x] / 65535f, map[x, z], 6);
        }
    }

    [Fact]
    public void ReadsSquareLittleEndianRaws()
    {
        var bytes = new byte[2 * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 65535);
        var map = TerrainHeightMap.ReadRaw(bytes);
        Assert.Equal(2, map.Width);
        Assert.Equal(1f, map[1, 1]);
        Assert.Throws<InvalidDataException>(() => TerrainHeightMap.ReadRaw(new byte[6]));
    }

    [Fact]
    public void BecomesRegionsCentredOnTheOriginWithHolesRoundIt()
    {
        var map = new TerrainHeightMap(4, 4, Enumerable.Repeat(0.5f, 16).ToArray());
        var regions = map.ToRegions(4, 10f, 30f);
        // Samples -2..1 each way: four regions of 4.
        Assert.Equal(4, regions.Count);
        var corner = regions.Single(r => r.X == -1 && r.Z == -1);
        // Its last two samples each way are the map's; the rest are holes.
        Assert.Equal(20f, corner.Heights[3 * 4 + 3]);
        Assert.False(TerrainControl.IsHole(corner.Control[3 * 4 + 3]));
        Assert.True(TerrainControl.IsAuto(corner.Control[3 * 4 + 3]));
        Assert.True(TerrainControl.IsHole(corner.Control[0]));
    }

    private static byte[] Png(ushort[,] values)
    {
        int height = values.GetLength(0), width = values.GetLength(1), stride = 2 * width;
        var rows = new byte[height][];
        for (var z = 0; z < height; z++)
        {
            rows[z] = new byte[stride];
            for (var x = 0; x < width; x++)
                BinaryPrimitives.WriteUInt16BigEndian(rows[z].AsSpan(2 * x), values[z, x]);
        }
        var filtered = new MemoryStream();
        for (var z = 0; z < height; z++)
        {
            var filter = (byte)(z % 5);
            filtered.WriteByte(filter);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= 2 ? rows[z][i - 2] : 0, up = z > 0 ? rows[z - 1][i] : 0, corner = i >= 2 && z > 0 ? rows[z - 1][i - 2] : 0;
                var predicted = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, corner) };
                filtered.WriteByte((byte)(rows[z][i] - predicted));
            }
        }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(filtered.ToArray());

        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 16;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    // Its CRC is not checked by the reader, so left as nought.
    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        png.Write(length);
        png.Write(System.Text.Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write(new byte[4]);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

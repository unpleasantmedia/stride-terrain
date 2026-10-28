using System;
using System.IO;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

namespace Stride.Terrain;

/// <summary>
/// Where a terrain's region files are. Each region is a file of its own (<see cref="TerrainRegion.Write"/>), named by
/// <see cref="TerrainRegion.NameOf"/>, in its terrain's <see cref="TerrainComponent.Folder"/> under the project's assets:
/// a .bin file, with a raw asset (.sdraw) beside it, so the game is built with it as it is. In the editor (see
/// <see cref="DiskFolder"/>) they are read and written there on disk; in the game, read from its content.
/// </summary>
public static class TerrainStorage
{
    public const string Extension = ".bin";

    /// <summary>
    /// The folder on disk a terrain's folder is (from its folder, as an asset url), set by the editor; none in the game,
    /// which reads its regions from its content.
    /// </summary>
    public static Func<string, string?>? DiskFolder { get; set; }

    /// <summary>Reads a region, if there is one: from disk in the editor, else from the game's content. None if it cannot be read.</summary>
    public static TerrainRegion? Load(IContentManager? content, string folder, int x, int z)
    {
        var name = TerrainRegion.NameOf(x, z);
        try
        {
            if (DiskFolder?.Invoke(folder) is { } disk)
            {
                var path = Path.Combine(disk, name + Extension);
                if (!File.Exists(path))
                    return null;
                using var file = File.OpenRead(path);
                return TerrainRegion.Read(file);
            }
            var url = $"{folder.Trim('/')}/{name}";
            if (content == null || !content.Exists(url))
                return null;
            using var stream = content.OpenAsStream(url, StreamFlags.None);
            return TerrainRegion.Read(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes a region to its file on disk, with its raw asset beside it if it has none yet. Only in the editor.</summary>
    public static bool Save(string folder, TerrainRegion region)
    {
        if (DiskFolder?.Invoke(folder) is not { } disk)
            return false;
        Directory.CreateDirectory(disk);
        var name = TerrainRegion.NameOf(region.X, region.Z);
        using (var file = File.Create(Path.Combine(disk, name + Extension)))
            region.Write(file);
        var asset = Path.Combine(disk, name + ".sdraw");
        if (!File.Exists(asset))
        {
            // Already compressed: packed into the game as it is.
            File.WriteAllText(asset, $"!RawAsset\nId: {Guid.NewGuid()}\nTags: []\nSource: {name}{Extension}\nCompress: false\n");
        }
        region.Modified = false;
        return true;
    }
}

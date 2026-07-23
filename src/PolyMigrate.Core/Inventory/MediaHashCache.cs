using System.Security.Cryptography;

namespace PolyMigrate.Core.Inventory;

/// <summary>
/// 媒體雜湊快取:(相對路徑, 大小, mtime)→ sha1。
/// media_manifest 每次重算全部檔案的 SHA1 要重讀數 GB——「可離線重跑」的工具重跑就該快,
/// 沒動過的檔案直接用上次的值。快取是內部加速檔、不是交付物(見 contracts.md)。
/// </summary>
internal sealed class MediaHashCache
{
    private readonly string _path;
    private readonly Dictionary<string, (long Size, long MtimeTicks, string Sha1)> _entries = new(StringComparer.Ordinal);
    private bool _dirty;

    private MediaHashCache(string path) => _path = path;

    public static MediaHashCache Load(string path)
    {
        var cache = new MediaHashCache(path);
        if (File.Exists(path))
        {
            foreach (var row in Csv.ReadRows(path).Skip(1))
            {
                if (row.Count == 4 && long.TryParse(row[1], out var size) && long.TryParse(row[2], out var ticks))
                {
                    cache._entries[row[0]] = (size, ticks, row[3]);
                }
            }
        }
        return cache;
    }

    /// <summary>檔案不存在回傳空值;快取命中免重讀,未命中計算並記入。</summary>
    public (string Sha1, string Bytes) GetOrCompute(string relative, string absolutePath)
    {
        var info = new FileInfo(absolutePath);
        if (!info.Exists)
        {
            return ("", "");
        }
        var mtime = info.LastWriteTimeUtc.Ticks;
        if (_entries.TryGetValue(relative, out var cached)
            && cached.Size == info.Length && cached.MtimeTicks == mtime)
        {
            return (cached.Sha1, info.Length.ToString());
        }
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(stream));
            _entries[relative] = (info.Length, mtime, sha1);
            _dirty = true;
            return (sha1, info.Length.ToString());
        }
        catch (IOException)
        {
            return ("", "");
        }
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var rows = new List<IReadOnlyList<string>> { new[] { "path", "size", "mtime_ticks", "sha1" } };
        rows.AddRange(_entries.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new[] { e.Key, e.Value.Size.ToString(), e.Value.MtimeTicks.ToString(), e.Value.Sha1 }));
        Csv.Write(_path, rows, guardFormulas: false);   // 內部快取本工具自己讀回,不可加公式防護前綴
    }
}

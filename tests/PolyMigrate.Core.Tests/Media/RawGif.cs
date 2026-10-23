namespace PolyMigrate.Core.Tests.Media;

/// <summary>
/// 直接組 GIF 位元組(不經 Magick):Magick 寫 GIF 時會把邏輯畫面校正成涵蓋所有影格,做不出
/// 「宣告的畫面比影格小」或「某一格的 LZW 壞掉」這種檔。每格純色(兩色交替)、LZW 壓縮後極小,
/// 所以宣告 3000x3000 的影格,檔案也只有幾十 KB(審查 review-v3b 的 mkgif.js 移植)。
/// </summary>
internal static class RawGif
{
    /// <param name="screen">邏輯畫面(logical screen)寬高,可以宣告得比影格小。</param>
    /// <param name="frames">每格的寬高,依序。</param>
    /// <param name="corruptFrame">這一格的 LZW 資料寫成壞的(第一個碼就超出字典);null = 全部正常。</param>
    public static byte[] Build((int Width, int Height) screen, IReadOnlyList<(int Width, int Height)> frames, int? corruptFrame = null)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([.. U16(screen.Width), .. U16(screen.Height), 0x80, 0, 0]);   // 全域色表,2 色
        gif.AddRange([255, 0, 0, 0, 0, 255]);
        gif.AddRange([0x21, 0xff, 0x0b, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0]);   // 無限循環
        for (var i = 0; i < frames.Count; i++)
        {
            var (width, height) = frames[i];
            gif.AddRange([0x21, 0xf9, 4, 0x04, 10, 0, 0, 0]);   // dispose none、延遲 10
            gif.AddRange([0x2c, 0, 0, 0, 0, .. U16(width), .. U16(height), 0]);
            gif.Add(2);   // LZW 最小碼長
            gif.AddRange(i == corruptFrame ? CorruptData() : Lzw(2, width * height, i % 2));
        }
        gif.Add(0x3b);
        return [.. gif];
    }

    private static byte[] U16(int value) => [(byte)(value & 0xff), (byte)((value >> 8) & 0xff)];

    /// <summary>一個子區塊:clear 碼之後接一個字典裡還沒有的碼,解碼器讀到就知道這格壞了。</summary>
    private static byte[] CorruptData() => [3, 0x04 | (31 << 3), 0xff, 0xff, 0];

    /// <summary>GIF 的變長 LZW,整格同一個值。</summary>
    private static byte[] Lzw(int minCodeSize, int count, int value)
    {
        var output = new List<byte>();
        var block = new List<byte>();
        long bits = 0;
        var bitCount = 0;
        void EmitByte(byte b)
        {
            block.Add(b);
            if (block.Count == 255)
            {
                output.Add(255);
                output.AddRange(block);
                block.Clear();
            }
        }
        void Emit(int code, int size)
        {
            bits |= (long)code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                EmitByte((byte)(bits & 0xff));
                bits >>= 8;
                bitCount -= 8;
            }
        }

        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var codeSize = minCodeSize + 1;
        var next = end + 1;
        var table = new Dictionary<int, int>();
        Emit(clear, codeSize);
        var key = value;
        for (var i = 1; i < count; i++)
        {
            var composite = (key << 8) | value;
            if (table.TryGetValue(composite, out var code))
            {
                key = code;
                continue;
            }
            Emit(key, codeSize);
            if (next == 4096)
            {
                Emit(clear, codeSize);
                next = end + 1;
                codeSize = minCodeSize + 1;
                table.Clear();
            }
            else
            {
                if (next >= 1 << codeSize)
                {
                    codeSize++;
                }
                table[composite] = next++;
            }
            key = value;
        }
        Emit(key, codeSize);
        Emit(end, codeSize);
        if (bitCount > 0)
        {
            EmitByte((byte)(bits & 0xff));
        }
        if (block.Count > 0)
        {
            output.Add((byte)block.Count);
            output.AddRange(block);
        }
        output.Add(0);
        return [.. output];
    }
}

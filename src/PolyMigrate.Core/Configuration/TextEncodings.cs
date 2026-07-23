using System.Text;

namespace PolyMigrate.Core.Configuration;

/// <summary>編碼解析(§3.1)。註冊 CodePages 讓 Big5/GB2312 等舊站編碼可用。</summary>
internal static class TextEncodings
{
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>依 config 的 site.encoding 解析;null = UTF-8。</summary>
    public static Encoding Resolve(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Encoding.UTF8 : Encoding.GetEncoding(name);
}

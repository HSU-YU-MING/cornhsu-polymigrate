using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace PolyMigrate.Core.Markdown;

/// <summary>
/// frontmatter 序列化(§2.6 兩坑):
/// 1. 一律用 YAML lib 跳脫——標題常含冒號/日期,手工拼會壞。
/// 2. 會被 YAML 誤判成數字/布林/null 的字串(如 slug '01182024')強制加單引號保留為字串;
///    PyYAML 與 js-yaml 對前導 0 判斷不一致,故任何純數字型字串一律強制引號(規格 _str_representer)。
/// </summary>
public static partial class FrontmatterSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithEventEmitter(next => new ForceQuoteAmbiguousStringsEmitter(next))
        .DisableAliases()
        .Build();

    /// <summary>序列化為含 "---" 圍欄的 frontmatter 區塊(尾含空行)。欄位序即輸出序。</summary>
    public static string ToBlock(IDictionary<string, object?> fields) =>
        "---\n" + Serializer.Serialize(fields) + "---\n\n";

    private sealed partial class ForceQuoteAmbiguousStringsEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        [GeneratedRegex(@"^[-+]?[0-9][0-9_]*(\.[0-9_]+)?$")]
        private static partial Regex NumericLike();

        // YAML 1.1 會誤判為布林/null 的裸字(js-yaml/舊解析器相容)
        private static readonly HashSet<string> AmbiguousWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "yes", "no", "on", "off", "null", "~",
        };

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string)
                && eventInfo.Source.Value is string s
                && (NumericLike().IsMatch(s) || AmbiguousWords.Contains(s)))
            {
                eventInfo = new ScalarEventInfo(eventInfo.Source) { Style = ScalarStyle.SingleQuoted };
            }
            nextEmitter.Emit(eventInfo, emitter);
        }
    }
}

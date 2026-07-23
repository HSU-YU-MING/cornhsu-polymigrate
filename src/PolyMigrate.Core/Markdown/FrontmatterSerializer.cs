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
internal static partial class FrontmatterSerializer
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

        // 下面幾式在 YAML 1.1(PyYAML / js-yaml / Hugo)會被解成非字串,故一律強制引號保為字串:
        [GeneratedRegex(@"^\d{4}-\d{1,2}-\d{1,2}([Tt ].*)?$")]      // ISO 日期/時間戳(slug 常見)
        private static partial Regex DateLike();

        [GeneratedRegex(@"^[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+$")]     // 六十進位(19:30 → base-60 整數)
        private static partial Regex SexagesimalLike();

        [GeneratedRegex(@"^[-+]?0(x[0-9a-fA-F_]+|o[0-7_]+|b[01_]+)$")]   // 十六/八/二進位
        private static partial Regex BaseNLike();

        [GeneratedRegex(@"^[-+]?\.[0-9][0-9_]*$")]                  // 前導小數點浮點(.5)
        private static partial Regex DottedFloatLike();

        // YAML 1.1 會誤判為布林/null/特殊浮點的裸字(js-yaml/舊解析器相容)
        private static readonly HashSet<string> AmbiguousWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "yes", "no", "on", "off", "null", "~",
            ".inf", "+.inf", "-.inf", ".nan",
        };

        private static bool NeedsQuoting(string s) =>
            NumericLike().IsMatch(s) || DateLike().IsMatch(s) || SexagesimalLike().IsMatch(s)
            || BaseNLike().IsMatch(s) || DottedFloatLike().IsMatch(s) || AmbiguousWords.Contains(s);

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string)
                && eventInfo.Source.Value is string s
                && NeedsQuoting(s))
            {
                eventInfo = new ScalarEventInfo(eventInfo.Source) { Style = ScalarStyle.SingleQuoted };
            }
            nextEmitter.Emit(eventInfo, emitter);
        }
    }
}

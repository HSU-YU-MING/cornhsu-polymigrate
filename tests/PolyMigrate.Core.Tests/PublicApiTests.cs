using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace PolyMigrate.Core.Tests;

/// <summary>
/// 函式庫 <c>Cornhsu.PolyMigrate.Core</c> 的公開面快照(semver 契約)。
///
/// <para>沒有 PublicApiAnalyzers、也沒有 EnablePackageValidation 的 baseline,所以在這之前,
/// 新增一個 public 型別、改一個參數名、把方法加一個選用參數(原始碼相容但二進位不相容——
/// 2.2.0 的 <c>Verify(..., allowUnlinked)</c> 就是這樣以 minor 發出去的)都不會有任何東西變紅。
/// 這份快照不判斷對錯,只逼人在改到公開面時**看見**它,然後決定這是 minor 還是 major。</para>
///
/// <para>基準更新(確認改動是刻意的之後):設 <c>POLYMIGRATE_UPDATE_API=1</c> 跑一次這個測試,
/// 再看 <c>git diff tests/PolyMigrate.Core.Tests/PublicApi.approved.txt</c>——diff 本身就是 semver 審查。</para>
/// </summary>
public class PublicApiTests
{
    private static readonly string ApprovedPath =
        Path.Combine(FindRepoRoot(), "tests", "PolyMigrate.Core.Tests", "PublicApi.approved.txt");

    [Fact]
    public void PublicSurface_MatchesApprovedBaseline()
    {
        var actual = Dump(typeof(PolyMigrator).Assembly);

        if (Environment.GetEnvironmentVariable("POLYMIGRATE_UPDATE_API") == "1")
        {
            File.WriteAllText(ApprovedPath, actual, new UTF8Encoding(false));
            return;
        }

        var approved = File.ReadAllText(ApprovedPath).Replace("\r", "");
        var expectedLines = Qualified(approved);
        var actualLines = Qualified(actual);
        var added = actualLines.Except(expectedLines).ToList();
        var removed = expectedLines.Except(actualLines).ToList();
        Assert.True(approved == actual,
            $"""
            PolyMigrate.Core 的公開面與 PublicApi.approved.txt 不一致——先判 semver 再更新基準。
              新增(minor,除非是既有成員的簽章變更):
                {(added.Count == 0 ? "(無)" : string.Join("\n    ", added))}
              移除或變更(major):
                {(removed.Count == 0 ? "(無)" : string.Join("\n    ", removed))}
            確認是刻意的:POLYMIGRATE_UPDATE_API=1 dotnet test --filter PublicSurface_MatchesApprovedBaseline
            """);
    }

    [Fact]
    public void FacadeIsPublic_ImplementationHelpersAreNot()
    {
        // 「新 helper 一律 internal」(CLAUDE.md 開工慣例)的反向檢查:這幾個是 2.x 時刻意收回的內部型別
        var exported = typeof(PolyMigrator).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet();

        Assert.Contains("PolyMigrator", exported);
        foreach (var internalType in (string[])["PageExtractor", "LinkRewriter", "Csv", "PathSafety",
                     "HreflangIndex", "PairingSuggester", "InventoryAggregator", "FrontmatterSerializer"])
        {
            Assert.DoesNotContain(internalType, exported);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 快照格式:每個 exported type 一段,成員一行一個、依字串排序(反射回傳順序不保證)。
    // 簽章含 nullability、預設值、ref/out、static、init、required——這些都是消費端看得到的面。
    // ─────────────────────────────────────────────────────────────────────────

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>每個成員行前面加上所屬型別——同一行字(如 public ctor())出現在很多型別裡,不能只比字串集合。</summary>
    private static List<string> Qualified(string dump)
    {
        var result = new List<string>();
        var type = "";
        foreach (var line in dump.Split('\n').Where(l => l.Length > 0))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                type = line[3..];
                result.Add(type);
            }
            else
            {
                result.Add($"{type} :: {line.Trim()}");
            }
        }
        return result;
    }

    private static string Dump(Assembly assembly)
    {
        var sb = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.Append("## ").Append(TypeHeader(type)).Append('\n');
            // 建構子 → 欄位 → 屬性 → 方法 → 事件,同類依名稱、再依完整簽章(多載)
            foreach (var (_, _, line) in Members(type)
                         .OrderBy(m => m.Category)
                         .ThenBy(m => m.Name, StringComparer.Ordinal)
                         .ThenBy(m => m.Line, StringComparer.Ordinal))
            {
                sb.Append("   ").Append(line).Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";   // 檔尾只留一個換行(不留空行)
    }

    private static string TypeHeader(Type t)
    {
        var kind = t.IsEnum ? "enum"
            : t.IsInterface ? "interface"
            : t.IsValueType ? "struct"
            : t.IsAbstract && t.IsSealed ? "static class"
            : IsRecord(t) ? "record"
            : "class";
        var modifiers = !t.IsValueType && !t.IsInterface && t.IsSealed && !t.IsAbstract ? "sealed " : "";
        var bases = new List<string>();
        if (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum))
        {
            bases.Add(PlainName(b));
        }
        bases.AddRange(t.GetInterfaces()
            .Where(i => i.IsPublic && (t.BaseType is null || !t.BaseType.GetInterfaces().Contains(i)))
            .Select(PlainName)
            .Order(StringComparer.Ordinal));
        return $"{modifiers}{kind} {t.FullName}" + (bases.Count > 0 ? " : " + string.Join(", ", bases) : "");
    }

    private static bool IsRecord(Type t) => t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;

    private static IEnumerable<(int Category, string Name, string Line)> Members(Type t)
    {
        if (t.IsEnum)
        {
            // 列舉值:依數值排(數值本身就是契約——序列化成數字的消費端靠它)
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .OrderBy(f => Convert.ToInt64(f.GetRawConstantValue())))
            {
                yield return (1, $"{Convert.ToInt64(f.GetRawConstantValue()):D10}", $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue())}");
            }
            yield break;
        }
        foreach (var c in t.GetConstructors(Declared))
        {
            yield return (0, "", $"public ctor({Parameters(c)})");
        }
        foreach (var f in t.GetFields(Declared))
        {
            var mods = f.IsLiteral ? "const " : (f.IsStatic ? "static " : "") + (f.IsInitOnly ? "readonly " : "");
            yield return (1, f.Name, $"public {mods}{TypeName(f.FieldType, Nullability.Create(f), TupleNames(f))} {f.Name}"
                + (f.IsLiteral ? $" = {Literal(f.GetRawConstantValue())}" : ""));
        }
        foreach (var p in t.GetProperties(Declared))
        {
            var accessors = new List<string>();
            if (p.GetMethod is { IsPublic: true })
            {
                accessors.Add("get;");
            }
            if (p.SetMethod is { IsPublic: true } set)
            {
                accessors.Add(set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit))
                    ? "init;" : "set;");
            }
            var isStatic = (p.GetMethod ?? p.SetMethod)!.IsStatic ? "static " : "";
            var required = p.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : "";
            yield return (2, p.Name, $"public {isStatic}{required}{TypeName(p.PropertyType, Nullability.Create(p), TupleNames(p))} "
                + $"{p.Name} {{ {string.Join(' ', accessors)} }}");
        }
        foreach (var m in t.GetMethods(Declared).Where(m => !m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)))
        {
            var mods = m.IsStatic ? "static "
                : m.IsAbstract ? "abstract "
                : m.GetBaseDefinition() != m ? "override "
                : m.IsVirtual && !m.IsFinal ? "virtual " : "";
            var generic = m.IsGenericMethodDefinition
                ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
            var extension = m.IsDefined(typeof(ExtensionAttribute)) ? "this " : "";
            yield return (3, m.Name, $"public {mods}{TypeName(m.ReturnType, Nullability.Create(m.ReturnParameter), TupleNames(m.ReturnParameter))} "
                + $"{m.Name}{generic}({extension}{Parameters(m)})");
        }
        foreach (var e in t.GetEvents(Declared))
        {
            yield return (4, e.Name, $"public event {PlainName(e.EventHandlerType!)} {e.Name}");
        }
    }

    /// <summary>不帶 nullability 的型別名(基底型別、介面、事件型別用)。</summary>
    private static string PlainName(Type t) => t.IsGenericType
        ? $"{Name(t)}<{string.Join(", ", t.GetGenericArguments().Select(PlainName))}>"
        : Name(t);

    private static string Parameters(MethodBase m) => string.Join(", ", m.GetParameters().Select(p =>
    {
        var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? (p.IsIn ? "in " : "ref ") : "";
        var paramsKw = p.IsDefined(typeof(ParamArrayAttribute)) ? "params " : "";
        var text = $"{paramsKw}{prefix}{TypeName(type, Nullability.Create(p), TupleNames(p))} {p.Name}";
        if (!p.HasDefaultValue)
        {
            return text;
        }
        // 值型別的 = default(如 CancellationToken ct = default)在反射裡是 null
        return $"{text} = {(p.DefaultValue is null && type.IsValueType ? "default" : Literal(p.DefaultValue))}";
    }));

    private static Queue<string?> TupleNames(ICustomAttributeProvider provider) =>
        new(provider.GetCustomAttributes(typeof(TupleElementNamesAttribute), false)
            .Cast<TupleElementNamesAttribute>().FirstOrDefault()?.TransformNames ?? []);

    private static string TypeName(Type t, NullabilityInfo info, Queue<string?>? tupleNames = null)
    {
        tupleNames ??= new Queue<string?>();
        if (Nullable.GetUnderlyingType(t) is { } underlying)
        {
            return TypeName(underlying, info.GenericTypeArguments.FirstOrDefault() ?? info, tupleNames) + "?";
        }
        var nullable = !t.IsValueType && info.ReadState == NullabilityState.Nullable ? "?" : "";
        if (t.IsArray)
        {
            return TypeName(t.GetElementType()!, info.ElementType ?? info, tupleNames) + "[]" + nullable;
        }
        if (t.IsGenericType && t.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
        {
            var args = t.GetGenericArguments();
            var names = args.Select(_ => tupleNames.Count > 0 ? tupleNames.Dequeue() : null).ToList();
            var elements = args.Select((a, i) =>
            {
                var element = TypeName(a, info.GenericTypeArguments.ElementAtOrDefault(i) ?? info, tupleNames);
                return names[i] is { } n ? $"{element} {n}" : element;
            });
            return $"({string.Join(", ", elements)})";
        }
        if (t.IsGenericType)
        {
            var args = t.GetGenericArguments()
                .Select((a, i) => TypeName(a, info.GenericTypeArguments.ElementAtOrDefault(i) ?? info, tupleNames));
            return $"{Name(t)}<{string.Join(", ", args)}>" + nullable;
        }
        return Name(t) + nullable;
    }

    /// <summary>BCL 型別用短名(與原始碼寫法一致),本組件與第三方型別用完整名稱。</summary>
    private static string Name(Type t)
    {
        if (t.IsGenericParameter)
        {
            return t.Name;
        }
        if (Alias(t) is { } alias)
        {
            return alias;
        }
        var name = t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] : t.Name;
        return t.Namespace is "System" or "System.Collections.Generic" or "System.Threading"
            or "System.Threading.Tasks" or "System.Net.Http"
            ? name
            : $"{t.Namespace}.{name}";
    }

    private static string? Alias(Type t) => Type.GetTypeCode(t) switch
    {
        _ when t.IsEnum => null,   // GetTypeCode 對列舉回傳底層型別,不能當成 int
        _ when t == typeof(object) => "object",
        _ when t == typeof(void) => "void",
        TypeCode.String => "string",
        TypeCode.Boolean => "bool",
        TypeCode.Int32 => "int",
        TypeCode.Int64 => "long",
        TypeCode.Double => "double",
        TypeCode.Byte => "byte",
        _ => null,
    };

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PolyMigrate.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }
        return dir;
    }
}

using System.Reflection;

// 骨架階段的極簡進入點;指令實作與 --dry-run(§3.8)隨各 Phase 落地。
// 之後如需子指令/選項解析再引入 System.CommandLine。

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

switch (args.FirstOrDefault())
{
    case "--version" or "-v":
        Console.WriteLine(version);
        return 0;

    case "extract" or "verify":
        Console.Error.WriteLine($"'{args[0]}' is not implemented yet.");
        return 2;

    case null or "--help" or "-h":
        Console.WriteLine(
            $"""
            polymigrate {version} — the i18n-first static-site migrator

            Usage:
              polymigrate extract <config.yaml>   Run extraction pipeline (not implemented)
              polymigrate verify <output-dir>     Verify migrated output (not implemented)
              polymigrate --version               Print version
            """);
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command: {args[0]}. Run 'polymigrate --help'.");
        return 2;
}

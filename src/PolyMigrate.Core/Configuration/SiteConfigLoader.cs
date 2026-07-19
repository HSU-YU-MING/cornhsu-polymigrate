using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PolyMigrate.Core.Configuration;

/// <summary>config 載入或驗證失敗(§3.9)。訊息面向使用者,CLI 直接印出。</summary>
public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// YAML site config 載入器(§3.9):underscored 命名、未知欄位報錯(不默默忽略)、載入後驗證。
/// </summary>
public static class SiteConfigLoader
{
    public static SiteConfig LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Config file not found: {path}");
        }
        return Load(File.ReadAllText(path), path);
    }

    public static SiteConfig Load(string yaml, string sourceName = "<string>")
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();   // 不設 IgnoreUnmatchedProperties → 未知欄位一律報錯

        SiteConfig config;
        try
        {
            config = deserializer.Deserialize<SiteConfig>(yaml)
                ?? throw new ConfigException($"{sourceName}: config is empty.");
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"{sourceName}: invalid config — {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        Validate(config, sourceName);
        return config;
    }

    private static void Validate(SiteConfig c, string src)
    {
        var errors = new List<string>();

        if (c.ConfigVersion != 1)
        {
            errors.Add($"config_version {c.ConfigVersion} is not supported (expected 1).");
        }
        if (!Uri.TryCreate(c.Site.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"site.base_url must be an absolute http(s) URL, got '{c.Site.BaseUrl}'.");
        }
        if (c.UrlPattern.LangMap.Count == 0)
        {
            errors.Add("url_pattern.lang_map must map at least one URL prefix to a BCP-47 locale (single-language sites use one entry).");
        }
        if (string.IsNullOrWhiteSpace(c.UrlPattern.DefaultLang))
        {
            errors.Add("url_pattern.default_lang is required.");
        }
        else if (c.UrlPattern.LangMap.Count > 0 && !c.UrlPattern.LangMap.ContainsValue(c.UrlPattern.DefaultLang))
        {
            errors.Add($"url_pattern.default_lang '{c.UrlPattern.DefaultLang}' must be one of the lang_map locales ({string.Join(", ", c.UrlPattern.LangMap.Values)}).");
        }
        if (string.IsNullOrWhiteSpace(c.Extract.Content))
        {
            errors.Add("extract.content (CSS selector for the main content) is required.");
        }
        if (c.Site.Encoding is { } enc)
        {
            try
            {
                _ = TextEncodings.Resolve(enc);
            }
            catch (ArgumentException)
            {
                errors.Add($"site.encoding '{enc}' is not a known encoding.");
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigException($"{src}: " + string.Join("\n  - ", ["config is invalid:", .. errors]));
        }
    }
}

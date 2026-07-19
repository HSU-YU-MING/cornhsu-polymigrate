using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Tests;

/// <summary>測試用 config:與 examples/ibps-austin.yaml 同構的最小版本。</summary>
public static class TestConfigs
{
    public static SiteConfig IbpsLike() => new()
    {
        Site = new SiteSection { BaseUrl = "https://www.ibps-austin.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
            DefaultLang = "zh-Hant",
            StripExtensions = [".php"],
        },
        Extract = new ExtractSection
        {
            Content = "section[id]:not(#header):not(#footer):not(#header_main):not(#footer_main)",
            TitleNoise = ["佛光山香雲寺", "IBPS Austin", "Fo Guang Shan Xiang Yun Temple"],
            SectionTypes = new Dictionary<string, string> { ["news"] = "article", ["events"] = "event" },
            SectionFlags = new Dictionary<string, string> { ["support"] = "needs_rebuild" },
            TypeRules = new Dictionary<string, PageTypeRules>
            {
                ["article"] = new PageTypeRules
                {
                    TitleSelector = "#personal h3, .personal_1 h3, #article h3",
                    RemoveSelectors =
                    [
                        "#personal .personal_1 h5", "#personal .personal_1 h3",
                        "#personal h5", "#personal h3", "#article h3",
                    ],
                    ImagesToGallery = true,
                },
            },
        },
        Pairing = new PairingSection
        {
            Fallback = ["shared_media", "date", "title_similarity"],
        },
        Media = new MediaSection
        {
            PdfLabels = new Dictionary<string, string> { ["zh-Hant"] = "下載／檢視 PDF", ["en"] = "View PDF" },
        },
    };
}

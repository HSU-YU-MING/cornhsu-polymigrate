using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>頁型分類(規格 classify),由 config 的 extract.section_types 驅動。</summary>
public sealed class PageClassifier(ExtractSection extract)
{
    public const string Listing = "listing";
    public const string DefaultType = "page";

    private readonly Dictionary<string, string> _sectionTypes = extract.SectionTypes;

    public string Classify(string section, string slug)
    {
        if (_sectionTypes.TryGetValue(section, out var type))
        {
            // 有型別的 section,其 index 頁是列表頁
            return slug == "index" ? Listing : type;
        }
        return DefaultType;
    }
}

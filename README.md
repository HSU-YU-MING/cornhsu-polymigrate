# PolyMigrate

> **The i18n-first static-site migrator** — the only site migrator that pairs your bilingual pages automatically.

Config 驅動、可離線重跑的「舊站 → 靜態站」搬遷工具,天生為多語言而生,以 .NET 實作。

**狀態:0.1 開發中。** Phase 2 抽取管線(`polymigrate extract`)已可運作,並以香雲寺全站
(516 頁、281 個 translation_key)對照 Python 原型完成端到端驗證。規劃見[評估與規劃書](搬遷工具_評估與規劃書.md)。

## 為什麼

多語言機構站(政府、大學、NGO、宗教組織)遷移時,都在手工對配多語版本 —— 沒有任何現有工具解這個問題。PolyMigrate 用「去語言前綴的路徑」自動配對可配的頁面,並列出配不起來的頁面供人工覆核。背後是一次真實完成的整站搬遷(495 頁、4.6GB 媒體、231 篇雙語文章、全站巡檢 0 錯誤)。

## 產品形態

| | 給誰 | 怎麼裝 |
|---|---|---|
| CLI | 只想用的人 | `dotnet tool install -g polymigrate`(尚未發佈) |
| `PolyMigrate.Core` | 要嵌進自己 pipeline 的人 | NuGet(尚未發佈) |

## 指令

| 指令 | 功能 |
|---|---|
| `polymigrate extract <config>` | Phase 2 結構化抽取:鏡像 HTML → frontmatter Markdown + 四份清單 + 雙語配對(含啟發式建議);`--dry-run` 只報告不寫檔 |
| `polymigrate verify <dir>` | 全站巡檢:frontmatter 欄位、內部連結、媒體引用;已知原站壞圖降為 warning |
| `polymigrate thumbs <config>` | 縮圖:EXIF 自動轉正(手機直拍坑)、等比縮至 max_width、增量可重跑 |
| `polymigrate probe-orphans <config>` | 探測「索引移除但頁面還在」的孤兒頁(逐日雙日期格式 + 後綴變體,禮貌間隔) |
| `polymigrate fetch-orphans <config>` | 抓回探測到的孤兒頁與其媒體進鏡像 |

## 開發

```
dotnet build
dotnet test
dotnet run --project src/PolyMigrate.Cli -- --help
```

依賴授權:全部為 MIT / BSD / Apache 2.0(影像處理用 **Magick.NET**;原規劃的
ImageSharp 因 4.x 起建置即要求授權金鑰而棄用,見規劃書 §3.7)。

| 目錄 | 內容 |
|---|---|
| `src/PolyMigrate.Core` | 抽取 / 配對 / 盤點核心(NuGet lib) |
| `src/PolyMigrate.Cli` | `polymigrate` CLI(dotnet tool) |
| `tests/PolyMigrate.Core.Tests` | 單元 + golden-file 測試 |
| `tests/fixtures/site` | 離線合成雙語 fixture 站(每個坑一頁) |
| `docs/contracts.md` | Phase 之間的輸出檔案契約 |

授權:1.0 前定案(MIT / Apache-2.0)。

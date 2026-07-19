# PolyMigrate

> **The i18n-first static-site migrator** — the only site migrator that pairs your bilingual pages automatically.

Config 驅動、可離線重跑的「舊站 → 靜態站」搬遷工具,天生為多語言而生,以 .NET 實作。

**狀態:0.1 骨架階段,尚不可用。** 規劃見[評估與規劃書](搬遷工具_評估與規劃書.md)。

## 為什麼

多語言機構站(政府、大學、NGO、宗教組織)遷移時,都在手工對配多語版本 —— 沒有任何現有工具解這個問題。PolyMigrate 用「去語言前綴的路徑」自動配對可配的頁面,並列出配不起來的頁面供人工覆核。背後是一次真實完成的整站搬遷(495 頁、4.6GB 媒體、231 篇雙語文章、全站巡檢 0 錯誤)。

## 產品形態

| | 給誰 | 怎麼裝 |
|---|---|---|
| CLI | 只想用的人 | `dotnet tool install -g polymigrate`(尚未發佈) |
| `PolyMigrate.Core` | 要嵌進自己 pipeline 的人 | NuGet(尚未發佈) |

## 開發

```
dotnet build
dotnet test
dotnet run --project src/PolyMigrate.Cli -- --help
```

| 目錄 | 內容 |
|---|---|
| `src/PolyMigrate.Core` | 抽取 / 配對 / 盤點核心(NuGet lib) |
| `src/PolyMigrate.Cli` | `polymigrate` CLI(dotnet tool) |
| `tests/PolyMigrate.Core.Tests` | 單元 + golden-file 測試 |
| `tests/fixtures/site` | 離線合成雙語 fixture 站(每個坑一頁) |
| `docs/contracts.md` | Phase 之間的輸出檔案契約 |

授權:1.0 前定案(MIT / Apache-2.0)。

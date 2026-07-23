# PolyMigrate

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.PolyMigrate.svg?label=Cornhsu.PolyMigrate)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![Downloads](https://img.shields.io/nuget/dt/Cornhsu.PolyMigrate.svg)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![CI](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **The i18n-first static-site migrator** — 唯一會自動配對多語言頁面的網站搬遷工具。

**[作品介紹與開發故事](https://cornhsu.com/polymigrate) · [NuGet](https://www.nuget.org/packages/Cornhsu.PolyMigrate) · [English](README.md) · MIT**

把老舊動態網站(舊 PHP 站等)搬成乾淨、可餵給靜態網站產生器的 Markdown,
並把「多語言」當成核心而非外掛。Config 驅動、全程離線可重跑,以 .NET 實作。

**狀態:2.0。** 抽取管線、雙語配對、全站巡檢、縮圖、孤兒頁找回皆已完成,
並以一次真實的整站搬遷驗證(見下)。**CLI 介面與 Phase 輸出契約維持穩定**(自 1.0 未變):
新功能升 minor、修正升 patch。2.0 是工程版:把 `Cornhsu.PolyMigrate.Core` 的公開 .NET API
收束到意圖中的進入點、移除未使用的 config 欄位,遷移說明見 [CHANGELOG](CHANGELOG.md)。

## 為什麼

多語言機構站(政府、大學、NGO、宗教組織)遷移時都在手工對配各語言版本——沒有現成工具解這個問題。PolyMigrate:

- 檔名對稱的頁面**自動配對**(`/ch/news/x` ↔ `/en/news/x` 共用 `translation_key`)
- 配不起來的**啟發式建議**:共用相簿、slug 內日期正規化(`20240121` vs `01212024`)、標題相似度
- 真的配不到的**誠實列出**,產出待人工覆核的缺漏清單,絕不亂猜

語言不限兩種:`lang_map` 宣告幾組就支援幾語,frontmatter、清單、配對全部跟著展開,輸出一律 BCP-47 標準代碼。

## 內建的實戰坑

| 真實踩過的坑 | 內建處理 |
|---|---|
| 手機直拍照片縮圖顛倒/側躺 | 縮圖前先依 EXIF 轉正 |
| 標題含冒號、數字 slug 前導 0 | YAML lib 跳脫 + 強制引號 |
| 圖片路徑 `%20` 雙重編碼 | 磁碟存解碼名、URL 單次編碼 |
| Markdown 轉換器丟掉影片/iframe/PDF | 佔位符保留在原位置 |
| `<title>` 帶日期與站名雜訊 | 內文標題優先 + 可設定清理 |
| 日期格式混用(YMD/MDY/DMY) | 全部認得並正規化 |
| 索引移除但頁面還在的孤兒文章 | 逐日 URL 候選 + 後綴變體探測 |
| 原站壞圖 | 偵測、記錄、不阻斷 |
| bot 防護(JS cookie 挑戰回 409) | config 宣告 cookie 繞法 |
| 舊編碼(Big5、GB2312…) | 每站宣告或預設 |

## 實戰案例

PolyMigrate 是一次已完成的真實搬遷(中英雙語佛寺網站)的產品化:

- **516 頁**、**4.6 GB** 媒體
- **281 個 translation key**;**231 篇雙語文章全自動配對**
- 內建巡檢:**1,269 個內部連結 + 4,116 個媒體引用,0 錯誤**
- date 探測找回 13 篇孤兒文章;修正 141 張 EXIF 顛倒照片的縮圖

移植過程以 Python 原型輸出為對照基準:466/516 頁正文在空白正規化後逐字相同,其餘皆為渲染等價或更保真。

同一次實跑的另外兩個數字:

- **重跑快約 7 倍**:媒體雜湊以 `(大小, mtime)` 快取,4.6 GB 實站全量重跑從 **30.1 秒降到 4.6 秒**。
- **產出即可部署**:`redirect_map` 自動填入新路徑,並另外輸出 **nginx conf** 與 **Netlify `_redirects`**
  ——301 設定從手填半天變成複製一個檔。

## 安裝與使用

兩條通路,選你手邊有的執行環境即可 —— **功能完全相同**。

```
npx cornhsu-polymigrate extract site.yaml   # 有 Node,不需要安裝 .NET
dotnet tool install -g Cornhsu.PolyMigrate  # 有 .NET
```

> npm 版是自帶執行環境的原生執行檔,只下載你這個平台的那一份
> (win32-x64 / linux-x64 / darwin-x64 / darwin-arm64)。
> 搬站多半是一次性任務 —— 為了跑一次而裝一整套 SDK,摩擦成本太高,
> 所以工具主動走到你所在的生態。

```
dotnet tool install -g Cornhsu.PolyMigrate
polymigrate extract site.yaml               # 鏡像 HTML → frontmatter Markdown + 清單
polymigrate verify out/                     # 連結/媒體/frontmatter 巡檢,exit code 可接 CI
polymigrate thumbs site.yaml                # EXIF 轉正縮圖
polymigrate probe-orphans site.yaml --section news --years 2021-2023
polymigrate fetch-orphans site.yaml --section news
```

> 沒有 .NET?上面每個指令都可用 npm **免安裝**執行,前面加 `npx cornhsu-polymigrate` 即可:
> `npx cornhsu-polymigrate extract site.yaml`、`npx cornhsu-polymigrate verify out/`,以此類推。

一站一份 YAML config,站別知識全在裡面——完整註解的真實範例見
[examples/ibps-austin.yaml](examples/ibps-austin.yaml)。

## 當函式庫用

CLI 只是 `Cornhsu.PolyMigrate.Core` 之上的薄殼。要在自己的 .NET 程式裡驅動搬遷,
用 `PolyMigrator` facade 即可 —— 唯一有文件的進入點,不必碰內部實作型別:

```
dotnet add package Cornhsu.PolyMigrate.Core
```

```csharp
using PolyMigrate.Core;

var migrator = PolyMigrator.FromConfigFile("site.yaml");
var report = migrator.Extract("out/");        // out/raw、out/media → out/content + 清單
if (report.HasErrors) { /* 有不安全路徑被跳過,見 path_issues.csv */ }

var verify = PolyMigrator.Verify("out/");      // 不需 config,只讀 Phase 2 輸出
Console.WriteLine($"{verify.Errors} errors, {verify.Warnings} warnings");
```

## 目錄

| 路徑 | 內容 |
|---|---|
| `src/PolyMigrate.Core` | 抽取/配對/巡檢核心(NuGet:`Cornhsu.PolyMigrate.Core`) |
| `src/PolyMigrate.Cli` | `polymigrate` CLI(NuGet tool:`Cornhsu.PolyMigrate`) |
| `tests/` | 單元/整合測試 + 離線 fixture 站與 golden 基準 |
| `docs/contracts.md` | Phase 之間的檔案格式契約 |
| `docs/搬遷工具_評估與規劃書.md` | 原始評估與規劃書(原始碼中滿地的 `§X.Y` 都指向這份) |

## 開發

```
dotnet build
dotnet test
dotnet run --project src/PolyMigrate.Cli -- --help
```

授權:[MIT](LICENSE)。依賴全為 MIT/BSD/Apache-2.0(影像處理用 **Magick.NET**;
ImageSharp 因 4.x 起建置即要求授權金鑰而棄用)。

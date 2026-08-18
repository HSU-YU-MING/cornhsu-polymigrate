# Phase 輸出契約(規劃書 §3.2)

> 「可離線重跑」的前提:每個 Phase 的輸出是明確、版本化的檔案格式契約。
> 契約定案後,才能只重跑 Phase 2 不重爬,也才是 NuGet lib 元件化的基礎。
> 狀態:**自 1.0 起穩定。** 下列 CSV 格式與 frontmatter 欄位為對外契約,破壞性變更才升 major。

## 通用規則

- 所有清單一律 CSV、UTF-8 **含 BOM**(人工覆核走 Excel,無 BOM 中文會亂碼)、`\r\n` 行尾、RFC 4180 跳脫;欄位順序固定,列排序規則明確(決定性輸出,§3.10)。
- 路徑一律用 `/` 分隔、相對於各自根目錄。
- 語言一律用 BCP-47 標準 locale(§3.3)。frontmatter `lang` 與清單 lang 欄輸出 locale;
  Markdown 輸出目錄與站內連結改寫仍沿用來源站 URL 前綴(= 新站路由),此為每站的路由決策,後續可 config 化。
- `translation_key` = 去語言前綴的路徑;**以 `/` 開頭 = 無語言前綴的站級頁**(如語言選擇頁),不參與配對。
- 每份輸出檔案的 schema 變更須升 `config_version` 或在此文件記錄遷移方式。

## Phase 1 → Phase 2:鏡像 + `url_inventory.csv`

**鏡像目錄結構**(TODO 0.1 定案):

```
mirror/
  <host>/
    <path...>          # 磁碟存「解碼後」檔名(§2.6:%20 雙重編碼坑)
```

**`url_inventory.csv`**(source of truth,§2.4):

| 欄位 | 型別 | 說明 |
|---|---|---|
| url | string | 正規化後的絕對 URL |
| status | int | HTTP 狀態碼 |
| content_type | string | 回應 Content-Type |
| detected_encoding | string | 偵測到的實際編碼(§3.1) |
| mirror_path | string | 鏡像檔相對路徑 |
| lang | string | BCP-47 locale(依 lang_map 判定;無前綴 = default_lang) |
| TODO | — | 0.1 實作時逐項定案 |

排序:`url` 字典序。

## Phase 2 → Phase 3:Markdown + 清單 + 配對結果

**Markdown frontmatter**(欄位序即輸出序;`verify` 檢查前六個為必填):

```yaml
source_url: ...
lang: zh-Hant         # BCP-47(§3.3)
section: ...
slug: ...
translation_key: ...  # 去語言前綴的路徑(§1.4)
title: ...            # 內文標題優先、剝除日期前綴(§2.6)
page_type: ...        # 由 extract.section_types 分類(§classify)
flags: [...]          # needs_rebuild / text_in_image 等
text_length: 0
image_count: 0
images: [...]         # 相簿:每筆 {local, alt}
videos: [...]
documents: [...]
```

- YAML 一律由 YamlDotNet 序列化(§2.6:冒號、數字 slug、以及 YAML 1.1 會誤判成日期/數字的字串,一律強制引號)。

**`content_inventory.csv`**(含雙語缺漏表,§1.4;0.1 已定案,欄序如下):

| 欄位 | 型別 | 說明 |
|---|---|---|
| translation_key | string | 配對鍵(排序鍵,ordinal) |
| section / slug | string | 首見版本的 section 與 slug |
| has_{locale}… | True/False | 每個 locale 一欄,欄名由 lang_map 值展開(`zh-Hant` → `has_zh_hant`) |
| suggested_type | string | 工具判的頁型 |
| final_type | string | 留空,人工覆核填 |
| flags | string | `;` 分隔(如 `needs_rebuild;text_in_image`) |
| pair_status | enum | `paired` / `missing` / `heuristic_suggested` / `site_level`;單語站留空 |
| suggested_pair | string | 啟發式建議的對方 translation_key(§1.4;僅 heuristic_suggested 有值) |
| pair_evidence | string | 證據,`;` 分隔(如 `shared_media=3;date=2026-01-21`) |
| text_len_{locale}… | int | 每個 locale 一欄;缺該語版 = 0 |
| image_count | int | 各語版取最大 |
| notes | string | 留空,人工覆核填 |

配對線索只「建議」不自動合併,順序由 config `pairing.fallback` 決定,也決定 `pair_evidence` 的加權:

| 名稱 | 依據 | 備註 |
|---|---|---|
| `hreflang` | 原站 `<link rel="alternate" hreflang>` 宣告的對應 | 唯一「作者宣告」而非工具推測的線索,故通常放第一位;**不受同 section 限制**(語言版換 section 是真的會發生) |
| `shared_media` | 兩語版共用相簿圖片,值 = 共用張數 | |
| `date` | slug 內日期正規化後相等(`20240121` = `01212024`) | |
| `title_similarity` | 字元 bigram Sørensen–Dice ≥ 0.5 | 跨語言本就偏弱,故墊底 |

`hreflang` 以外的啟發式一律要求兩端同 section(跨 section 太容易巧合)。
配對分兩趟:先把 `hreflang` 配得起來的配完,啟發式才上場——同一趟內是貪婪、先到先得,
一趟做完的話宣告關係會被字典序在前的推測關係搶走對象。
無任何證據就不硬配(missing),最終決定權在人。

**`hreflang_map.csv`**(2.3 起;原站宣告的 hreflang 全紀錄,**含不可用的**):

| 欄位 | 型別 | 說明 |
|---|---|---|
| source_url | string | 宣告這則 hreflang 的頁(排序鍵,ordinal) |
| hreflang | string | 屬性原值,不正規化(`en` / `zh-Hant` / `x-default`) |
| target_url | string | 以來源頁 URL 解析後的絕對 URL |
| target_translation_key | string | 目標頁的配對鍵;不在鏡像裡則留空 |
| in_mirror | True/False | 目標頁同 host、且該路由真的抽到過一頁 |
| reciprocal | True/False | 目標頁也宣告了一則指回來的 hreflang |
| usable | True/False | 通過全部門檻,可用於配對(不代表一定產生了建議) |
| reject_reason | enum | 卡在哪一條;`usable` 為 True 時留空 |

排序:`source_url`、`hreflang`、`target_url` 字典序。恆輸出(沒宣告 hreflang 的站只有表頭)。

`reject_reason` 的值(即全部門檻,依判定順序):

| 值 | 意思 |
|---|---|
| `not_in_mirror` | 指到別的 host,或指到原站早就下架的 URL |
| `x_default` | `x-default` 是「都不符合時去哪」,不是某個語言版本 |
| `self_reference` | 指向自己。標準做法,但配對上零資訊 |
| `site_level` | 兩端任一是站級頁(`/` 開頭的 key,如語言選擇頁) |
| `ambiguous_target` | 同一個目標頁被**多個同語言**的來源頁宣告——一頁不可能是三頁的翻譯 |

為什麼守門只到「建議」為止、不直接改寫 `translation_key`,見
[hreflang_量測與決策.md](hreflang_量測與決策.md)。

`ambiguous_target` 擋的是最常見的壞法:同一段 `<link>` 被貼進全站模板,於是整站的
alternate 都指向首頁。限定「同語言」是因為多語站的合法情況長得很像——中文版與日文版
可以各自宣告同一個英文版。

**刻意不要求 `reciprocal`**——只有一半宣告在真實站上太常見,互指是可信度加分而非門檻;
這一層只建議、不合併,守門標準本來就該比直接覆寫 `translation_key` 寬。

這份檔案刻意保留不可用的宣告:它的用途是回答「這個站的 hreflang 能不能信」,
只留可用的等於把品質問題藏起來。

**`redirect_map.csv`**:`old_url, new_path, lang, translation_key`。
`new_path` 由 LinkRewriter 的同一套路由規則自動填(與內文連結改寫一致,兩者不同步 = 301 打到破頁);
人工可在 CSV 覆改。另出兩種可直接部署的 301 格式(old==new 略過防迴圈,依 old path 排序):

- `redirects.nginx.conf` — `location = {old} {{ return 301 {new}; }}`
- `_redirects` — Netlify 格式 `{old} {new} 301`

**`path_issues.csv`**(§3.4):`severity, page, issue`。
error(保留裝置名/非法字元/尾點空白/大小寫碰撞)= 該頁在**任何平台**都一致地拒寫並記錄
——兩平台產出必須相同;warning(超長路徑)照寫但記錄。恆輸出(乾淨站只有表頭)。

**執行報告**(§3.8):warning / error 分級、壞圖清單、配不起來的頁面、跳過原因;
exit code:0 乾淨 / 1 warning(壞圖、待補媒體、超長路徑)/ 2 error(不安全路徑拒寫)。

**內部加速檔(非交付物)**:`.polymigrate/media_sha1_cache.csv` —
(路徑, 大小, mtime)→ sha1 快取,沒動過的媒體免重讀(4.6GB 實測重跑 30s → 5s);
刪掉只會變慢、不影響輸出;golden 比對排除此目錄。

## Phase 4(verify)

`verify` 只讀 Phase 2/3 的輸出(Markdown + 清單),不需要網路與鏡像 —— 契約完整性的試金石。

檢查項:frontmatter 必填欄位(source_url/lang/slug/translation_key/title/page_type)、
內部連結對路由集(content 樹推導;index 檔代表目錄路由)、`/media/` 引用對磁碟
(URL 解碼後比對;`missing_images.csv` 已記錄的原站壞圖降為 warning)。

輸出:`verify_report.csv`(severity, page, kind, detail);exit code:0 乾淨 / 1 有 warning / 2 有 error(§3.8)。

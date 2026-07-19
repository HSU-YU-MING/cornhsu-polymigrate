# Phase 輸出契約(規劃書 §3.2)

> 「可離線重跑」的前提:每個 Phase 的輸出是明確、版本化的檔案格式契約。
> 契約定案後,才能只重跑 Phase 2 不重爬,也才是 NuGet lib 元件化的基礎。
> 狀態:**骨架(v0)— 欄位隨 0.1/0.5 實作逐項定案,定案前允許破壞性修改。**

## 通用規則

- 所有清單一律 UTF-8(無 BOM)CSV,欄位順序固定,列排序規則明確(決定性輸出,§3.10)。
- 路徑一律用 `/` 分隔、相對於各自根目錄。
- 語言一律用 BCP-47 標準 locale(§3.3),URL 前綴不外流到任何輸出。
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

**Markdown frontmatter 必填欄位**(TODO 0.1 定案):

```yaml
title: ...            # 內文標題優先、剝除日期前綴(§2.6)
lang: zh-Hant         # BCP-47
translation_key: ...  # 去語言前綴的路徑(§1.4)
source_url: ...
date: ...             # 正規化 ISO 8601(§2.6:YYYYMMDD vs MMDDYYYY 坑)
```

- YAML 一律由 YamlDotNet 序列化(§2.6:冒號、數字 slug 引號坑)。

**`content_inventory.csv`**(含雙語缺漏表,§1.4):

| 欄位 | 型別 | 說明 |
|---|---|---|
| translation_key | string | 配對鍵 |
| has_zh_hant / has_en / … | bool | 每個 locale 一欄(欄名由 lang_map 展開) |
| pair_status | enum | `paired` / `missing` / `heuristic_suggested` / `manual` |
| TODO | — | 0.5 實作時逐項定案 |

**`redirect_map.csv`**:舊 URL → 新路徑;MVP 出中性 CSV,匯出格式(nginx / `_redirects` / web.config)留擴充位(§3.10)。

**執行報告**(§3.8):warning / error 分級、壞圖清單、配不起來的頁面、跳過原因。格式 TODO 0.5 定案。

## Phase 4(verify)輸入

`verify` 只讀 Phase 2/3 的輸出(Markdown + 三份清單),不需要網路與鏡像 —— 契約完整性的試金石。

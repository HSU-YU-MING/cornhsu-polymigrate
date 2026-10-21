# Phase 輸出契約(規劃書 §3.2)

> 「可離線重跑」的前提:每個 Phase 的輸出是明確、版本化的檔案格式契約。
> 契約定案後,才能只重跑 Phase 2 不重爬,也才是 NuGet lib 元件化的基礎。
> 狀態:**自 1.0 起穩定。** 下列 CSV 格式與 frontmatter 欄位為對外契約,破壞性變更才升 major。

## 通用規則

- 表格清單(`*.csv`,含 `verify_report.csv`)一律 UTF-8 **含 BOM**(人工覆核走 Excel,無 BOM 中文會亂碼)、`\r\n` 行尾(最後一列也是)、RFC 4180 跳脫;欄位順序固定,列排序規則明確(決定性輸出,§3.10)。
  純文字清單 `need_fetch_media.txt`、`orphan_slugs.txt` 不是 CSV,格式見各自段落。
- **CSV 公式注入防護**(1.1.2 起):交付用 CSV 的欄位若以 `=` `+` `-` `@`、tab 或 CR 開頭,寫出時會前置一個單引號 `'`
  (Excel 才不會把它當公式執行),所以這類欄位讀回來比原始資料多一個前綴字元。內部快取 `.polymigrate/media_sha1_cache.csv` 不加。
- Markdown(`content/**/*.md`)UTF-8 **無 BOM**;結構為 `---\n` + frontmatter + `---\n\n` + 內文 + `\n`,
  `---` 圍欄與內文以 `\n` 換行。內文非空時檔尾是單一 `\n`;內文為空時(正文節點是空的,或 `images_to_gallery: true`
  把只有照片的正文清空)檔尾即為 `---\n\n\n`。
  frontmatter 內部各行的換行目前隨執行平台(Windows 為 `\r\n`、Linux/macOS 為 `\n`);YAML 解析器兩者都吃,
  但跨平台逐位元組比對前要先正規化行尾。
- 路徑一律用 `/` 分隔、相對於各自根目錄。
- 語言一律用 BCP-47 標準 locale(§3.3)。frontmatter `lang` 與清單 lang 欄輸出 locale;
  Markdown 輸出目錄與站內連結改寫仍沿用來源站 URL 前綴(= 新站路由),此為每站的路由決策,後續可 config 化。
- `translation_key` = 去語言前綴的路徑;**以 `/` 開頭 = 無語言前綴的站級頁**(如語言選擇頁),不參與配對。
- 每份輸出檔案的 schema 變更須升 `config_version` 或在此文件記錄遷移方式。

## Phase 1 → Phase 2:鏡像目錄

鏡像由現成爬蟲產出(§2.4);PolyMigrate 自己只有 `fetch-orphans` 會往裡面補檔(只寫不刪、已存在就跳過)。
`extract` 讀 `<root>/raw` 與 `<root>/media`(`--raw` / `--media` 可改位置):

```
raw/
  <URL 路徑>.html      # 原始 URL 路徑 + ".html",沒有 host 這一層:/ch/news/a.php → raw/ch/news/a.php.html
media/
  <URL 路徑>           # 磁碟存「解碼後」檔名(§2.6:%20 雙重編碼坑):/images/logo%20a.jpg → media/images/logo a.jpg
```

- `extract` 只認 `raw/` 底下的 `*.html`;第一層目錄是 `lang_map` 的 key 時即為語言前綴,下一層為 section。
- 只有一個 host:`source_url` 一律以 `site.base_url` 還原;媒體不論原本在哪個 host,都只按 URL 路徑放進 `media/`。

**`url_inventory.csv`**(§2.4 規劃的 URL 清單):**尚未實作**——目前沒有任何指令產出或讀取它,不屬於契約。
以下是規劃時的欄位草案,留著備查:

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

**Markdown frontmatter**(欄位序即輸出序;`verify` 檢查 `source_url` / `lang` / `slug` / `translation_key` / `title` / `page_type` 為必填,`section` 不在內):

```yaml
source_url: ...
lang: zh-Hant         # BCP-47(§3.3)
section: ...
slug: ...
translation_key: ...  # 去語言前綴的路徑(§1.4)
title: ...            # 內文標題優先、剝除日期前綴(§2.6)
page_type: ...        # 由 extract.section_types 分類(§classify)
flags: [...]          # config extract.section_flags 指定的值(如 needs_rebuild)+ 內建的 text_in_image
text_length: 0
image_count: 0
images: [...]         # 相簿:每筆 {local, alt}
videos: [...]         # 每筆 {type: youtube, url} 或 {type: local, src}
documents: [...]      # PDF iframe:每筆 {src, orig}(src = 媒體網路路徑,orig = 原始絕對 URL)
```

- YAML 一律由 YamlDotNet 序列化(§2.6:冒號、數字 slug、以及 YAML 1.1 會誤判成日期/數字的字串,一律強制引號)。
- 正文的 `<iframe>`:YouTube(`youtube.com/embed`、`youtube-nocookie.com/embed`、`youtu.be`)與 PDF 換成內嵌 HTML
  並記進 `videos` / `documents`;其他 iframe(地圖、行事曆、表單、Vimeo…)以原始 HTML 保留在內文原位置,
  不進 `videos` / `documents`(不猜型別)。

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
| `slug_normalized` | slug 去大小寫與 `-` `_` 空白後**完全相等** | 不是相似度比對:`/products/` 與 `/produkte/` 配不起來是對的;可靠度與對稱路徑同級 |
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
| hreflang | string | 屬性原值,只去頭尾空白,不做大小寫或格式正規化(`en` / `zh-Hant` / `x-default`) |
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

**`reject_reason` 是開放集合**:未來新增門檻會新增值,消費端須容忍沒見過的值
(當成「不可用」處理即可),不得以列舉窮盡為前提。這一條先講明,
新增門檻才不必動 `config_version`。

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
人工可在 CSV 覆改。排序:鏡像檔相對路徑(`raw/` 底下那一段)ordinal,即處理順序;
不安全路徑被拒寫的頁不出現。恆輸出。
另出兩種可直接部署的 301 格式(old==new 略過防迴圈,依 old path 排序):

- `redirects.nginx.conf` — 每列 `location = {old} { return 301 {new}; }`。
  `{old}` 寫**解碼後**的路徑(nginx 的 location 比對的是 `%XX` 解碼後的 URI,設定檔字串照字面;
  寫編碼形式的話中日韓或含空白的舊網址永遠對不上);`{new}` 維持單次編碼(Location 標頭應為 ASCII),
  其中的 `$` 另編成 `%24`(`return` 會展開變數)。兩者含空白、`; { } " ' \ # $` 或控制字元時才加雙引號,
  引號內的 `"` 與 `\` 以反斜線跳脫。排序依寫出來的(解碼後的)路徑。
- `_redirects` — Netlify 格式 `{old} {new} 301`(兩者皆為單次編碼的路徑)

兩者皆 UTF-8 無 BOM、`\n` 行尾、含結尾換行。

**`path_issues.csv`**(§3.4):`severity, page, issue`。
error(保留裝置名/非法字元或控制字元/尾點空白/兩個來源檔映到同一輸出路徑(1.1.2 起)/大小寫碰撞)
= 該頁在**任何平台**都一致地拒寫並記錄
——兩平台產出必須相同;warning(超長路徑)照寫但記錄。恆輸出(乾淨站只有表頭)。
排序:`severity` 字串 ordinal(所以 `error` 列在 `warning` 前)→ `page` ordinal。

**`media_manifest.csv`**:`local_path, original_url, referenced_by, alt, sha1, bytes`。
`local_path` 相對於輸出根目錄(媒體目錄不在輸出目錄底下時一律寫成 `media/<相對路徑>`);
`original_url` 為首見的原始絕對 URL;`referenced_by`(引用頁的 `source_url`)與 `alt`(非空的 alt 文字)
各自 ordinal 排序後以 ` | ` 連接;`sha1` 小寫十六進位、`bytes` 為檔案大小,檔案不存在時兩者皆空。
排序:媒體相對路徑(`media/` 底下那一段)ordinal。恆輸出。

**`missing_images.csv`**:`source_page, missing_image`(引用頁的 `source_url`、單次編碼的媒體網路路徑)。
排序:處理順序——鏡像檔相對路徑 ordinal,同一頁內依正文 `<img>` 出現的順序;不去重。恆輸出。
**跨 Phase**:`verify` 讀它的第 2 欄,命中的 `missing_media` 降為 `known_missing_media`(warning)。

**`need_fetch_media.txt`**:PDF iframe 與 `<video><source>` 引用、需要另外抓回來的原始絕對 URL,一行一個。
UTF-8 無 BOM、`\n` 分隔、**沒有結尾換行**(0 筆時為空檔);ordinal 排序、去重。恆輸出。

**執行報告**(§3.8):warning / error 分級、壞圖清單、配不起來的頁面、跳過原因;
`extract` 的 exit code:0 乾淨 / 1 warning(壞圖、待補媒體、超長路徑;殘檔不算)/ 2 error(不安全路徑拒寫)。
全部指令的 exit code 見文末「exit code」。

**內部加速檔(非交付物)**:`.polymigrate/media_sha1_cache.csv` —
(路徑, 大小, mtime)→ sha1 快取,沒動過的媒體免重讀(4.6GB 實測重跑 30s → 5s);
刪掉只會變慢、不影響輸出;golden 比對排除此目錄。

## Phase 4(verify)

`verify` 只讀 Phase 2/3 的輸出(Markdown + 清單),不需要網路與鏡像 —— 契約完整性的試金石。

檢查項:frontmatter 必填欄位(source_url/lang/slug/translation_key/title/page_type)、
內部連結對路由集(content 樹推導;index 檔代表目錄路由)、`/media/` 引用對磁碟
(URL 解碼後比對;`missing_images.csv` 已記錄的原站壞圖降為 warning)、
孤島頁(2.2 起:沒有任何內容頁連到的頁;內建豁免站根與各語言首頁——深度 ≤ 1 的目錄 index——
以及 `page_type: listing` 的列表頁,其餘可用 `--allow-unlinked <file>` 豁免,一行一頁、content 相對路徑或路由皆可、`#` 開頭為註解)。

輸出:`verify_report.csv`(severity, page, kind, detail);`content/` 不存在時不寫。
排序:`severity` 字串 ordinal(`error` 在 `warning` 前)→ `page` ordinal → `detail` ordinal。`kind` 目前的值:

| kind | severity | detail |
|---|---|---|
| `no_content` | error | `content/` 不存在(此時不寫報告檔) |
| `invalid_frontmatter` | error | 沒有 frontmatter 區塊,或 YAML 解析錯誤訊息 |
| `missing_field` | error | 缺少(或不是非空字串)的必填欄位名 |
| `missing_media` | error | 原始引用字串 |
| `known_missing_media` | warning | 原始引用字串(已記錄於 `missing_images.csv`) |
| `broken_link` | error | 原始引用字串 |
| `unlinked_page` | warning | `not linked from any content page (menus are not checked)` |

exit code:0 乾淨 / 1 有 warning / 2 有 error(§3.8)。2.2 新增 `unlinked_page` 之後,
原本 exit 0 的專案可能變成 exit 1。

## `probe-orphans` → `fetch-orphans`:`orphan_slugs.txt`

`probe-orphans` 的輸出、`fetch-orphans --slugs` 的預設輸入(皆預設 `<root>/orphan_slugs.txt`)。
一行一個 slug,依探測順序;UTF-8 無 BOM、`\n` 分隔、**沒有結尾換行**;每次探測跑完整份覆寫。
例外:**一個回答都沒拿到**(每個候選都是連線錯誤、逾時、重試後仍 409,或 401/403/429/5xx 這類
不是「有/沒有」的狀態)時不寫——既有檔原樣保留,沒有檔也不建空檔(空清單會被讀成「沒有孤兒頁」),
stderr 印 `warning:` 並列出原因,exit code 仍是 0。部分候選沒拿到回答時照寫,stderr 警告清單可能不完整。
「確定沒有」的回答是 404、410、其他 2xx,以及 3xx(probe 不跟轉址,轉址頁不算命中)。
`fetch-orphans` 讀取時逐行去頭尾空白、略過空行,所以 `slugs` 的輸出或手寫清單也能直接餵。
清單以 UTF-8 讀取(有 BOM 時依 BOM 判斷 UTF-8 / UTF-16 / UTF-32)。

但書:`slugs` 寫的是 stdout,行尾為 `Environment.NewLine`(Windows 為 `\r\n`),不是上面 `orphan_slugs.txt`
的固定格式;行尾不影響 `fetch-orphans`(逐行讀取)。編碼:
- **stdout 被導到檔案時(cmd、Git Bash 的 `>`)一律是無 BOM 的 UTF-8**,與 console 碼頁無關——
  在 zh-TW Windows(cp950)上導檔,`fetch-orphans` 也讀得回中日韓 slug。
- 接到 console(沒有導向)或管線時跟著 console 的輸出編碼:Windows PowerShell 5.1(與 7.4 之前的 PowerShell)
  讀原生指令輸出走管線、依 console 編碼解讀後再用自己的編碼寫檔(5.1 預設含 BOM 的 UTF-16,`fetch-orphans` 讀得回來),
  所以 PowerShell 裡導檔不受影響。**Git Bash 的管線**(例如 `slugs … | sort > x.txt`)仍是 console 碼頁:
  先把碼頁切成 UTF-8(`chcp.com 65001`)再跑。
- Linux / macOS 的 console 編碼取自 locale,一般就是 UTF-8,沒有這個問題。

## exit code

0 乾淨 / 1 warning / 2 error / 130 中斷。所有指令的用法錯誤、config 錯誤、IO 錯誤一律 2;未知指令 2。
config 的 CSS selector(`extract.content`、`type_rules.*.title_selector` / `remove_selectors`)載入時不檢查語法,
改在用到之前才檢查:`extract` 在處理任何頁之前、`fetch-orphans` 在寫入抓到的頁之前;寫壞 → 2,而且什麼都還沒寫。
寫壞但這次沒有頁用到的 selector 不擋(例如沒有任何頁屬於那個頁型)。

| 指令 | 0 | 1 | 2(除上述共通情況) | 130 |
|---|---|---|---|---|
| `extract` | 乾淨 | 壞圖、待補媒體、超長路徑 | 不安全路徑拒寫、鏡像目錄不存在 | — |
| `thumbs` | 全部建立或已存在 | 任一張失敗 | 媒體目錄不存在 | — |
| `probe-orphans` | 跑完(不論命中幾筆;一個回答都沒拿到時也是 0,但不寫清單,見上) | — | `--years` 不合法 | Ctrl-C |
| `fetch-orphans` | 沒有任何頁出錯 | 任一頁出錯(非 200、非 HTML、連線失敗、逾時) | slug 清單不存在 | Ctrl-C |
| `verify` | 無 issue | 只有 warning | 有 error;`--allow-unlinked` 檔不存在 | — |
| `slugs` | 列出成功(含空清單) | — | 找不到該 section 目錄;`--section` / `--lang` 不是單一目錄名 | — |

經 npm(`npx cornhsu-polymigrate`、全域安裝的 `polymigrate`)執行時,中間多一層啟動腳本 `bin/polymigrate.js`:
PolyMigrate 自己的 exit code 原樣轉傳;被訊號中斷時為 128+訊號編號(SIGINT → 130)。
**啟動腳本自己失敗**(平台不在支援表、平台包沒裝、執行檔起不來)時 PolyMigrate 根本沒有執行,
目前回 **1**,stderr 以 `error:` 開頭並註明 `PolyMigrate did not run.` 與「這個 1 不是 warning」——
這個 1 與上表的「1 = 只有 warning」撞號,要不要改成 2 待維護者裁定(既有情境的 exit code 改變屬 major)。

130 目前只有 `probe-orphans` / `fetch-orphans` 回得出來:其餘指令是同步執行、不接取消訊號,
第一次 Ctrl-C 不會中斷它們,第二次由 runtime 直接終止行程,不經 PolyMigrate 收尾,結束碼由作業系統決定。

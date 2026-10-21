# Changelog

版本規則:preview 期間破壞性修改不另行公告;1.0 起新功能升 minor,修正升 patch。

## 未發佈

**產品 bug 修正累積中,目前全部是 patch 或 none(下一版可為 2.3.2)。** 以下每一條都標了 semver;
CLI 參數、輸出檔欄位、YAML 欄位、函式庫的 public 面(`PublicApi.approved.txt`)都沒有動。exit code 只有兩處
從契約外拉回契約內:selector 寫壞時的未處理例外改為 2、同步指令的第一次 Ctrl-C 改為 130。會改變既有情境
exit code 的那一半(例如 `probe-orphans` 一個回答都沒拿到時要不要非零結束、npm 啟動腳本失敗時要不要回 2)
留給維護者裁定,沒有做。

- **修正(patch):config 的 CSS selector 寫壞時,`extract` 與 `fetch-orphans` 以未處理例外崩潰。**
  `extract.content`、`type_rules.*.title_selector`、`type_rules.*.remove_selectors` 不合語法時,
  AngleSharp 的 `DomException` 直接穿出去,行程以未處理例外結束(Windows 為 `0xE0434352`),
  不在 0/1/2/130 契約內。`extract` 還會先把前面的頁寫進 `content/`、清單檔一份都沒寫;
  `fetch-orphans` 則是頁面先寫進 `raw/` 才掃資產,修好 config 重跑時那一頁被當成已抓過,
  它的資產永遠補不回來。現在是 **exit 2**,訊息點名欄位
  (`extract.content: 'section[id' is not a valid CSS selector.`),而且在寫任何檔之前就停。
  **載入 config 時仍刻意不驗 selector**:寫壞但這次沒有頁用到的規則照舊不擋——在載入時擋,
  原本能跑的 config 會開始失敗。函式庫:`PolyMigrator.Extract`、`ExtractionPipeline.Run`、
  `OrphanFetcher.FetchAsync` 在這個情況改擲 `ConfigException`(原本是 AngleSharp 的 `DomException`)。
- **修正(patch):`probe-orphans` 一個回答都沒拿到時,把既有的 `orphan_slugs.txt` 覆寫成空檔。**
  連線錯誤、逾時、403/429/5xx 原本一律當成「不存在」,全部失敗時照樣「跑完、0 筆」——先前探好的清單
  (預設延遲下一年約 36 分鐘的請求量)被清空,而空清單會被排程讀成「沒有孤兒頁」。現在分開統計
  「確定沒有」(404、410、3xx)與「沒拿到回答」:**一個回答都沒拿到時不寫 `--out`**(既有檔原樣保留,
  沒有檔也不建空檔),stderr 印 `warning:` 並列出原因(例如 `HTTP 503: 732`);部分沒拿到回答時照寫,
  另警告清單可能不完整。**exit code 不變(仍是 0)**——要不要改成非零屬既有情境的 exit code 改變,
  留給維護者裁定。
- **修正(patch;verify 的 exit code 變動待裁定,見下):YouTube 與 PDF 以外的 `<iframe>` 在 Markdown 裡整個消失。** 程式註解與 README 都說
  其他 iframe「保留原樣」,實際上它們留在 DOM 裡交給 ReverseMarkdown,而未知標籤的處理是只輸出子節點——
  iframe 沒有子節點,於是 Google 地圖、行事曆、表單、Vimeo 等嵌入靜默消失,沒有任何清單或 warning 記得。
  現在以原始 HTML 保留在內文原位置(不進 `videos` / `documents`,不猜型別),站內的 `src` 與 `<a href>`
  走同一套路由改寫(外站與完整網址不動、相對路徑以頁面網址解析)。另外
  `youtube-nocookie.com/embed`(YouTube 的隱私強化模式)現在也認成 YouTube 影片。
  **升級注意:** 重跑 `extract` 後,含這類 iframe 的頁 `.md` 內文會多出 `<iframe …>`;
  用 `youtube-nocookie.com` 嵌入的頁,frontmatter `videos` 會多一筆 `{type: youtube, url}`。
  **`verify` 也會看到這些 iframe**:指向「沒有搬過來的站內頁」(例如舊站的表單頁)的 iframe 會報
  `broken_link`,**`verify` 因此可能由 exit 0/1 變成 2**——原本那個嵌入在 Markdown 裡根本不存在,
  現在它在新站上確實是壞的。這是既有情境的 exit code 改變,semver 判讀(patch 或留到 major)待維護者裁定。
- **修正(patch):npm 啟動腳本自己失敗時,訊息改英文並明講 PolyMigrate 沒有執行。**
  平台不在支援表、平台包沒裝(`--no-optional` / `--omit=optional` 或安裝中斷)、執行檔起不來這三種情況,
  原本印繁體中文(CLI 其他輸出都是英文),也沒說工具根本沒跑。現在 stderr 以 `error:` 開頭、
  註明 `PolyMigrate did not run.`,並說明這個 exit code 1 來自啟動腳本、不是 PolyMigrate 的 warning。
  **exit code 仍是 1**——它和契約的「1 = 只有 warning」撞號,依「0/1 放行」的 CI 會把「工具沒跑」
  當成通過;改成 2 屬既有情境的 exit code 改變,留給維護者裁定。
- **修正(patch):`redirects.nginx.conf` 對中日韓或含空白的舊網址寫出永遠不會命中的 `location`。**
  舊路徑原本寫成百分號編碼(`location = /ch/news/%E7%A6%AA%E4%BF%AE.php`),但 nginx 的 location
  比對的是 `%XX` **解碼後**的 URI、設定檔字串照字面(ngx_http_core_module 文件「location」段),
  所以這些 301 永遠不會命中,舊網址直接 404;含空白的路徑更會讓 `nginx -t` 整份設定載入失敗。
  現在 `{old}` 寫解碼後的路徑(`location = /ch/news/禪修.php`),含空白或 `; { } " ' \ # $` 時加雙引號並跳脫;
  `return 301` 的目標維持編碼(另把 `$` 編成 `%24`,避免被當成 nginx 變數)。排序改依寫出來的
  (解碼後的)路徑。格式 `location = {old} { return 301 {new}; }` 不變,純 ASCII 的路徑逐位元組相同。
  **升級注意:重跑 `extract` 後,含非 ASCII 或特殊字元的列會改變寫法與順序,部署前請重新 `nginx -t`。**
  `_redirects`(Netlify)不變——Netlify 怎麼比對編碼路徑沒有查到文件,未經實測前不動。
- **修正(patch):`probe-orphans` 漏探已鏡像日期的後綴孤兒,又重複列出已鏡像的後綴版。**
  日期本身已在鏡像裡時,原本連 `A`～`D` 後綴都不探,所以「原頁已鏡像、`A` 版是孤兒」永遠找不到——
  而那正是 probe 要找的東西;後綴變體則不比對已鏡像的 slug,已經抓過的 `A` 版照樣列出。
  現在已鏡像的日期本身不探、後綴照探;已鏡像的後綴版不探也不列,「`A` 有才會有 `B`」的鏈繼續往下。
  **升級注意:`orphan_slugs.txt` 可能多出原本漏掉的後綴孤兒、少掉已鏡像的後綴版;
  每個已鏡像的日期多一個 HEAD 請求(探它的 `A`)。**
- **修正(patch):沒有語言前綴的站(`lang_map` 的 key 是 `""`)在 `probe-orphans` / `fetch-orphans`
  請求 `//news/…` 這種雙斜線網址。** nginx 預設合併斜線所以看不出來,但不合併斜線的伺服器或框架路由
  會讓 probe 全部落空、fetch 全部 404。現在只接非空的路徑段(`/news/…`);進度訊息與
  `fetch-orphans` 的錯誤列(`[404] news/…`)同樣不再多一個斜線。
- **修正(patch):`slugs`(與 `probe-orphans` 的已知 slug 集、函式庫的 `PolyMigrator.MirrorSlugs`)
  在檔名第一個點就截斷,和 `extract` 寫出的 slug 不一致。** `raw/ch/news/v1.2-release.php.html` 原本印 `v1`
  (`extract` 寫的是 `slug: v1.2-release`),餵給 `fetch-orphans --slugs` 就去抓不存在的網址。
  `probe-orphans` 手上有 config,已知 slug 集改用 `url_pattern.strip_extensions`,與 `extract` 完全相同的規則。
  `slugs` 與 `PolyMigrator.MirrorSlugs` 不帶 config、不知道 `strip_extensions`,改成去掉結尾的 `.html`
  再去掉一個全是 ASCII 字母的副檔名(`.php`、`.asp`、`.aspx`…),`2019.05.01.html` 這類乾淨網址不會被多剝——
  **這仍是啟發式,大多數站與 `extract` 一致,但不是全部**:乾淨網址站的 `jane.doe.html` 會印成 `jane`,
  `strip_extensions: []` 的站上 `foo.php.html` 會印成 `foo`(`extract` 的 slug 分別是 `jane.doe`、`foo.php`)。
  **升級注意:含點的 slug 輸出會改變**(原本的截斷值本來就無法回餵 `fetch-orphans`)。列表頁 `index` 照舊列出。
- **修正(patch):Windows 上 `slugs` 導成檔案時跟著 console 碼頁,回餵 `fetch-orphans` 變成亂碼網址。**
  2.2.0 與 `docs/搬完之後你還要做的事.md` 教的就是 `slugs … > local.txt` 這條管線;在 zh-TW Windows(cp950)
  用 cmd 或 Git Bash 導檔,`禪修` 寫成 Big5,`fetch-orphans` 以 UTF-8 讀回成 U+FFFD。現在 **stdout 導到檔案時
  一律寫無 BOM 的 UTF-8**。接到 console 或管線時刻意維持 console 編碼:Windows PowerShell 5.1 讀原生指令輸出
  走管線、依 console 編碼解讀,那條路改寫 UTF-8 反而會變亂碼。行尾仍是 `Environment.NewLine`。
  **升級注意:Windows 上導檔得到的非 ASCII slug 位元組會改變**(原本的值本來就讀不回來)。
- **修正(patch):`content_inventory.csv` 的 `pair_evidence` 隨機器文化改變。** `date=` 與 `title_similarity=`
  原本以目前文化格式化:th-TH 寫出佛曆(`date=2569-02-14`)、fa-IR 寫出波斯曆、de-DE 寫出 `0,84`
  (CSV 欄位因此被加引號),違反決定性輸出,也與契約範例的 ISO 日期不符。現在一律 invariant。
  配對決策本身不受影響;en-US 等文化的輸出不變。
- **修正(patch):Windows 上 Markdown 的 frontmatter 是 CRLF,同一個檔混用兩種行尾。** YamlDotNet 預設用
  `Environment.NewLine`,所以 Windows 上 frontmatter 各行是 `\r\n`、圍欄與內文卻是 `\n`,兩平台產出的位元組不同
  (YAML 解析器兩者都吃,不會壞,但跨平台比對與 git diff 會有雜訊)。現在整檔一律 `\n`。
  golden 比對原本把所有 `\r` 刪掉再比,所以三平台 CI 都沒抓到——Markdown 現在逐字比對,
  基準也重產過(只少了 `\r`)。**升級注意:Windows 上重跑 `extract` 後,每一個 `.md` 的位元組都會改變
  (只有行尾),第一次 commit 會是整片的行尾 diff。**
- **修正(patch):`extract`、`thumbs`、`verify`、`slugs` 按 Ctrl-C 拿不到 130,第一次 Ctrl-C 也沒有作用。**
  2.2.0 說 `extract` / `thumbs` 會乾淨回 130,但只有 `probe-orphans` / `fetch-orphans` 真的看取消訊號:
  同步指令按一次 Ctrl-C 照常跑完、回 0/1/2,按第二次由 runtime 直接終止(Windows 結束碼 `0xC000013A`)。
  現在第一次 Ctrl-C 就在下一頁/下一張圖/下一個媒體檔的雜湊之前停下,stderr 印 `cancelled.`、回 **130**;已寫出的 `.md` 與縮圖保留,
  清單檔、`verify_report.csv` 不會只寫一半,`slugs` 不印半份清單。第二次 Ctrl-C 的行為不變。
  對逐指令的 exit code 表而言是 130 欄由「—」變成可達(增補,不改既有情境)。
- **修正(patch):`probe-orphans` 要整批探完才發現 `--out` 寫不出去。** 目錄不存在或 `--out` 指到一個目錄時,
  原本先發完一整輪請求(一年 730 個以上,預設延遲下約 36 分鐘)才在寫檔時 exit 2。現在探測前就檢查,
  一個請求都不發;檢查不留痕跡(既有檔內容與修改時間不變,原本沒有的檔不會被建成空檔)。exit code 仍是 2。
  (中斷時把已命中的部分寫出**刻意不做**:那會用半份清單蓋掉上一次完整的清單;命中的 slug 已即時印在
  stdout 的 `hit …` 行。)
- **修正(patch):`content/` 不存在時,`verify` 的摘要印出一個從未寫出的報告路徑,媒體列也印 `media refs : 0`。**
  exit 2 與 `[error] no_content` 明細本來就對,只有摘要誤導。現在印 `report : (not written)` 與
  `media checks : skipped (content directory not found)`。
- **文件(none):hreflang 讀的是整份文件的 `<link rel="alternate" hreflang>`,不只 `<head>`。**
  程式註解與 `docs/hreflang_量測與決策.md` 原本寫「`<head>` 裡的」,與實作不符;現在照實寫,並以測試釘住。
  行為沒有改:舊站的 `<head>` 常被 include 檔的 BOM 提早截斷、`<link>` 落進 body,只讀 head 會讓這類站的
  宣告整批消失。要不要改成只讀 head 留給維護者裁定。
- **修正(patch):CLI 內建說明和 parser 不一致,也漏寫預設值與別名。** `extract` 參數錯誤時印的 `Usage:`
  沒有 `--dry-run`(與 `--help` 不同);六個指令的語法列現在只寫一次,`--help` 與參數錯誤的 `Usage:` 共用。
  `--help` 另補上:`--years` 可只給一年、`probe-orphans` 的 `--lang` 預設是 `lang_map` 第一個 key、
  `probe-orphans --out` 與 `fetch-orphans --slugs` 預設 `<root>/orphan_slugs.txt`、`-h` / `-v` 別名,
  以及「`--root` 預設是 config 所在目錄」。參數行為沒有任何改變。
- **文件(none):`PolyMigrator` 的 XML 文件不再把 public 型別稱為「內部實作型別」。**
  `ExtractionPipeline`、`OutputVerifier`、`ThumbnailGenerator`、`OrphanProber`、`OrphanFetcher` 等自 2.0.0 起就是
  public、已經出貨,屬於公開面;這段文件會打包進 `PolyMigrate.Core.xml` 出現在 IntelliSense,原本的說法
  會讓人以為它們可以隨時被換掉。現在寫成「較底層的進階用途、同樣受 semver 約束」,並點明探測/抓回孤兒頁
  目前沒有 facade 入口。(隨下一版 nupkg 更新。)
- **文件(none):README 的相對連結改成絕對網址**(`622edd4`)。README.md 隨 NuGet 與 npm 主套件出貨,
  nuget.org 會把相對連結清成 `href=""`,v2.3.1 套件頁上的 License 徽章、CHANGELOG、THIRD-PARTY-NOTICES、
  繁體中文說明、hreflang 文件、examples 都是死連結。新增測試擋住之後再貼回相對路徑。

## 2.3.1

**兩件已經壞了三個版本、但沒有人回報的事。** 對外契約與 exit code 不變。

- **修正:npm 通路在 Windows / Linux arm64 上完全裝不起來**(影響 2.1.0～2.3.0)。
  `bin/polymigrate.js` 的平台對照表停在四個平台,缺 `win32 arm64` 與 `linux arm64`——
  而 2.1.0 明明已經在 `release.yml`、`npm/prepare.mjs` 與主套件的 `optionalDependencies`
  三處都補上了 arm64。**症狀特別隱蔽**:npm 依 `os`/`cpu` 正確下載了對應的平台包、
  安裝完全成功,但啟動腳本查不到自己的平台鍵,回一句「沒有預先建置版本」就 exit 1——
  看起來像沒發布那個平台,實際上檔案就躺在 `node_modules` 裡。
  根因是**同一份平台清單散在四個地方**,加平台時漏掉了最後一處;現在對照表旁邊留了
  「要與 `prepare.mjs` 一致」的警語,`CLAUDE.md` 也記下了那四個地方的完整清單。
- **修正:README 開頭的 License 徽章跑版**。`8244632` 本意是在文末加一節「See also」,
  結果整段被貼進了徽章的 alt text 裡,於是徽章不呈現、而文末那一節根本不存在
  (中文版的「另見」是好的,只有英文版壞)。**這份 README 隨 NuGet 套件
  (`PackageReadmeFile`)與 npm 主套件一起出貨**,所以壞掉的版面是直接掛在
  nuget.org 與 npmjs 的套件頁上給人看的。徽章已還原,See also 補到 License 段之前
  (與中文版同位置),安裝說明的平台清單也從四個更正為六個。

### 內部

- **`NuGet/login` 改以 commit SHA 釘選**(`8d19675…` = v1.2.0)。它是發佈鏈上唯一
  非 `actions/*` 官方命名空間、又直接經手憑證的第三方 action(拿 OIDC token 去
  nuget.org 換臨時 API key)。tag 可以被上游挪動到任意 commit,SHA 不行——
  四個姊妹套件的發佈完整性原本都繫在同一個 `v1` tag 上。**本版是第一次實際走過
  這條釘選後的路徑。**
- **刪除一次性的 `npm-backfill.yml`**。它是 2.1.0 補發時的產物,檔頭自己就寫著
  「補完即可刪除」。刪除前核對過七個 npm 套件的 2.1.0 全數在線,且 2.2.0～2.3.0
  都由 `release.yml` 的 OIDC 信任發布正常發出——token 通道不再需要。repo 的
  secret 清單同時確認為空,發佈路徑收斂成 OIDC 一條。
- 新增 `CLAUDE.md`:開發慣例、npm 發佈線的兩種認證互卡、對外契約的邊界、
  以及從 git log 整理出的已知教訓。

## 2.3.0

**開始讀原站自己宣告的 `hreflang`。** 對外契約只增不改:既有欄位、exit code、CLI 介面全部不變,
`pairing.fallback` 沒列 `hreflang` 的 config 行為與 2.2 逐位元相同。

- **新增輸出 `hreflang_map.csv`**:原站每一則 `<link rel="alternate" hreflang>` 的全紀錄,
  附 `in_mirror` / `reciprocal` / `usable` / `reject_reason` 四欄。**含不可用的宣告**——這份檔案的用途是回答
  「這個站的 hreflang 能不能信」,只留可用的等於把品質問題藏起來。恆輸出,沒宣告的站只有表頭。
- **新增配對線索 `hreflang`**,可放進 `pairing.fallback`(建議放第一位,見
  `examples/ibps-austin.yaml`)。它是全站唯一由作者**宣告**而非工具**推測**的配對關係,
  所以是唯一**不受「兩端須同 section」限制**的線索——中文在 `/ch/news/`、英文在 `/en/press/`
  是真的會發生,而那正是對稱路徑配不起來的原因之一。
- **宣告不等於正確,所以要查證。** 會需要搬遷的老站正好就是 hreflang 壞掉的那些站。
  一則宣告要能用於配對,必須通過五道門,不可用的原因照實寫進 `reject_reason` 欄:
  `not_in_mirror`(指到別的 host 或早就 404 的 URL——**含 host 要一致**,不少站的英文版在
  `en.example.org`,只比路徑的話 `/ch/news/a.php` 會配到自己身上)、`x_default`、
  `self_reference`、`site_level`、`ambiguous_target`。
  **`ambiguous_target` 擋的是最常見的那一種**:同一段 `<link>` 被貼進全站模板,
  於是每一頁中文都宣告英文首頁是自己的英文版。一頁不可能是三頁的翻譯,所以那些宣告
  全部作廢——否則會產出「英文首頁 = 某篇中文新聞」這種建議,還掛著 hreflang 這個最權威的
  證據標籤,比誠實回報 missing 糟得多。限定「同語言」是為了不誤傷多語站的合法情況
  (中文版與日文版可以各自宣告同一個英文版)。
  **刻意不要求互指**:只有一半宣告在真實站上太常見,互指是可信度加分而非門檻,照實記在
  `reciprocal` 欄由人判斷——這一層只建議、不合併,守門標準本來就該比直接覆寫
  `translation_key` 寬。
- **新增配對線索 `slug_normalized`**:slug 去大小寫與 `-` `_` 空白後**完全相等**即成立。
  這是實跑參考站 528 頁鏡像找出來的洞——光明燈法會中文 `2025-light-offering`、
  英文 `2025_light_offering`,只差一個分隔符,而三個既有啟發式全部落空:
  圖是同一張的兩份拷貝各放自己語言目錄(`shared_media` 不成立)、slug 無日期、
  標題一中一英(相似度 0)。加上這條之後,那個站的建議數從 0 變 1,其餘輸出逐檔不變。
  **這不是「URL 字串相似度」**:那個做法配的是 `/products/` 與 `/produkte/`,
  不同語言的同一個意思字面上根本不像,純粹在猜;這裡要求正規化後完全相等,
  可靠度與「去語言前綴後路徑相等」同級。
- **配對改成兩趟**:`hreflang` 配得起來的先配完,啟發式才上場。同一趟內是貪婪配對、
  先到先得,而「先到」只是 translation_key 的字典序——一趟做完的話,宣告的關係會被
  字典序剛好排在前面的共用相簿搶走對象,等於讓推測贏過宣告。
- **`extract` 摘要多一行** `hreflang links : N declared, M passed validation (…)`。
  措辭刻意是「通過查證」而不是「可用」:通過查證不等於配到了對象,更不等於有被用——
  `pairing.fallback` 沒列 `hreflang` 的話,這兩個數字純粹是觀察,摘要會直接講出來。
- **`reject_reason` 明定為開放集合**(見 `docs/contracts.md`):未來新增門檻會新增值,
  消費端須容忍沒見過的值。先講明,以後新增門檻才不必動 `config_version`。
- **新增 `docs/hreflang_量測與決策.md`**:為什麼只拿 hreflang「建議」而不直接配,
  背後的量測(唯一可驗證的真實站:528 頁、hreflang 0 則)、以及要重新考慮的觸發條件。

### 內部

這幾筆在 2.2.1 之後就進了 main,隨這一版一起發出去,對工具行為沒有影響:

- **新增 bug 回報範本與資安通報政策**(`.github/ISSUE_TEMPLATE/bug_report.yml`、`SECURITY.md`)。
  範本會問到 `verify_report.csv` / `path_issues.csv`,那兩份通常一貼上來就省掉一輪往返。
- **CI workflow 明確宣告最小權限**(`permissions: contents: read`),不吃 repo 的預設值——
  預設值哪天被改寬,workflow 會跟著變寬而且沒有人會發現。

### 升級注意

- **輸出目錄會多一個檔案**(`hreflang_map.csv`)。有在比對輸出檔案清單的下游(golden 測之類)
  要更新一次基準。

- `pairing.fallback` 加上 `hreflang` 之後,原本靠 `shared_media` / `date` / `title_similarity`
  建議的那些配對,`pair_evidence` 可能多出 `hreflang=…` 一項,建議對象也可能改變
  (證據強的贏)。`content_inventory.csv` 是給人覆核的,不是給程式吃的,但若你有下游腳本
  在讀 `pair_evidence`,注意它現在可能以 `hreflang=` 開頭。
- 不想要這個行為就別把 `hreflang` 列進 `pairing.fallback`——預設沒有它。

## 2.2.1

**送到使用者手上之後才看得到的三件事。** 對外契約與 exit code 不變。

- **`extract` 會回報殘檔**:`content/` 裡存在、但這一次沒被寫到的 `.md`。
  `extract` 只寫不刪(刻意如此——「只寫不刪」正是 `raw/` 與 `media/` 這兩份不可再生資料
  安全的原因),但舊站下架一篇文章後重跑,舊的 `.md` 會永遠留著,於是回報的
  「markdown files」與磁碟上的實際檔數從此對不起來,而原本沒有任何地方會提。
  現在會列出來讓人自己決定要不要刪。**刻意不計入 warning**:那會讓所有有殘檔的
  既有使用者從 exit 0 變 1,而 exit code 是對外契約,不在 patch 版動它。
- **`unlinked_page` 的指引補上「這可能是殘檔」**。原本只建議「加連結、加選單項目、
  或豁免」——但如果那頁其實是上一輪的殘留,這三條沒有一條是對的,正確動作是刪掉它。
- **README 說明 `raw/` 不可再生**(中英文)。以禮貌間隔重爬一個站要好幾個小時,
  而來源站很可能在搬完那一刻就被關掉——那時鏡像就是僅存的一份。原本文件裡一句都沒提。

### 內部

- **從原始碼建置不再自稱 1.0.0。** `Directory.Build.props` 原本刻意不寫 `<Version>`,
  於是退回 MSBuild 預設的 `1.0.0`——而 `1.0.0` 是 PolyMigrate **真的發行過**的版本,
  所以本機建出來的執行檔不是自稱一個不存在的版本,而是自稱一個存在、但不是它自己的版本,
  debug 時會把人帶去錯的方向。改為固定 `0.0.0-dev`(npm 那條線的 `package.json` 早就用
  `0.0.0-placeholder` 做同一件事,這裡只是把 .NET 這半補齊)。
  發出去的版本完全不受影響:`release.yml` 與 `npm-backfill.yml` 一律以 `-p:Version=`
  覆寫。規則寫進 `RELEASING.md`——不寫下來的話,下次還是會有人手動改那一格。
- 新增 README 與 `--help` 的指令清單一致性測試。這個走鐘實際發生過兩次:2.2.0 開發期間
  README 寫 `slugs out/` 而 `--help` 寫 `<root>`,撐過一整輪人工審視;同一次改動裡
  英文 README 加了 `slugs`、中文 README 漏掉,也沒人當場看出來。只比對「有哪些指令」,
  不比對說明文字——逐字比對只會製造假警報,反而讓人把測試關掉。
- 中文 README 補上遺漏的 `slugs`,並同步 `verify` 的說明。

## 2.2.0

**`verify` 補上一整類抓不到的錯:孤島頁面。** Phase 輸出契約(CSV 欄位、frontmatter)不變,
新增的只是 `verify_report.csv` 裡多一種 `kind`。

### ⚠️ 升級後 `verify` 可能從 exit 0 變 exit 1

孤島頁面判為 **warning**,而 warning 會讓 `verify` 回傳 **1**。在此之前唯一的 warning 是
`known_missing_media`(少見),所以多數專案現在拿到的是 0 —— 升級後若站上真的有孤島頁,
CI 腳本(尤其 `set -e`)會從綠燈變紅燈,**即使你什麼都沒改**。

內建豁免已涵蓋「本來就不會被內容頁連到」的三類(站根、各語言首頁、`page_type: listing`
的分類列表頁),正常專案應為 0 孤島。真的要豁免其他頁:

```sh
polymigrate verify out/ --allow-unlinked allow_unlinked.txt
```

檔案一行一頁,`content` 相對路徑或路由皆可,`#` 開頭為註解。

### 新增

- **孤島頁面偵測**:輸出裡存在、卻沒有任何內容頁連到的頁 → `unlinked_page` warning。
  這是 `verify` 原本的系統性盲區 —— 沿著連結走的巡檢**走不到**這種頁,於是回報「全部正常」,
  而「零錯誤」在此是誤導:它證明的是「連得到的頁都正常」,不是「所有頁都正常」。
  實作只用既有資料(路由集 ∖ 被引用集),不多掃一趟、不碰網路。
  **界線**:選單不在 Phase 2 輸出裡,verify 看不到它,故訊息措辭是
  「not linked from any content page (menus are not checked)」而非「沒有入口」。
- `polymigrate verify --allow-unlinked <file>`;函式庫端為 `PolyMigrator.Verify(..., allowUnlinked:)`。
  刻意**不**放進 site config —— `verify` 的「只讀輸出、不需 config」是它的設計前提。
- **`polymigrate slugs <root> --section <name>`**:列出 `raw/` 底下已鏡像的 slug,
  字典序、跨語言去重、一行一個。並行期(新舊站同時在線)用來跟舊站現況求差集:

  ```sh
  polymigrate slugs . --section news > local.txt         # 「.」= 放 raw/ 的那一層
  comm -13 local.txt oldsite.txt > missing.txt          # oldsite.txt 由你的腳本產生
  polymigrate fetch-orphans site.yaml --section news --slugs missing.txt
  ```

  **stdout 只有 slug**,摘要走 stderr,所以可以直接導成檔案。純本地、不碰網路、不需 config。
  函式庫端為 `PolyMigrator.MirrorSlugs(rawDir, section, langPrefix)`。

  這裡刻意**只做本地那一半**。「去舊站讀列表頁、挑出 slug」沒有做:它因站而異
  (香雲寺的活動根本沒有列表頁,`/ch/events/index.php` 回 404,是從首頁卡片連出去的),
  而且會**安靜地壞掉**——舊站一改版、regex 抓不到東西,排程只會每天回報「沒有新文章」,
  你以為在追平其實早就斷了。那一半本來就是十幾行的一次性腳本,留給使用者。
  對應地,`--section` 打錯時回傳 **exit 2 而非空清單**:空清單會被讀成「沒有新文章」。

### 修正

- **中日韓 slug 的百分號編碼連結不再被誤判**。瀏覽器與編輯器產出中文 href 預設是編碼的
  (`/ch/news/%E7%A6%AA%E4%BF%AE`),而鏡像檔名是解碼的(契約 §2.6)——原本直接比字串,
  會把「存在、而且真的被連到」的頁報成 `broken_link`,2.2 之後還連帶誤報成 `unlinked_page`,
  **一個正確的連結產生兩筆假發現**。對 i18n-first 的工具來說那是旗艦情境。
  改為原樣對不上時才多試一次解碼版:原本就對得上的維持逐位元組不變(golden 不動),
  解完還是對不上就照樣報壞連結、且回報原始寫法。此為 1.0 起就存在的缺陷。
- **`slugs` 拒絕絕對路徑與 `..` 的 `--section` / `--lang`**。`Path.Combine` 只要後段是
  絕對路徑就會丟掉前段,所以 `--lang C:\somewhere` 會安靜地讀到鏡像目錄以外的地方、
  還回報成功。本機 CLI 上這不是提權,該擋的理由是它做了你沒要求的事卻不出聲。
  空字串仍合法(無語言前綴的站)。
- **自己連自己不再算「有入口」**:訪客得先有辦法到那一頁,才看得到頁上那個指向自己的
  連結。原本一個 canonical 或麵包屑的自我連結,就足以讓那頁靜靜逃掉孤島偵測。
- **`slugs` 只認 `*.html`**(與 `extract` 對「一個鏡像頁」的定義相同)。原本逐檔取名,
  鏡像目錄被 Finder / 檔案總管開過長出來的 `.DS_Store` 會變成空字串 slug——
  也就是輸出的第一行是空行——而 `Thumbs.db` 會變成一篇叫「Thumbs」的假文章。
  `probe-orphans` 的已知 slug 集共用同一份實作,一併受惠。
- **`verify` 印出孤島警告後會多講一行「該做什麼」**:收到這種警告的人多半不是工程師,
  而且要修的地方根本不在 PolyMigrate(在網站選單)。同時把 `--allow-unlinked` 這個
  逃生門講出來——否則使用者得去翻 CHANGELOG 才知道有它。
- **`verify` 現在會印出 warning 明細**,不再只給一個計數。warning 會讓 exit code 變 1,
  卻在畫面上找不到原因、只能自己去翻 `verify_report.csv`,是很差的第一印象。
- **各指令的例外處理收斂為同一組**,exit code 的對應只在一處定義。原本五個指令各抄一份且
  **抄得不一致**:`verify` 只接 IO 錯,`extract`/`thumbs` 漏接取消 —— 所以這三個指令被 Ctrl-C
  中斷時會直接崩潰,而非乾淨回傳 130。
- exit code(0/1/2/130)補上驗收測試。在此之前這份契約只寫在註解裡,沒有任何測試會因為
  有人改動它而變紅 —— 而它是最多人依賴的那一份契約(CI 與 shell 腳本直接吃它)。

## 2.1.0

**發佈管線的修補。** CLI 介面、Phase 輸出契約、frontmatter 欄位、golden 全部不變。

- **npm 補上 arm64 平台包**(`win32-arm64`、`linux-arm64`),從四個平台變六個,與 Parity 對齊。
  原本 arm64 機器上 `npx cornhsu-polymigrate` 會因為找不到可用的 `optionalDependency` 而直接失敗。
  註:npm 的 OIDC 信任發布無法建立**全新**套件,這兩包首發需要 `NPM_TOKEN`;release.yml 既有的
  「有 token 就用 token、沒有就走 OIDC」自動切換已能處理,首發後把 secret 刪掉即可回到零長效金鑰。
- **符號套件(snupkg)現在真的有產出。** `Directory.Build.props` 設了 `SymbolPackageFormat`
  卻沒設 `IncludeSymbols`,所以 snupkg 從來沒被建出來過 —— 「可以 step-in 進原始碼」
  一直只是設定檔上的宣告。同時補上 SourceLink、`EmbedUntrackedSources`、`Deterministic`
  與 `ContinuousIntegrationBuild`(其餘三個姊妹套件早就有,只有這裡漏了)。
- **移除寫死的 `<Version>1.0.0-preview.1</Version>`。** 實際版本早已是 2.0.x,而 release
  一律以 tag 版號 `-p:Version=` 注入 —— 寫死的預設值只會過期,讓本機建置報出一個
  早就不存在的版本號。
- **npm 平台包的 description 改英文**(主套件的 description 本來就是英文,只有平台包是中文)。
- 新增 `.github/dependabot.yml`(nuget + github-actions);release 的 `setup-node` 由 v5 對齊到 v7。

## 2.0.1

**修正 2.0.0 後全新視角覆審抓到的缺陷。** 對外契約不變。

- **Ctrl-C 對同步指令不再被吞掉**:2.0.0 的 handler 無條件攔截 Ctrl-C,但 `extract`/`thumbs`/`verify`
  是同步、不看 token → 按 Ctrl-C 既不取消也不終止(2.0.0 引入的回歸)。改為第一次合作式取消、
  第二次放行讓 runtime 終止。
- **不安全路徑被跳過的頁不再產生死 301**:被 `path_issues` 記為 error 而跳過(未寫檔)的頁,
  原本仍會被聚合進 `redirect_map` / `redirects.nginx.conf` / `_redirects`,導致 301 打到磁碟上
  不存在的頁(→404),也污染 inventory / media_manifest。改為跳過的頁只出現在 `path_issues.csv`。
- **硬化**:CLI 對 `FormatException`(如病態 URL)乾淨退出 2 而非崩潰;`verify` 對手改壞的
  非字串 `images[].local` 略過而非 `InvalidCastException`;孤兒抓取的原子寫入在取消時清掉 `.tmp`。

## 2.0.0

**工程版:收束函式庫 API、清掉死 config、內部重構。** 這一版**不改任何對外行為契約**——
CLI 指令與參數、Phase 輸出檔案格式(`content_inventory` / `media_manifest` / `redirect_map` /
`path_issues` 等)、frontmatter 欄位、golden 全部逐位元組不變。破壞性僅在「.NET 函式庫的公開型別」
與「YAML config 的死欄位」兩處,故升 major。

### ⚠️ 破壞性變更(升級須看)

- **函式庫公開面大幅收束**:`Cornhsu.PolyMigrate.Core` 原本把抽取/配對/路徑/媒體的內部型別
  (`PageExtractor`、`ExtractedPage`、`RawPage`、`PathSafety`、`MediaPaths`、`LinkRewriter`、
  `PairingSuggester`、`SlugDates`、`FrontmatterSerializer`、`TextEncodings` 等 ~28 個)意外公開;
  現全改 `internal`。**遷移**:改用新的 `PolyMigrator` facade(`FromConfigFile` / `Extract` /
  `GenerateThumbnails` / `Verify`)——這是唯一有文件的進入點。CLI 使用者不受影響。
- **`ExtractionReport.PathIssues`** 由 `List<(string,string,string)>` 改為 `List<PathIssue>`;
  **`VerifyIssue.Severity`** 由 `string` 改為 `Severity` enum(`Warning` / `Error`)。裸 tuple 與魔術字串
  是 semver 陷阱與易錯點。
- **移除四個從未生效的 config 欄位**:`site.render`、`pairing.strategy`、`media.download`、
  `polite.concurrency`。它們宣告了卻從不被讀取;因 loader 對未知欄位報錯,**config 若設了這些欄位
  需刪除該行**,否則載入失敗。功能無任何改變(抽取一律讀 raw 鏡像、配對一律對稱路徑、probe/fetch
  一律循序)。

### 新增

- **`PolyMigrator` facade**:在自己的 .NET 程式裡三行驅動搬遷(README「當函式庫用」段)。
- **函式庫終於帶 XML 文件**:`GenerateDocumentationFile` 打開,`///` 註解進 nupkg,消費者有 IntelliSense。
- **CLI 合作式中斷**:接 Ctrl-C → `CancellationToken` 穿進 probe/fetch,數小時的探測可乾淨中止(exit 130);
  原本 token 從 CLI 端是擺設。

### 內部重構(無行為變更,golden 全綠)

- `ExtractionPipeline.Run` 的聚合邏輯抽成純記憶體、可單獨測的 `InventoryAggregator`。
- redirect 輸出抽成 `IRedirectExporter`(新增格式不必動 pipeline)。
- 各 helper 只收用到的 config section,不再整包 `SiteConfig`。
- 啟發式合法值改為 `PairingSuggester.KnownHeuristics` 單一事實來源。

### 測試與工具

- 新增 `PolyMigrate.Cli.Tests`:CLI 參數解析與 exit code 契約首次有覆蓋(含 1.1.2「選項不吞旗標」的回歸)。
- 新增 `.editorconfig` + CI `dotnet format --verify-no-changes` 關卡。
- 文件校正:`contracts.md` 狀態改「自 1.0 起穩定」並修掉不存在的 `date` frontmatter 欄位;
  `RELEASING.md` 補 npm 通路;規劃書移進 `docs/`;新增 `CONTRIBUTING.md`(含 golden 更新流程)。

## 1.1.2

**穩健性與安全性修正。** CLI 指令、輸出契約、config 欄位皆未變更;fixture 的 golden 輸出
逐檔不變(既有站的搬遷結果不受影響),下列修正只在「惡意/邊界輸入」與「錯誤處理」路徑生效。

安全:
- **媒體路徑穿越防護**:`..%2f..%2f` 之類「編碼後穿越」會逃過 `Uri.AbsolutePath` 的正規化、
  解碼後還原成 `../` 逃出 media 根目錄。解碼後逐段檢查,含穿越分段一律拒絕(當缺圖記錄,不寫根外)。
- **CSV 公式注入防護**:供 Excel 覆核的清單中,以 `= + - @` 起頭的欄位(可能來自爬到的 alt/URL)
  前置單引號中和;本工具自己讀回的內部快取不加料(逐位元組還原)。

穩健性:
- **HTTP 逾時不再掀掉整批**:`HttpClient.Timeout` 到期丟的是 `TaskCanceledException`(非
  `HttpRequestException`),原本會讓 probe/fetch 在跑到一半時整批中止;現視為暫時性失敗,記錄該項、其餘照跑。
- **原子寫入**:縮圖與孤兒資產改「先寫暫存再改名」,中途中斷(Ctrl-C、磁碟滿)不再於最終路徑
  留半截檔——否則重跑會因「檔案已存在」把壞檔當成已完成。
- **重複輸出路徑偵測**:兩個不同來源檔收斂成同一輸出(如 `a.php.html` 與 `a.asp.html` 都成 `a.md`)
  現會記入 `path_issues.csv` 並拒寫,不再靜默覆蓋。
- **CLI**:選項值不再吞掉後面的旗標(`extract site.yaml --root --dry-run` 現報「--root 需要值」,
  而非把 `--dry-run` 當成 root 路徑);一般 IO 錯誤(含 `verify`)乾淨退出 code 2 而非噴堆疊。
- **config 驗證補齊**:`polite.concurrency`、`polite.delay_ms`、`thumbnails.max_width/quality`、
  `text_in_image_max_length`、`lang_map` 空 locale 值,越界即報錯,不再默默下傳給編碼器/排程器。

i18n / 決定性:
- **slug 日期解析**釘死 invariant culture 與 ASCII 數字(`[0-9]` 而非 `\d`):泰/波斯曆機器不再
  誤判年份,全形/阿拉伯數字不再讓 `int.Parse` 崩潰。
- **標題清理**的大小寫比對加 `CultureInvariant`,避開 tr-TR 的 Turkish-I;空字串雜訊不再讓剝除迴圈卡死。
- **frontmatter 引號**補上 YAML 1.1 會誤判為非字串的形態(ISO 日期、六十進位、十六/八/二進位、
  `.inf`/`.nan`、前導小數點),下游 PyYAML/js-yaml/Hugo 讀回仍是字串。
- **內文連結改寫**保留 `?query` 與 `#fragment`(`news.php?id=5` 與 `#team` 不再被丟)。
- **verify** 認得單引號與大寫的 HTML `href/src`,媒體引用先去 `?query`/`#fragment` 再對磁碟找檔。

## 1.1.1

**無功能變更。** 用於驗證 npm 的 OIDC 信任發布路徑 —— 1.1.0 是以長效 token 發布的
(npm 的信任發布必須先有套件才設定得了),設定完成後需要一次實際發布確認該路徑可用,
否則問題會留到下次真正要發版時才浮現。

- 移除臨時的診斷 workflow(用於查出 1.1.0 發布失敗的根因:token 已被撤銷,`npm whoami` 回 E401)

## 1.1.0

**新增 npm 發布通路。** 兩條通路功能完全相同,選手邊有的執行環境即可:

```
npx cornhsu-polymigrate extract site.yaml   # 有 Node,不需要 .NET
dotnet tool install -g Cornhsu.PolyMigrate  # 有 .NET
```

- 使用者是「要把舊網站搬成靜態站的人」,多半在 Hugo / Eleventy / Astro / Next.js
  的生態裡,手邊有 Node、不一定有 .NET SDK。而搬站是一次性任務 —— 為了跑一次而裝
  整套 SDK,摩擦成本高到多數人會直接放棄。
- 採 esbuild 模式:主套件只含啟動腳本,四個平台包(win32-x64 / linux-x64 /
  darwin-x64 / darwin-arm64)掛 `optionalDependencies`,npm 依 `os`/`cpu` 只下載
  當前平台那一份(壓縮後約 45 MB)。不使用 postinstall 下載腳本 —— 那會被
  `npm ci --ignore-scripts` 擋掉。
- **只有 CLI 上 npm**;`Cornhsu.PolyMigrate.Core` 是給 .NET 開發者用的函式庫,
  受眾本來就在 NuGet。
- CLI 行為、輸出契約、config 欄位皆未變更。

## 1.0.0

介面定案。功能與 `1.0.0-preview.1` 相同——preview 期間未回報任何問題,
119 個單元/整合測試在三平台 CI 全數通過,故直接定版。

自此 **CLI 指令與參數、Phase 之間的輸出契約(`content_inventory` / `media_manifest` /
`redirect_map` 等檔案格式)、YAML config 的欄位**視為穩定介面:新增功能升 minor,
修正升 patch,破壞性變更才升 major。

文件更新:補上 NuGet / CI / 授權徽章與作品集連結;修正安裝指令(不再需要
`--prerelease`);case study 補記重跑快取(4.6GB 實站 30.1s → 4.6s)與
redirect 匯出(nginx conf、Netlify `_redirects`)。

## 1.0.0-preview.1

首個公開版本。把一次真實完成的整站搬遷(中英雙語佛寺網站,495+ 頁、4.6GB 媒體)
產品化為 config 驅動、可離線重跑的工具;抽取結果以原 Python 管線輸出為 golden 基準逐頁驗證。

- **`extract`(Phase 2 結構化抽取)**:鏡像 HTML → 帶 frontmatter 的 Markdown +
  `content_inventory` / `media_manifest` / `redirect_map` / 壞圖與待補媒體清單;`--dry-run` 只報告不寫檔。
  內建實戰坑:YAML 數字 slug 強制引號、冒號標題跳脫、`%20` 單次編碼、影片/iframe/PDF
  佔位符保留原位、內文標題優先於髒 `<title>`、`.php` 相對連結改寫新路由、原站壞圖記錄不阻斷。
- **多語言為核心**:`lang_map` 宣告任意數量語言(URL 前綴 → BCP-47),inventory 欄位、
  frontmatter、配對全部隨語言數展開;單語站 = 一組對映的特例。
- **雙語配對**:對稱路徑自動配對(translation_key);配不起來的依 config 順序用
  共用相簿 / slug 日期正規化(YMD/MDY/DMY 都認)/ 標題相似度給啟發式建議
  (`pair_status` / `suggested_pair` / `pair_evidence`),無證據誠實標 missing、絕不硬配。
- **`verify`(全站巡檢)**:frontmatter 必填欄位、內部連結對路由集、媒體引用對磁碟
  (已記錄的原站壞圖降 warning);exit code 0/1/2 可直接進 CI。實測 516 頁 0 錯誤。
- **`thumbs`**:EXIF 自動轉正(手機直拍坑)後 Lanczos 縮圖,增量可重跑;
  與 Pillow 原型逐張比對一致。影像庫用 Magick.NET(ImageSharp 4.x 建置即索取授權金鑰,棄用)。
- **`probe-orphans` / `fetch-orphans`**:找回「索引移除但頁面還在」的孤兒文章——
  逐日雙日期格式候選 + A–D 後綴鏈、409 bot 防護退避、config 宣告 cookie 繞法、禮貌間隔。
- **redirect 一鍵可部署**:`redirect_map` 的 `new_path` 以內文連結改寫的同一套路由規則自動填,
  另出 `redirects.nginx.conf` 與 Netlify `_redirects`(old==new 略過防迴圈)。
- **重跑快取**:媒體 sha1 以 (大小, mtime) 快取,4.6GB 實站重跑 30s → 5s;快取是內部檔、刪掉不影響輸出。
- **跨平台路徑防護**:Windows 保留裝置名/非法字元/尾點空白 → 任何平台一致拒寫並記入
  `path_issues.csv`(兩平台產出必須相同);大小寫碰撞偵測;超長路徑 warning。
- **工程底盤**:一站一份 YAML config(未知欄位報錯)、Phase 輸出契約文件、
  離線 fixture 雙語站 + golden-file 測試、119 個單元/整合測試、三平台 CI。

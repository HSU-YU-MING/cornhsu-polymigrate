# PolyMigrate 開發指南

把老舊動態站搬成靜態站 Markdown 的 CLI 與函式庫（.NET 10）。工具能做什麼、怎麼用、
config 長什麼樣，見 [README.md](README.md) / [README.zh-Hant.md](README.zh-Hant.md)——那是使用者
文件，這份是開發慣例。輸出格式契約在 [docs/contracts.md](docs/contracts.md)，貢獻流程與
golden 更新在 [CONTRIBUTING.md](CONTRIBUTING.md)。

**發版流程的共通規則寫在全域 skill `nuget-packages`**（tag 是版本真相源、Trusted Publishing、
0.x 凍結規則、發完要同步作品集網站）。這份只寫 PolyMigrate 特有的部分——主要是它比另外三個
姊妹套件多出來的 **npm 發佈線**。

## 指令

```sh
dotnet build                                        # 或 dotnet build PolyMigrate.slnx
dotnet test
dotnet format PolyMigrate.slnx --verify-no-changes  # CI 的 format job 會擋，先在本機看一眼
dotnet run --project src/PolyMigrate.Cli -- --help
```

CI（`ci.yml`）三個 job：`format`、三平台 `test`（ubuntu / windows / macos）、`pack`。
`permissions: contents: read` 是**明文寫死的**，不吃 repo 預設值——預設值哪天被改寬，
workflow 會跟著變寬而且沒有人會發現。

golden 基準更新（改動**刻意**改變管線輸出時才做）：

```sh
POLYMIGRATE_UPDATE_GOLDEN=1 dotnet test -c Release --filter FixtureSite_MatchesGolden
git diff tests/fixtures/golden    # 逐檔看過再 commit，diff 本身就是 review
```

本機乾跑封裝（不發佈，只確認裝得起來、跑得動）的指令在 [RELEASING.md](RELEASING.md)；
tool 套件約 **100 MB**（內含 Magick.NET 全平台原生檔），這個大小是預期的，不是打包壞掉。

## 版本欄位

`Directory.Build.props` 的 `<Version>` 固定 `0.0.0-dev`，**發版不要動它**（理由與其他三個套件
共通，見 skill）。本 repo 多一格：`npm/cornhsu-polymigrate/package.json` 的
`"version": "0.0.0-placeholder"` 與其中六個 `optionalDependencies` 的版本，同樣由
`npm/prepare.mjs` 在 CI 覆寫，**也不要手動改**。

## npm 發佈線（本 repo 獨有，坑集中在這裡）

一個 tag 同時出七個 npm 套件：主套件 `cornhsu-polymigrate`（只含 `bin/polymigrate.js` 啟動腳本）
+ 六個平台包 `@cornhsu/polymigrate-{win32,linux,darwin}-{x64,arm64}`（各自 self-contained 執行檔，
掛 `optionalDependencies`，npm 依 `os`/`cpu` 只下載當前平台那一份）。組裝在 `npm/prepare.mjs`，
發佈在 `release.yml` 的 `npm` job（`needs: release`，NuGet 成功才跑）。

**只有 CLI 上 npm。** `Cornhsu.PolyMigrate.Core` 是給 .NET 開發者的函式庫，受眾本來就在 NuGet。

### 兩種認證需求互卡（2.1.0 實際踩到，加新平台包時會再遇到）

npm 的 **OIDC 信任發布無法建立全新套件**——套件要先存在，才設定得了信任發布。所以每次新增一個
平台包，那一包的首發只能用 `NPM_TOKEN`。但**既有且已設信任發布的套件，用 token 發會撞 provenance
簽章 403**（`CA_CREATE_SIGNING_CERTIFICATE_ERROR`）：workflow 有 `id-token: write`，npm 就會自動
嘗試簽 provenance，而認證走的是 token → 憑證申請被拒。

`release.yml` 已經內建對策，不要拆掉：

- 有 `NPM_TOKEN` → 寫 `.npmrc` 用 token，並 `export NPM_CONFIG_PROVENANCE=false`；沒有 → 走 OIDC
  （OIDC 路徑不設這個變數，信任發布仍會自動簽 provenance）。
- 迴圈**刻意不用 `set -e`**：單一套件失敗只記進 `failed` 陣列並繼續，全跑完再統一非零退出。
  否則一次失敗就要重跑很多趟才推得完。
- 已存在的版本 `npm view` 得到就跳過而非報錯，重跑冪等。

流程是：加平台包 → 暫時放 `NPM_TOKEN` secret 發一次 → 到 npmjs 幫新套件設信任發布 → **把 secret
刪掉**，之後自動回到 OIDC。

### 不要再造 token 通道

一次性的 `npm-backfill.yml`（2.1.0 補發用）已於 **2026-08-25** 刪除——七個套件版本已核對在線，
任務完成。`gh secret list` 與 `gh variable list` 目前皆為空（2026-08-26 實查），
workflow 只剩 `ci.yml` 與 `release.yml`。**主線走 OIDC 就好**，不要為了方便再開一條長效金鑰的路。

### 其他已釘死的細節，動了會安靜壞掉

- **`setup-node` 刻意不設 `registry-url`**：它會在 `.npmrc` 寫
  `//registry.npmjs.org/:_authToken=${NODE_AUTH_TOKEN}`，OIDC 情境下該變數不存在 → npm 拿「空 token」
  去驗證而不改走 OIDC，直接失敗（actions/setup-node#1551）。預設 registry 本來就是 npmjs.org。
  同源理由：token 路徑用的是自訂的 `NPM_TOKEN`，**不是** `NODE_AUTH_TOKEN`。
- **npm CLI 要 >= 11.5.1** 才支援信任發布，Node 22 內建 10.x，所以 job 裡有一步 `npm install -g npm@latest`。
- **平台包必須先發，主套件最後發**，否則主套件的 `optionalDependencies` 解析不到。
- **授權通知是硬性中止條件**：平台包夾了 `Magick.Native-Q8-*`，那是實質再散布，Apache-2.0 §4(d)
  要求隨附 Magick.NET 的 `Notice.txt`。`prepare.mjs` 從 `project.assets.json` 讀出**實際還原到的版本**
  去 NuGet 快取拿那份通知（不寫死版本路徑——寫死的話相依一升版就會安靜失效），找不到就 throw；
  `release.yml` 另有一步驗成品確實帶到。**不要把它降級成印個警告**——這條原本是 RELEASING.md 上的
  人工檢查項，自動化的理由就是人工項遲早會被忘記。
- **煙霧測試只跑 linux-x64**（runner 上唯一能直接執行的），至少守住「self-contained 產物開得起來」。

### 新增或移除平台時，要一起改的地方

1. `release.yml` 的 `Publish binaries (6 RIDs)` 迴圈
2. `npm/prepare.mjs` 的 `TARGETS`
3. `npm/cornhsu-polymigrate/package.json` 的 `optionalDependencies`
4. **`npm/cornhsu-polymigrate/bin/polymigrate.js` 的 `TARGETS`**（← 2.1.0 就是漏了這處，見下面技術債）
5. README 的平台清單

## 地雷

### 對外契約自 1.0 起凍結，範圍比想像的大

CLI 指令與參數、**exit code**、Phase 輸出檔案格式（CSV 欄位、frontmatter 欄位）、YAML config 欄位，
四面都是契約。破壞任一面 = major。幾個容易誤判的角落：

- **exit code 是最多人依賴的那份契約**（CI 與 shell 腳本直接吃）：0 乾淨 / 1 warning / 2 error /
  130 中斷。對應只在 `Cli.Report()` 定義一次，`CliTests` 末段有驗收測試釘著。**新增指令時
  catch 用共用的 `IsHandled()`，不要各自挑幾種例外**——2.2.0 之前五個指令各抄一份且抄得不一致，
  `extract`/`thumbs` 漏接取消，Ctrl-C 時直接崩潰而非乾淨回 130。
- **「新增一種 warning」是破壞性的**，即使檔案格式沒變。2.2.0 加了 `unlinked_page` warning，
  於是既有使用者什麼都沒改就從 exit 0 變 1。同理 2.2.1 的殘檔回報**刻意不計入 warning**——
  那會在 patch 版動到 exit code。
- **`SiteConfigLoader` 對未知欄位報錯**，所以「移除一個 config 欄位」會讓設過它的 config 載入失敗
  （2.0.0 移掉四個死欄位就是這樣升的 major）。
- `reject_reason` 明定為**開放集合**：未來新增門檻會新增值，消費端須容忍沒見過的值——先講明，
  以後新增門檻才不必動 `config_version`。
- CSV 一律 **UTF-8 含 BOM + CRLF**（人工覆核走 Excel，無 BOM 中文會亂碼）。這是契約，不是筆誤。

### golden 與行尾

`tests/fixtures/**` 在 `.gitattributes` 釘 `-text`（禁止任何行尾轉換），其餘全 repo 釘 `eol=lf`。
CI 的 Windows runner 預設 `autocrlf=true`，CRLF checkout 會改變 raw string literal 的內容，
**測試會在 CI 上與本機行為不同**（踩過）。不要讓編輯器重排 fixture。

### `extract` 只寫不刪，`raw/` 與 `media/` 不可再生

「只寫不刪」正是那兩份不可再生資料安全的原因——沒有任何 PolyMigrate 指令會刪或覆寫它們。
代價是舊站下架文章後重跑，`content/` 會留舊 `.md`，所以摘要有 `stale in content` 那一行。
**不要為了「乾淨」改成會刪檔**。

### hreflang 只建議、不合併，而且預設不啟用

`pairing.fallback` 預設是**空 list**（`SiteConfig`），`hreflang` 要 config 明寫才會作用——
`examples/ibps-austin.yaml` 有放，README 範例也有放，但那是範例不是預設。摘要那行措辭
刻意是「passed validation」而不是「usable/used」：通過查證 ≠ 配到對象 ≠ 有被用。
五道門與「為什麼不直接改寫 `translation_key`」的量測記在
[docs/hreflang_量測與決策.md](docs/hreflang_量測與決策.md)，要改這個決策前先讀它。

配對是**兩趟**：`hreflang` 先配完，啟發式才上場。合成一趟的話，宣告的關係會被字典序剛好排在
前面的共用相簿搶走對象，等於讓推測贏過宣告。

### `NuGet/login` 已釘 commit SHA

`release.yml` 的 `NuGet/login@8d196754b4036150537f80ac539e15c2f1028841`（= v1.2.0，2026-08-25 安全硬化）。
**不要改回 `@v1`**；上游出新版要更新時，四個姊妹 repo 一起換新 SHA。

## 已知教訓（從 git log 挖的，都真的爆過）

- **設定檔上的宣告不等於有生效**：`SymbolPackageFormat` 設了卻沒設 `IncludeSymbols`，snupkg
  從來沒被建出來過，「可以 step-in 進原始碼」一直只是宣告（2.1.0 修）。
- **從原始碼建置曾自稱 `1.0.0`**：`<Version>` 留空退回 MSBuild 預設，而 1.0.0 是 PolyMigrate
  **真的發行過**的版本——執行檔自稱一個存在、但不是它自己的版本，debug 時會把人帶去錯方向（2.2.1 修）。
- **`Path.Combine` 後段是絕對路徑就丟掉前段**：`slugs --lang C:\somewhere` 會安靜地讀到鏡像目錄
  以外的地方還回報成功。判斷刻意不用 `Path.IsPathRooted`（Linux 上認不出 Windows 形式的路徑）。
- **平台差異只有 CI 抓得到**：`slugs` 的路徑片段檢查一開始是平台相依的，在 Linux/macOS runner 才紅。
  三平台 test matrix 不是裝飾品。
- **一個正確的連結產生兩筆假發現**：中日韓 slug 的百分號編碼 href 對不上解碼後的鏡像檔名，
  `verify` 報 `broken_link`，2.2 之後還連帶誤報 `unlinked_page`。修法是「原樣對不上時才多試一次
  解碼版」，原本對得上的維持逐位元組不變，所以 golden 不動。
- **目錄裡的垃圾檔會變成假資料**：`.DS_Store` 讓 `slugs` 輸出第一行是空行、`Thumbs.db` 變成一篇
  叫「Thumbs」的文章。改成只認 `*.html`（與 `extract` 對「一個鏡像頁」的定義相同）。
- **空結果會被讀成「沒有新文章」**：`slugs --section` 打錯回 **exit 2**，不是空清單。
  同理「去舊站讀列表頁挑 slug」那一半**刻意不做**——它因站而異，而且會安靜地壞掉
  （regex 抓不到東西時，排程只會每天回報「沒有新文章」，你以為在追平其實早就斷了）。
- **被跳過的頁不該進 redirect**：`path_issues` 記為 error 而未寫檔的頁，原本仍被聚合進
  `redirect_map`，導致 301 打到不存在的頁（→404）。
- **原子寫入**：縮圖與孤兒資產先寫 `.tmp` 再改名，否則中斷留下的半截檔會在重跑時被當成已完成。
- **HTTP 逾時丟的是 `TaskCanceledException` 不是 `HttpRequestException`**，原本會讓 probe/fetch
  跑到一半整批中止。
- **文件與程式各自演化一定會走鐘**：2.2.0 開發期 README 寫 `slugs out/` 而 `--help` 寫 `<root>`，
  撐過一整輪人工審視；同一次改動英文 README 加了 `slugs`、中文 README 漏掉，也沒人當場看出來。
  對策是 `ReadmeDriftTests`（見下）。

## 技術債與判讀留帳

- **⚠ 尚未出貨的修補：arm64 啟動腳本與 README 徽章（2026-08-26 修好，等下次發版）**
  兩個都已修進 `main`，但**使用者手上的 2.1.0～2.3.0 仍是壞的**，要推下一個 tag 才會到他們手上：
  - `bin/polymigrate.js` 的 `TARGETS` 原本只有四個平台，缺 `win32 arm64` / `linux arm64`。
    2.1.0 在 `release.yml`、`prepare.mjs`、`optionalDependencies` 三處都加了 arm64，**唯獨啟動
    腳本沒改**（該檔自 1.1.0 的 `1c8bf09` 起未曾變動）。症狀極隱蔽：平台包被 npm 正確裝好，
    啟動腳本卻回「沒有預先建置版本」並 exit 1。**這就是上面「五個地方要一起改」那張清單的由來。**
  - `README.md` 的 License 徽章被 `8244632` 把整段「See also」貼進 alt text 裡，徽章不呈現、
    且底部缺 See also 一節。這份 README 隨 NuGet（`PackageReadmeFile`）與 npm 主套件出貨，
    所以壞掉的徽章會出現在套件頁上。已還原徽章並把該節補到 License 段之前（比照中文版位置）。
- **四→六的平台數描述漂移**（arm64 是 2.1.0 加的，這幾處還停在四個；README 已於 2026-08-26 修）：
  - `RELEASING.md`「npm 通路」段寫「四個 RID … 四個平台包」
  - `npm/prepare.mjs` 檔頭註解寫「五個可直接 npm publish 的資料夾」（實為 6 平台包 + 1 主套件 = 7）
  - `release.yml` 註解說 arm64「是 **2.0.2** 補上的」——沒有 v2.0.2 這個 tag，CHANGELOG 記的是 2.1.0
- **已刪的 `npm-backfill.yml` 仍被文件提到**：`Directory.Build.props` 的 `<Version>` 註解與
  CHANGELOG 2.2.1「內部」段都還寫著它。CHANGELOG 是歷史紀錄、留著合理；`Directory.Build.props`
  那句可以順手改掉。
- **沒有 README 示範輸出的自動比對**。`ReadmeDriftTests` 只擋「README 列的指令 = `--help` 列的指令」
  （中英文各一），**不比對說明文字，也不比對貼在 README 裡的示範輸出**（刻意如此：逐字比對只會
  製造假警報，反而讓人把測試關掉）。姊妹 repo 有 `verify-readme-sample.ps1`（XamlContrast）與
  `verify-readme-facts.ps1`（Parity），**這個 repo 兩者都沒有**。所以：
  **動過 console 輸出、case study 數字或版本敘述，就要人工比對 README 的兩份語言版本。**
  要補這類腳本的話照姊妹 repo 的形狀做（腳本放 `scripts/`、CI 呼叫、附 `-Update` 一鍵重貼）。
- **tool 套件約 100 MB**（Magick.NET 全平台原生檔）。backlog 有「把 `thumbs` 拆成選配套件」一項，
  尚未做。
- **相依掃描 2026-08-23 實掃：0 漏洞。** NuGet audit 警告（NU1901–NU1904）在
  `Directory.Build.props` 刻意不升級為錯誤，所以掃描結果要自己看，不會靠建置失敗提醒你。
- 本機還留著已合併的 `feat/hreflang-pairing` 分支，無害。

## 開工慣例

- **註解與設計說明用繁體中文；公開 API 名稱、README、CLI `--help` 用英文。** 原始碼裡的
  `§X.Y` 指向 [docs/搬遷工具_評估與規劃書.md](docs/搬遷工具_評估與規劃書.md)。
- 函式庫公開面是 semver 契約：新 helper 一律 `internal`，`PolyMigrator` facade 是唯一有文件的
  進入點。測試靠 `InternalsVisibleTo`（`PolyMigrate.Core.Tests`）看得到內部型別。
- 站別知識放 YAML config，不要進程式碼。
- 收尾：CHANGELOG 補上本版段落（**tag 推出去就發佈了，沒有反悔的機會**），
  改過輸出格式就更新 `docs/contracts.md`，改過相依就更新 `THIRD-PARTY-NOTICES.md`，
  改過 console 輸出就人工比對兩份 README。

# Changelog

版本規則:preview 期間破壞性修改不另行公告;1.0 起新功能升 minor,修正升 patch。

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

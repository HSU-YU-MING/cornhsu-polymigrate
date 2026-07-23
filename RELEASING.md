# 發布 Cornhsu.PolyMigrate 到 NuGet

用 **Trusted Publishing(OIDC)**——不需要長效 API key、不需要 repo secret。推一個 `v*` tag 就發佈。
(與 cornhsu-parity 同一套流程。)

## 一次性設定

1. **把 repo 推上 GitHub**:`HSU-YU-MING/cornhsu-polymigrate`
   (`Directory.Build.props` 的 `RepositoryUrl` 與 `release.yml` 都已指向這個路徑。)

2. **在 nuget.org 設定 Trusted Publisher**(帳號 → Trusted Publishing),**一條政策即可**
   (政策綁的是 repo+workflow;`Cornhsu.PolyMigrate` 與 `Cornhsu.PolyMigrate.Core` 兩個新 ID
   都會在首次發佈時建立在這條政策下):
   - Policy Name:`Cornhsu.PolyMigrate`(自由文字;沿用「以主套件 ID 命名」的既有慣例,同 Cornhsu.Parity)
   - Package Owner:`Cornhsu`
   - Repository Owner:`HSU-YU-MING`
   - Repository:`cornhsu-polymigrate`
   - Workflow File:`release.yml`
   - Environment:留空(workflow 未使用 GitHub environment)

3. 確認 `release.yml` 裡 `NuGet/login` 的 `user:` = nuget.org 使用者名稱(目前 `Cornhsu`)。

## 每次發布

```sh
# 版號由 tag 推導(release.yml 用 -p:Version 覆蓋 props 的預設值)
git tag v1.0.0-preview.1
git push origin v1.0.0-preview.1
```

`release.yml` 會自動:build → test → pack → OIDC 換臨時金鑰 → `dotnet nuget push`(tool + lib 兩包)。

## 本機乾跑(不發佈,驗證封裝可裝可跑)

```sh
dotnet pack -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts Cornhsu.PolyMigrate
polymigrate --version && polymigrate --help
dotnet tool uninstall --global Cornhsu.PolyMigrate
```

> tool 套件約 100 MB——內含 Magick.NET 全平台原生二進位(EXIF 轉正縮圖用)。
> 若要瘦身,backlog 有「把 thumbs 拆成選配套件」一項。

## npm 通路(同一個 tag 一起發)

同一條 `release.yml` 在 NuGet job 成功後跑 `npm` job(見該檔),不需另外操作。機制:

- 對四個 RID(win-x64 / linux-x64 / osx-x64 / osx-arm64)`dotnet publish --self-contained`,
  由 `npm/prepare.mjs` 組成主套件 `cornhsu-polymigrate`(只含啟動腳本)+ 四個平台包
  (`@cornhsu/polymigrate-*`,掛 `optionalDependencies`,npm 依 os/cpu 只下載當前平台那份)。
- **只有 CLI 上 npm**;`Cornhsu.PolyMigrate.Core` 是給 .NET 開發者的函式庫,受眾在 NuGet。
- 認證自動切換:有 `NPM_TOKEN` secret → token(首次發布只能這樣,信任發布需先有套件才設定得了);
  沒有 → OIDC 信任發布(零長效金鑰)。設定好信任發布後把 secret 刪掉即自動改走 OIDC。
- 發布冪等:已存在的版本跳過而非報錯,某包失敗重跑不會被前面已成功的擋住。

一次性設定:在 npmjs.org 為 `cornhsu-polymigrate` 與 `@cornhsu/polymigrate-*` 設 trusted publisher
(repo `HSU-YU-MING/cornhsu-polymigrate`、workflow `release.yml`)。

## 發布前檢查清單

- [ ] `CHANGELOG.md` 補上本版段落
- [ ] `dotnet test` 全綠
- [ ] `dotnet format --verify-no-changes` 無變更(CI 也會擋)
- [ ] 破壞性變更(major)在 CHANGELOG 寫明遷移方式

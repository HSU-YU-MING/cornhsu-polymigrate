# 發布 Cornhsu.PolyMigrate 到 NuGet

用 **Trusted Publishing(OIDC)**——不需要長效 API key、不需要 repo secret。推一個 `v*` tag 就發佈。
(與 cornhsu-parity 同一套流程。)

## 一次性設定

1. **把 repo 推上 GitHub**:`HSU-YU-MING/cornhsu-polymigrate`
   (`Directory.Build.props` 的 `RepositoryUrl` 與 `release.yml` 都已指向這個路徑。)

2. **在 nuget.org 設定 Trusted Publisher**(帳號 → Trusted Publishing),兩個套件各一條:
   - Package:`Cornhsu.PolyMigrate` 與 `Cornhsu.PolyMigrate.Core`(新 ID 可由首次發佈建立)
   - Repository owner:`HSU-YU-MING`
   - Repository:`cornhsu-polymigrate`
   - Workflow:`release.yml`

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
dotnet tool install --global --add-source ./artifacts Cornhsu.PolyMigrate --prerelease
polymigrate --version && polymigrate --help
dotnet tool uninstall --global Cornhsu.PolyMigrate
```

> tool 套件約 100 MB——內含 Magick.NET 全平台原生二進位(EXIF 轉正縮圖用)。
> 若要瘦身,backlog 有「把 thumbs 拆成選配套件」一項。

## 發布前檢查清單

- [ ] `CHANGELOG.md` 補上本版段落
- [ ] `dotnet test` 全綠(100+)
- [ ] icon.png(首次發布前補,與其他 Cornhsu 套件一致)

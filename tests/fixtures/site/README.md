# Fixture 測試站(規劃書 §3.5)

合成的離線雙語小站,golden-file 測試(`GoldenTests`)的輸入。**每修一個坑(§2.6),就在這裡加一頁重現它** —— 本站即功能的活文件。

## 規則

- 全站離線可跑,禁止外部網路依賴;CI 直接使用。
- 每頁在檔頭 HTML 註解標明「重現哪個坑」。
- 基準輸出在 `tests/fixtures/golden/`;更新方式:`POLYMIGRATE_UPDATE_GOLDEN=1` 跑一次測試後人工 review diff。
- `.gitattributes` 已對 `tests/fixtures/**` 關閉行尾轉換,勿改。

## 覆蓋的坑

- [x] 對稱雙語頁(`news/20260101` 中英同名)→ 自動配對 happy path
- [x] 不對稱檔名(`events/2026_chanxiu` vs `events/enRetreat`)→ shared_media 啟發式建議
- [x] 日期格式混用(`20260214` vs `02142026`)→ date 啟發式建議(§2.6)
- [x] 無語言前綴的站級頁(語言選擇頁)→ `/index`,不參與配對
- [x] 標題含冒號、數字 slug 前導 0(YAML 跳脫)
- [x] `<title>` 與內文標題不符、帶日期前綴、站名雜訊
- [x] YouTube iframe / PDF iframe 佔位保留(zh/en 標籤)
- [x] 圖片路徑 `%20` 編碼(磁碟解碼名、URL 單次編碼)
- [x] 壞圖(引用但 404)→ missing_images 記錄;verify 降為已知 warning
- [x] 內文殘留 `.php` 相對連結(`../`)→ 新路由
- [x] 相簿頁型:圖入 frontmatter、內文移除(空 `<li>` 不殘留)
- [x] section flag(support → needs_rebuild)、text_in_image 偵測
- [ ] Big5 編碼頁 + header/meta 宣告不一致(§3.1,待 Phase 0 編碼偵測)
- [ ] EXIF 方向顛倒的圖片(待縮圖功能)
- [ ] CSS `url()` 背景圖(待 Phase 1)
- [ ] Windows 危險檔名(保留字、超長路徑、大小寫碰撞)(§3.4,待 slug 產生器)

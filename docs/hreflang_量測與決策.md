# hreflang:量到什麼、決定做到哪裡

> 決策紀錄。2026-08-18 定,對應 2.3.0。
> 這份文件回答一個會被反覆問到的問題:**既然 hreflang 是最權威的配對訊號,
> 為什麼 PolyMigrate 只拿它「建議」,不拿它直接配?**

## 一句話

因為在唯一一個可以驗證的真實站上,hreflang **一則都沒有**——
而「直接配」是整條路上錯了最貴的一階,不能用合成資料驗收。

## 量到什麼

對象:`D:/海外實習/香雲寺/網站改版/crawl/raw`——本專案 README 案例研究那個站,
也是 `examples/ibps-austin.yaml` 對應的站。2000 年代的 PHP 機構站,中英雙語,
正是 PolyMigrate 的目標客群。

| 檢查項 | 結果 |
|---|---|
| 鏡像 `.html` 檔數 | 528 |
| 含 `hreflang` 的頁 | **0** |
| 含 `rel="alternate"` 的頁 | **0** |
| 鏡像裡的 sitemap | 無 |
| 原站 `/sitemap.xml`(2026-08-18 實測) | **404** |

怎麼重跑(換掉 `$R` 就能量別的站):

```bash
R=/path/to/crawl/raw
grep -rli "hreflang" "$R" | wc -l
grep -rli "rel=[\"']*alternate" "$R" | wc -l
find "$R" -iname "*sitemap*"
```

有裝 PolyMigrate 的話,更完整的答案在 `extract` 之後的 `hreflang_map.csv`——
它連「有宣告但不能用」的比例與原因(`reject_reason`)都會告訴你,
那才是判斷「這個站的 hreflang 能不能信」該看的東西。

## 據此做了什麼(2.3.0)

- **讀**:每頁 `<head>` 的 `<link rel="alternate" hreflang>` 全記錄成 `hreflang_map.csv`,
  含不可用的宣告與被擋下的原因。沒宣告的站只有表頭——這正是上面那個 0 的來源。
- **建議**:`hreflang` 成為 `pairing.fallback` 的合法值,排在最前面,
  並且是唯一不受「兩端須同 section」限制的線索。
- **不合併**:配對結果一樣只是 `heuristic_suggested`,`translation_key` 不動,
  最終決定權在人。

守門是五道(見 `HreflangIndex`)。其中 `ambiguous_target` 是實跑一個對抗性假站才發現要加的:
少了它,「整站 alternate 都指首頁」的模板複製會產出「英文首頁 = 某篇中文新聞」這種建議,
還掛著 hreflang 這個最權威的證據標籤。**合成資料驗得出守門漏不漏,驗不出真實資料長什麼樣。**

## 沒做什麼、為什麼

### Stage 2:把 hreflang 當權威,直接改寫 `translation_key`

**掛起。** 不是成本問題(估 2–3 天),是**沒有素材可以驗收**。

`translation_key` 會流進 frontmatter、`redirect_map.csv`、以及使用者自己的匯入程式——
改寫錯了不會有任何一個地方報錯,只會安靜地把兩篇無關的文章合併成一組。
而目前唯一能拿來驗收的真實站,輸入是空的。

`n=1`,所以上面那個 0 **不證明**「舊站都沒有 hreflang」。
它證明的是更狹窄、也更要命的一件事:**我們沒有素材可以驗證 Stage 2**。

**重啟條件**:拿到一個真的有 hreflang 宣告的鏡像。屆時先看 `hreflang_map.csv` 的
`reject_reason` 分布——`ambiguous_target` 或 `not_in_mirror` 佔多數的話,
答案還是不要做;`usable` 佔壓倒性多數而且多半 `reciprocal=True` 的話,才值得往下走。

**屆時可以直接沿用的**:`HreflangIndex` 的五道門與 `AlternatesByKey`
(來源 key → 目標 key)已經是 Stage 2 需要的那張圖,差的只是 union-find 與覆寫,
以及一份 `pairing_conflicts.csv` 收容衝突。

### Stage 3:讀 sitemap 的 `xhtml:link`

**拆成兩案,兩案都沒做。**

1. **當配對來源**——成本比原估低(`HreflangIndex` 已在,sitemap 只是另一個產生同樣三元組的
   輸入源,五道門與輸出全部共用,約 3–4 小時),但**價值也更低**:會在 sitemap 出
   `xhtml:link` 的站,幾乎一定也會在 `<head>` 出 `hreflang`——同一套 SEO 工具產的。
   多半是冗餘的第二條路,只在「鏡像抓漏」或「head 被剝掉」時才有用。
2. **當 URL 全集,用來補鏡像**——這條可能才是 sitemap 真正的用處:
   交叉比對「鏡像漏抓了什麼」,比 `probe-orphans` 逐日猜 URL 可靠得多
   (香雲寺那次是靠日期探測找回 13 篇)。但它屬於孤兒頁找回,不屬於配對,
   要做的話該獨立提案,不要掛在 hreflang 這條線底下。

順帶:上面那個站現在連 sitemap 都沒有(404),所以這兩案在同一個站上也一樣驗不了。

## 同一次量測的副產品

跑那 528 頁時另外看到兩件跟 hreflang 無關、但更該先處理的事:

- **50 個單語 key,三個既有啟發式合計建議 0 筆。** 其中「光明燈法會」中英兩頁
  只差一個分隔符(`2025-light-offering` / `2025_light_offering`),卻三個都落空。
  2.3.0 因此加了 `slug_normalized`,那個站的建議數 0 → 1。
- **`shared_media` 在這個站上一組都沒配出來。** 中英兩頁各自引用自己語言目錄下的圖,
  即使是同一張圖的兩份拷貝(`2026_gmd.jpg` / `2026gmd.jpg`),工具眼中就是兩張不同的圖。
  `PairingSuggesterTests` 原本有一句註解宣稱「相簿中英共用」是香雲寺實況,那是錯的,已修正。

剩下的 49 組,看過檔名後判斷多半是真的只有單語版本,或需要真的看得懂中英文才配得起來
(`2026_emperor_liang_amitabha` 與 `2026_lianghuang_sanshi_xinian` 之類)。
**沒有安全的自動規則配得到它們,誠實列進 `missing` 就是正確行為。**

## 這份紀錄什麼時候該作廢

- 量到第二個、第三個真實站之後——`n=1` 的結論該被更多資料取代。
- 或者有人拿著一個 hreflang 齊全的鏡像來要 Stage 2。

在那之前,**「只建議、不合併」是刻意的,不是還沒做完。**

# PolyMigrate

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.PolyMigrate.svg?label=Cornhsu.PolyMigrate)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![Downloads](https://img.shields.io/nuget/dt/Cornhsu.PolyMigrate.svg)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![CI](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **The i18n-first static-site migrator** — the only site migrator that pairs your multilingual pages automatically.

**[Project write-up](https://cornhsu.com/polymigrate.html) · [NuGet](https://www.nuget.org/packages/Cornhsu.PolyMigrate) · [繁體中文說明](README.zh-Hant.md) · MIT**

PolyMigrate turns legacy dynamic sites (old PHP sites and the like) into clean, static-site-ready
Markdown — and it treats multilingual content as a first-class concern, not an afterthought.
Config-driven, fully offline-rerunnable, built on .NET.

**Status: 1.0 preview.** The extraction pipeline, pairing, verification, thumbnails and
orphan-page recovery are complete and validated against a real full-site migration (see below).

## Why

Every multilingual institution site (governments, universities, NGOs, religious organizations)
faces the same painful migration step: matching up the language versions of every page by hand.
No existing tool solves this — general-purpose scrapers extract pages one at a time and leave
the pairing to you. PolyMigrate:

- **pairs automatically** where filenames are symmetric (`/ch/news/x` ↔ `/en/news/x` share a
  `translation_key`),
- **suggests pairs heuristically** where they are not — shared photo albums, normalized dates
  hidden in slugs (`20240121` vs `01212024`), title similarity,
- **honestly reports what it cannot pair**, producing a review-ready gap inventory instead of
  guessing wrong.

Languages are not limited to two: declare any number in `lang_map` and every output
(frontmatter, inventories, pairing) expands accordingly. All locale output is standard BCP-47.

## Battle-tested defaults

The extraction pipeline bakes in fixes for real problems found during a real migration —
things generic tools and LLM extractors silently get wrong:

| Real-world pit | Built-in handling |
|---|---|
| Phone photos sideways/upside-down in thumbnails | EXIF auto-orientation before resizing |
| Titles with colons / numeric slugs with leading zeros | YAML-library escaping, forced quoting |
| `%20` double-encoding in image paths | decoded on disk, single-encoded in URLs |
| Markdown converters dropping videos / iframes / PDFs | placeholder round-trip keeps embeds in place |
| `<title>` polluted with dates and site name | body-first title extraction + configurable cleanup |
| Mixed date formats (`YYYYMMDD` / `MMDDYYYY` / `DDMMYYYY`) | all recognized and normalized |
| Old articles removed from indexes but still served | orphan probing (per-day URL candidates + suffix variants) |
| Broken images on the source site | detected, recorded, never blocks the run |
| Bot protection (JS cookie challenge → 409) | declarative cookie workaround in config |
| Legacy encodings (Big5, GB2312, …) | declared or defaulted per site |

## Case study: full temple-site migration

PolyMigrate's pipeline is the productized version of a completed real migration
(a bilingual Buddhist temple site, Chinese/English):

- **516 pages** mirrored and extracted, **4.6 GB** of media
- **281 translation keys**; **231 bilingual articles paired automatically** by symmetric paths
- built-in verifier: **1,269 internal links + 4,116 media references checked — 0 errors**
- 13 orphaned articles recovered via date probing; 141 EXIF-rotated photos fixed in thumbnails

The original Python prototype's output was used as the golden baseline while porting: 466/516
extracted bodies are byte-identical after whitespace normalization, and the remainder are
render-equivalent or strictly more faithful.

Two more numbers from that run:

- **Re-runs are ~7× faster**: media hashes are cached by `(size, mtime)`, taking a full re-run
  of the 4.6 GB site from **30.1 s down to 4.6 s**.
- **The output is deployable as-is**: `redirect_map` is auto-filled with the new paths, and
  PolyMigrate emits both an **nginx conf** and a **Netlify `_redirects`** file — turning a
  half-day of hand-written 301s into copying one file.

## Install & use

```
dotnet tool install -g Cornhsu.PolyMigrate --prerelease   # 1.0 preview on nuget.org
polymigrate extract site.yaml               # mirror HTML -> frontmatter Markdown + inventories
polymigrate verify out/                     # link/media/frontmatter audit, CI-friendly exit codes
polymigrate thumbs site.yaml                # EXIF-corrected, width-capped thumbnails
polymigrate probe-orphans site.yaml --section news --years 2021-2023
polymigrate fetch-orphans site.yaml --section news
```

One YAML config per site describes everything site-specific — see
[examples/ibps-austin.yaml](examples/ibps-austin.yaml) for a fully-annotated real example.

```yaml
config_version: 1
site:
  base_url: https://legacy.example.org
url_pattern:
  lang_map: { ch: zh-Hant, en: en }     # any number of languages
  default_lang: zh-Hant
  strip_extensions: [.php]
extract:
  content: "section[id]:not(#header):not(#footer)"
pairing:
  strategy: symmetric_path
  fallback: [shared_media, date, title_similarity]
```

## Layout

| Path | Contents |
|---|---|
| `src/PolyMigrate.Core` | extraction / pairing / verification library (NuGet: `Cornhsu.PolyMigrate.Core`) |
| `src/PolyMigrate.Cli` | the `polymigrate` CLI (NuGet tool package: `Cornhsu.PolyMigrate`) |
| `tests/` | 100 unit/integration tests + an offline fixture site with golden-file baselines |
| `docs/contracts.md` | file-format contracts between pipeline phases |

## Development

```
dotnet build
dotnet test
dotnet run --project src/PolyMigrate.Cli -- --help
```

License: [MIT](LICENSE). All dependencies are MIT/BSD/Apache-2.0
(imaging via **Magick.NET**; ImageSharp was dropped when its 4.x line began requiring a
license key at build time).

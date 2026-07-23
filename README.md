# PolyMigrate

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.PolyMigrate.svg?label=Cornhsu.PolyMigrate)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![Downloads](https://img.shields.io/nuget/dt/Cornhsu.PolyMigrate.svg)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![CI](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **The i18n-first static-site migrator** — the only site migrator that pairs your multilingual pages automatically.

**[Project write-up](https://cornhsu.com/polymigrate) · [NuGet](https://www.nuget.org/packages/Cornhsu.PolyMigrate) · [繁體中文說明](README.zh-Hant.md) · MIT**

PolyMigrate turns legacy dynamic sites (old PHP sites and the like) into clean, static-site-ready
Markdown — and it treats multilingual content as a first-class concern, not an afterthought.
Config-driven, fully offline-rerunnable, built on .NET.

**Status: 2.0.** The extraction pipeline, pairing, verification, thumbnails and orphan-page
recovery are complete and validated against a real full-site migration (see below). The **CLI
surface and the Phase output contracts remain stable** (unchanged since 1.0): new features bump
the minor version, fixes bump the patch. 2.0 is an engineering release — it narrowed the
`Cornhsu.PolyMigrate.Core` public .NET API to its intended entry points and removed unused
config fields; see the [CHANGELOG](CHANGELOG.md) for the migration notes.

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

Two channels — pick whichever runtime you already have. **Same tool, same behaviour.**

```
npx cornhsu-polymigrate extract site.yaml   # Node — no .NET install needed
dotnet tool install -g Cornhsu.PolyMigrate  # .NET
```

> The npm build ships a self-contained native binary and only downloads the one
> matching your platform (win32-x64 / linux-x64 / darwin-x64 / darwin-arm64).
> Migrating a site is usually a one-off job — installing a whole SDK for a single
> run is friction most people won't accept, so the tool meets you where you are.

```
dotnet tool install -g Cornhsu.PolyMigrate
polymigrate extract site.yaml               # mirror HTML -> frontmatter Markdown + inventories
polymigrate verify out/                     # link/media/frontmatter audit, CI-friendly exit codes
polymigrate thumbs site.yaml                # EXIF-corrected, width-capped thumbnails
polymigrate probe-orphans site.yaml --section news --years 2021-2023
polymigrate fetch-orphans site.yaml --section news
```

> No .NET? Every command above also runs with **zero install** via npm — just prefix it:
> `npx cornhsu-polymigrate extract site.yaml`, `npx cornhsu-polymigrate verify out/`, and so on.

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
  fallback: [shared_media, date, title_similarity]
```

## Use as a library

The CLI is a thin shell over `Cornhsu.PolyMigrate.Core`. To drive a migration from your own
.NET code, use the `PolyMigrator` facade — the single documented entry point:

```
dotnet add package Cornhsu.PolyMigrate.Core
```

```csharp
using PolyMigrate.Core;

var migrator = PolyMigrator.FromConfigFile("site.yaml");
var report = migrator.Extract("out/");        // out/raw, out/media -> out/content + inventories
if (report.HasErrors) { /* unsafe paths were skipped; see path_issues.csv */ }

var verify = PolyMigrator.Verify("out/");      // no config needed; reads Phase 2 output only
Console.WriteLine($"{verify.Errors} errors, {verify.Warnings} warnings");
```

## Layout

| Path | Contents |
|---|---|
| `src/PolyMigrate.Core` | extraction / pairing / verification library (NuGet: `Cornhsu.PolyMigrate.Core`) |
| `src/PolyMigrate.Cli` | the `polymigrate` CLI (NuGet tool package: `Cornhsu.PolyMigrate`) |
| `tests/` | unit/integration tests + an offline fixture site with golden-file baselines |
| `docs/contracts.md` | file-format contracts between pipeline phases |
| `docs/搬遷工具_評估與規劃書.md` | the original design/planning doc (the `§X.Y` references throughout the source point here) |

## Development

```
dotnet build
dotnet test
dotnet run --project src/PolyMigrate.Cli -- --help
```

License: [MIT](LICENSE). All dependencies are MIT/BSD/Apache-2.0
(imaging via **Magick.NET**; ImageSharp was dropped when its 4.x line began requiring a
license key at build time).

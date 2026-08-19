# PolyMigrate

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.PolyMigrate.svg?label=Cornhsu.PolyMigrate)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![Downloads](https://img.shields.io/nuget/dt/Cornhsu.PolyMigrate.svg)](https://www.nuget.org/packages/Cornhsu.PolyMigrate)
[![CI](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-polymigrate/actions/workflows/ci.yml)
[![## See also

[**多語言網站搬遷最容易搬丟的東西**](https://cornhsu.com/articles/multilingual-site-migration-pairing)
(in Traditional Chinese) — background on the category rather than this tool: the five pairing
signals in priority order, why "just read `hreflang`" often misses on real legacy sites
(measured on one: 528 pages, **0** declarations, sitemap 404), the five gates a declaration has to
pass before it is usable at all, and why URL string similarity is the wrong tool for this job.

License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **The i18n-first static-site migrator** — the only site migrator that pairs your multilingual pages automatically.

**[Project write-up](https://cornhsu.com/polymigrate) · [NuGet](https://www.nuget.org/packages/Cornhsu.PolyMigrate) · [繁體中文說明](README.zh-Hant.md) · MIT**

PolyMigrate turns legacy dynamic sites (old PHP sites and the like) into clean, static-site-ready
Markdown — and it treats multilingual content as a first-class concern, not an afterthought.
Config-driven, fully offline-rerunnable, built on .NET.

**Status: 2.3.** The extraction pipeline, pairing, verification, thumbnails and orphan-page
recovery are complete and validated against a real full-site migration (see below). The **CLI
surface and the Phase output contracts remain stable** (unchanged since 1.0): new features bump
the minor version, fixes bump the patch. 2.0 was an engineering release — it narrowed the
`Cornhsu.PolyMigrate.Core` public .NET API to its intended entry points and removed unused
config fields; 2.1 fixed up the release pipeline (arm64 npm packages, symbol packages).
2.2 closed a blind spot in `verify` — pages nothing links to were reported as fine, because a
link-following audit never reaches them — and added `slugs` for keeping up with a source site
that is still being updated. **Upgrading to 2.2 can turn a passing `verify` into exit 1**.
2.3 reads the source site's own `hreflang` declarations (see below) and reports them in a new
`hreflang_map.csv`. See the [CHANGELOG](CHANGELOG.md) for the migration notes.

## Why

Every multilingual institution site (governments, universities, NGOs, religious organizations)
faces the same painful migration step: matching up the language versions of every page by hand.
No existing tool solves this — general-purpose scrapers extract pages one at a time and leave
the pairing to you. PolyMigrate:

- **pairs automatically** where filenames are symmetric (`/ch/news/x` ↔ `/en/news/x` share a
  `translation_key`),
- **reads the site's own `hreflang` declarations** — `<link rel="alternate" hreflang>` is the one
  pairing signal the site's authors *stated* rather than something a tool inferred, so it is
  tried first and can pair across sections (`/ch/news/` ↔ `/en/press/`),
- **suggests pairs heuristically** where neither applies — slugs that differ only in separators
  (`2025-light-offering` vs `2025_light_offering`), shared photo albums, normalized dates hidden
  in slugs (`20240121` vs `01212024`), title similarity,
- **honestly reports what it cannot pair**, producing a review-ready gap inventory instead of
  guessing wrong.

Note what PolyMigrate deliberately does *not* do: match language versions by URL string
similarity. `/products/`, `/produkte/` and `/產品/` have a similarity of zero and are the same
page; symmetric-path pairing is an exact match after the language prefix is stripped, never a
fuzzy one.

Languages are not limited to two: declare any number in `lang_map` and every output
(frontmatter, inventories, pairing) expands accordingly. All locale output is standard BCP-47.

### About `hreflang`

Legacy sites are exactly the sites whose `hreflang` is missing or wrong — usually bolted on by
an SEO contractor years later. So PolyMigrate treats it as strong evidence, not as gospel.
A declaration is only used for pairing if it:

- isn't `x-default` (that's "where to go when nothing matches", not a language version),
- doesn't point at the page itself,
- resolves to a page actually in your mirror — **same host included**, since plenty of sites put
  the English version on `en.example.org`, where a path-only comparison would make a page its
  own translation,
- and **isn't one of many same-language pages claiming the same target**. This is the classic
  failure: one `<link>` pasted into the site-wide template, so every Chinese page declares the
  English homepage as its English version. One page cannot be the translation of three others,
  so all of those claims are dropped rather than turned into a confident-looking wrong pair.

Everything declared — including the rejected ones — is written to `hreflang_map.csv` with
`in_mirror` / `reciprocal` / `usable` and a `reject_reason` naming which rule it failed, because
"can I trust this site's hreflang?" is a question you want answered with data, not a total.

Pairs are still only ever *suggested*: `hreflang` raises the evidence quality, it does not
switch the tool into merging pages on its own. That is a deliberate call, not an unfinished
one — the measurement behind it, and what would have to change to revisit it, are recorded in
[docs/hreflang_量測與決策.md](docs/hreflang_量測與決策.md).

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
| `hreflang` pointing at dead URLs, other hosts, or itself | validated against the mirror; unusable ones recorded, not applied |
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
polymigrate verify out/                     # link/media/frontmatter/unlinked-page audit, CI exit codes
polymigrate thumbs site.yaml                # EXIF-corrected, width-capped thumbnails
polymigrate probe-orphans site.yaml --section news --years 2021-2023
polymigrate fetch-orphans site.yaml --section news
polymigrate slugs . --section news          # slugs already mirrored under ./raw (pipeable)
```

> **`raw/` is the one thing you cannot regenerate.** Re-crawling a site takes hours at a
> polite request rate, and the source site may be switched off the moment you finish
> migrating — at which point your mirror is the only copy left. No PolyMigrate command
> ever deletes or overwrites `raw/` or `media/`; keep them until the migration is signed
> off, and back them up if the source is going away.

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
  fallback: [hreflang, slug_normalized, shared_media, date, title_similarity]
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
| `docs/hreflang_量測與決策.md` | why `hreflang` only ever *suggests* — the measurement behind that call |
| `docs/搬遷工具_評估與規劃書.md` | the original design/planning doc (the `§X.Y` references throughout the source point here) |

## Development

```
dotnet build
dotnet test
dotnet run --project src/PolyMigrate.Cli -- --help
```

License: [MIT](LICENSE). All dependencies are MIT/BSD/Apache-2.0
(imaging via **Magick.NET**; ImageSharp was dropped when its 4.x line began requiring a
license key at build time). See [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).

PolyMigrate fetches pages from the site you point it at, and can be configured to send
cookies past a bot challenge. **It assumes the site is yours, or that you are migrating
it with the owner's permission** — that is what it is for. Check the source site's terms
before pointing it at anything else.

# Third-party notices

PolyMigrate itself is licensed under the [MIT License](LICENSE). It builds on the
following third-party packages. All of their licences are compatible with MIT, and
none of them are copyleft.

| Package | Licence | Project |
|---|---|---|
| AngleSharp | MIT | https://anglesharp.github.io/ |
| ReverseMarkdown | MIT | https://github.com/mysticmind/reversemarkdown-net |
| YamlDotNet | MIT | https://github.com/aaubry/YamlDotNet |
| Magick.NET (`Magick.NET-Q8-AnyCPU`, `Magick.NET.Core`) | Apache-2.0 | https://github.com/dlemstra/Magick.NET |

## Why this file exists

For the NuGet packages (`Cornhsu.PolyMigrate`, `Cornhsu.PolyMigrate.Core`) the
dependencies are resolved by NuGet and each package carries its own licence — nothing
further is required from us.

**The npm channel is different.** `cornhsu-polymigrate` ships a self-contained native
binary that embeds `Magick.Native-Q8-*` directly, which makes it a redistribution of
Magick.NET and of ImageMagick. Magick.NET is Apache-2.0 and its package includes a
`Notice.txt`, so section 4(d) of the Apache License applies: that notice must travel
with the redistribution.

## Magick.NET / ImageMagick

> Copyright Dirk Lemstra, https://github.com/dlemstra/Magick.NET
> Licensed under the Apache License, Version 2.0.
>
> Bundles ImageMagick, used under the ImageMagick License —
> https://imagemagick.org/license/

The full, authoritative notice ships inside the Magick.NET package as `Notice.txt`
(and covers the exact ImageMagick version bundled by that release). Reproduce that
file, not this summary, when redistributing.

**This is automated, not a checklist item.** `npm/prepare.mjs` resolves the actual
Magick.NET version from `project.assets.json`, reads `Notice.txt` from the NuGet cache,
and writes it into every platform package as `NOTICE.txt` — **aborting the build if it
cannot find it**. `release.yml` then verifies the built packages really carry it. A
licence obligation that depends on someone remembering a checklist is one that will
eventually be missed.

## Keeping this current

This file lists licences as of the versions pinned in `src/*/*.csproj`. **When a
dependency is added, removed, or bumped to a version with a different licence, update
this file in the same commit** — Dependabot bumps will not do it for you.

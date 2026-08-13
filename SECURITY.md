# Security policy

## Reporting a vulnerability

**Please don't open a public issue for anything security-sensitive.**

Use GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/HSU-YU-MING/cornhsu-polymigrate/security/advisories/new)
(also reachable from the repository's **Security** tab).

This is maintained by one person as a side project — reports are handled on a best-effort
basis, so please allow a few days for a first response.

## What's in scope

PolyMigrate runs locally against a site you already control, so the interesting surface is what
it *consumes*, not what it exposes:

- **Untrusted input from the source site.** Slugs, filenames, links and media paths all come from
  someone else's HTML. Anything that lets a crafted source site write outside the output
  directory, overwrite files it shouldn't, or produce output that is unsafe for the tools that
  consume it, is in scope. `PathSafety` and the CSV formula-injection guard in `Csv.Write` exist
  for exactly this — holes in them count.
- **The bundled native ImageMagick.** The npm packages ship a self-contained binary that embeds
  `Magick.Native`, so an ImageMagick vulnerability reachable through `polymigrate thumbs` reaches
  our users too. Dependency versions are tracked by Dependabot, but a report showing something is
  *reachable from PolyMigrate* is more useful than a version number.
- **The published packages.** Anything suggesting that what's on
  [NuGet](https://www.nuget.org/packages/Cornhsu.PolyMigrate) or
  [npm](https://www.npmjs.com/package/cornhsu-polymigrate) doesn't match this repository.
  Releases are built by `release.yml` from a `v*` tag using OIDC trusted publishing — there is no
  long-lived publishing key to steal, and nothing is ever pushed from a laptop.

## What's out of scope

- **`site.auth_workaround` sending cookies.** It sends the cookies you configured, to the site you
  named, to get past a bot challenge on a site you're migrating. That's the feature, not a leak.
- **Crawling a site you don't have permission to migrate.** That's a misuse question — see the
  note in the [README](README.md) — not a vulnerability in the tool.
- **A malicious config file.** The tool trusts its own config by design. Someone who can edit your
  YAML already has your machine.

## Supported versions

Only the latest release. Fixes ship as a new patch version; there are no backports.

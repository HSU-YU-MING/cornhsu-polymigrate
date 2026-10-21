# Contributing to PolyMigrate

## Dev setup

Requires the .NET 10 SDK. Everything runs from the repo root against `PolyMigrate.slnx`.

```sh
dotnet build   -c Release
dotnet test    -c Release
dotnet format  --verify-no-changes    # style/format gate (CI enforces this)
```

Run the CLI locally:

```sh
dotnet run --project src/PolyMigrate.Cli -- --help
```

## Tests

- **Unit tests** live in `tests/PolyMigrate.Core.Tests` (library) and `tests/PolyMigrate.Cli.Tests`
  (CLI arg-parsing + exit codes). Internal types are testable via `InternalsVisibleTo`.
- **Golden tests** (`GoldenTests.cs`) run the full pipeline over the offline fixture site
  (`tests/fixtures/site`) and diff every output file (except `verify_report.csv` and
  `.polymigrate/`) against committed baselines in `tests/fixtures/golden` — this is how
  output-contract stability is guarded. The diff compares *text*: it reads each file with the
  BOM dropped and, for everything except Markdown, strips every `\r` (CSVs are CRLF by contract),
  so BOMs and CSV line endings are not checked there. Markdown is compared as-is because its
  contract is LF-only on every platform. The byte format (CSV = UTF-8 with BOM + CRLF, Markdown
  without BOM and LF-only) is pinned by `Extraction/OutputBytesTests.cs`.

### Updating the golden baselines

When a change *intentionally* alters pipeline output, regenerate the baselines and review the diff:

```sh
POLYMIGRATE_UPDATE_GOLDEN=1 dotnet test -c Release --filter FixtureSite_MatchesGolden
git diff tests/fixtures/golden      # eyeball every change before committing
```

Never regenerate to make a red test green without understanding *why* the output changed —
the golden diff is the review. `tests/fixtures/**` is pinned `-text` in `.gitattributes` so git
never converts line endings in the fixture input or the baselines on any platform; don't let an
editor reformat them.

### Updating the public API baseline

`PublicApiTests.PublicSurface_MatchesApprovedBaseline` snapshots every public type and member of
`Cornhsu.PolyMigrate.Core` into `tests/PolyMigrate.Core.Tests/PublicApi.approved.txt`. It goes red
whenever the library's public surface changes — including changes that still compile for callers,
such as adding an optional parameter (source-compatible, binary-breaking). The failure message
lists what was added and what was removed or changed.

**Decide the semver bump first, then update the baseline:** additions are minor; removing or
changing an existing signature, or narrowing visibility, is major. Once the change is intentional:

```sh
POLYMIGRATE_UPDATE_API=1 dotnet test -c Release --filter PublicSurface_MatchesApprovedBaseline
git diff tests/PolyMigrate.Core.Tests/PublicApi.approved.txt   # the diff is the semver review
```

## Conventions

- **Branch + PR** into `main`; CI (format + 3-OS test + pack) must be green. Releases go out by
  pushing a `v*` tag — see [RELEASING.md](RELEASING.md).
- **Public API** of `Cornhsu.PolyMigrate.Core` is a semver contract. Keep new helper types
  `internal` unless they're genuinely part of the library surface (the `PolyMigrator` facade is the
  intended entry point). The output-file formats and frontmatter fields are contracts too — see
  [docs/contracts.md](docs/contracts.md).
- **Adding a site**: write a config (see [examples/ibps-austin.yaml](examples/ibps-austin.yaml),
  fully annotated). Site-specific knowledge belongs in the YAML, not in code.
- Comments and design notes are in Traditional Chinese; public API names, README, and CLI `--help`
  are in English. The `§X.Y` references point at [docs/搬遷工具_評估與規劃書.md](docs/搬遷工具_評估與規劃書.md).

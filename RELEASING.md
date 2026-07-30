# Releasing

## Status

| | |
| --- | --- |
| Mod name `fdash-exporter` on the portal | available (as of 2026-07-30) |
| Thumbnail, changelog, locale, LICENSE | present |
| CI (`.github/workflows/ci.yml`) | builds backend + frontend, runs self-tests and Lua checks |
| Backend compiles | **unverified** — no .NET SDK was installed on the development machine |
| Mod loaded in Factorio | **unverified** — no Factorio was installed there |

The last two rows are the real remaining risk. The first push resolves the first one through
CI; the second needs an actual start of the game (see "Test before uploading").

## Before the very first commit

Until the mod rewrite, the repository held things that must not go public. `.gitignore` catches
them — but only if they were never committed. So in a fresh repository, check first:

```bash
git status --short
```

**None** of this may appear in that list:

- `fdash.db`, `*.db-wal`, `*.db-shm` — the time-series database (~460 MB in the working copy;
  GitHub rejects files over 100 MB without LFS).
- `appsettings.Local.json` — holds the RCON password.
- `publish/`, `bin/`, `obj/`, `node_modules/`.

Also: until the mod rewrite, `src/Fdash.Api/appsettings.json` carried an RCON password in
plain text. The value is empty now. **If that password is still valid on a reachable server,
change it** — it sat for a long time in a file that is public now. A local copy still lives in
`publish/appsettings.json` (not in the repository, but on disk). And if you ever publish this
directory with its existing git history: old commits still contain it, in which case a fresh
repository without history is the easier route.

## Placeholders in the mod

Done for `boehla` / `github.com/boehla/factorio-dashboard`: `author`, `contact` and `homepage`
in `mod/fdash-exporter/info.json`, plus the copyright lines in `mod/fdash-exporter/LICENSE`
and the root `LICENSE`.

The only thing left is if your **mod portal username** differs from the GitHub one — then
adjust `author` in `info.json`. CI warns as long as `SET-ME` appears anywhere.

The internal name `fdash-exporter` was still free on 2026-07-30
(<https://mods.factorio.com/mod/fdash-exporter> → 404). If it has been taken since, it has to
change in `info.json` **and** in three places in the code (`remote.add_interface("fdash", …)`
is unaffected — that is the interface name, not the mod name):

```bash
grep -rn "fdash-exporter" mod/fdash-exporter/
```

The thumbnail (`mod/fdash-exporter/thumbnail.png`, 144×144) is ready and gets packed by
`build-mod.ps1` automatically. To regenerate it from the source image in `mod/assets/`:

```bash
powershell -ExecutionPolicy Bypass -File .\mod\make-thumbnail.ps1
```

The script downscales in steps rather than one jump — going straight from 1024 to 144 frays
the thin lines (the chart curve, the window frame).

## Building the mod

```bash
powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1
```

Produces `mod/dist/fdash-exporter_<version>.zip` containing exactly one top-level folder
`fdash-exporter_<version>/` — which is what Factorio expects.

To test locally before uploading:

```bash
powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1 -Install
```

## Test before uploading

1. Start Factorio, load a large save.
2. `/fdash-status` — `scanning` has to go from `true` to `false`.
3. Check `script-output/fdash/`: `index.json`, `snapshot-N.json`, `prototypes.json`.
4. `dotnet run --project tests/Fdash.LiveCheck` with `FDASH_SCRIPT_OUTPUT` set.
5. F5 in game → debug overlay, watch the tick time. The mod should disappear into the noise;
   if it does not, lower `fdash-entity-budget` and compare the difference.
6. Save/load cycle: save, load, `/fdash-status` — the registry counts have to survive, with no
   second initial scan.

## Bumping the version

On every change:

1. `version` in `mod/fdash-exporter/info.json`.
2. A new block in `mod/fdash-exporter/changelog.txt`. The format is strict — the separator
   line must be **exactly 99 dashes**, `Date:` as `YYYY-MM-DD`, categories indented by two
   spaces and entries by four.
3. If the snapshot format changes incompatibly: raise `PROTOCOL` in
   `mod/fdash-exporter/scripts/publish.lua` **and** `ModSnapshotParser.SupportedProtocol` in
   `src/Fdash.Core/ModSnapshot.cs`.

## Portal upload

<https://mods.factorio.com/upload>. For the description, the content of
`mod/fdash-exporter/README.md` mostly fits — but portal markdown cannot render tables, so
rewrite those as lists first.

Worth stating clearly in the description: the mod **does nothing on its own**. It has no GUI,
no entities, no recipes. Anyone installing it without its counterpart just gets files in
`script-output/`.

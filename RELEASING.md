# Releasing

## Status

| | |
| --- | --- |
| Mod name `fdash-exporter` on the portal | available (as of 2026-07-30) |
| Thumbnail, changelog, locale, LICENSE | present |
| CI (`.github/workflows/ci.yml`) | builds backend + frontend, runs self-tests and Lua checks |
| Backend compiles | verified by CI and locally (0 warnings, 28/28 self-tests) |
| Mod loaded in Factorio | verified 2026-07-31 on 2.0.77 + Pyanodons (see below) |
| Mod to dashboard end to end | verified 2026-07-31, live data in the browser |

### What the in-game test covered

Run against a Pyanodons save (2600 assemblers, 660 drills, 20000 poles, 17400 chunks) on a
headless server, Factorio 2.0.77:

- Mod loads, initial scan completes, all ten collectors publish plausible data.
- Snapshot rotation, `index.json` and the prototype export (10117 recipes) all work; the
  snapshot structure matches what `ModSnapshotParser` expects.
- Save/load: registry survives, no second initial scan.
- Both transports: file output and `remote.call("fdash", …)` over RCON.
- Cost ~0.8–1.1 ms per tick at the default budget (details in the mod README).

It found three bugs, all fixed: settings were declared in a `settings.json`, which Factorio
does not read at all (so every setting silently fell back to its default and the entity budget
could not be changed); `/fdash-status` printed nothing when called over RCON; and an empty
train cargo serialised as `{}` where the consumer expects a list.

The whole chain was then run end to end against that save: mod writes snapshots, the collector
picks them up over the file transport, `/api/snapshots` serves all ten jobs plus the derived
ones, and the frontend renders live numbers in the browser. Item icons resolve for modded Py
items too, from a `--dump-data` run.

What is still untested: Space Age (the save has no Space Age, so `platforms` and `orbital`
never ran), multiplayer with actual clients, and auto-research actually writing.

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

`-Install` targets `%APPDATA%\Factorio\mods`. A portable install (a zip package, recognisable
by `config-path.cfg` with `use-system-read-write-data-directories=false`) keeps its mods under
the install directory instead — point `-ModsDir` at it:

```bash
powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1 -Install -ModsDir C:\Data\Factorio\mods
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

## Cutting a GitHub release

Pushing a version tag triggers `.github/workflows/release.yml`:

```bash
git tag v0.1.0
git push origin v0.1.0
```

That builds the mod zip and a self-contained Windows dashboard, then creates the release with
both attached. Release notes come from the changelog block for that version, so the portal
changelog and the GitHub release cannot drift apart.

The workflow refuses to run if the tag disagrees with `version` in `info.json`, if any
`SET-ME` placeholder is left, if the packaged zip has the wrong layout, or if the published
`appsettings.json` somehow carries an RCON password. Versions starting with `0.` are marked
as pre-release.

To undo a release: delete it on GitHub, then `git push --delete origin v0.1.0`.

## Portal upload

<https://mods.factorio.com/upload>. For the description, the content of
`mod/fdash-exporter/README.md` mostly fits — but portal markdown cannot render tables, so
rewrite those as lists first.

Worth stating clearly in the description: the mod **does nothing on its own**. It has no GUI,
no entities, no recipes. Anyone installing it without its counterpart just gets files in
`script-output/`.

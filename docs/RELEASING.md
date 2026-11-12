# Release process

How a new version of the mod is built, tagged and published to GitHub.
The process is repeatable from a clean clone plus a Silksong install and the
pinned external inputs. It is **not** a byte-for-byte reproducible build: BepInEx
is downloaded, the game DLLs come from the local install, and archive metadata may
vary between machines. The published SHA-256 manifest is the integrity reference
for the exact artifacts that were released.

## 0. Prerequisites

| Requirement | Check |
|---|---|
| Git + write access to `Nirok85/silksong-coop-mod` | `git push --dry-run origin master` |
| `gh` authenticated | `gh auth status` |
| .NET SDK 8 (also used for `netstandard2.1`) | `dotnet --version` |
| Silksong installed (for the game's managed DLLs) | see `ManagedDir` below |

`git` authenticates through `gh`:

```bash
git config --global credential.https://github.com.helper '!gh auth git-credential'
```

The token must have **Contents: Read and write**. It must *additionally* have
**Workflows: Read and write** if the release touches `.github/workflows/` —
otherwise the push is rejected with
`refusing to allow a Personal Access Token to create or update workflow ...`.
Fine-grained tokens get permissions at <https://github.com/settings/personal-access-tokens>.

If `dotnet` is installed per-user and the shell cannot find it:

```bash
export PATH="$HOME/.dotnet:$PATH"
```

`ManagedDir` is the game's managed assemblies directory:

```bash
export ManagedDir="$HOME/.steam/steam/steamapps/common/Hollow Knight Silksong/Hollow Knight Silksong_Data/Managed"
```

## 1. Bump the version

The version lives in project metadata, the plugin constant, changelog, README files and roadmap — a release that updates only some of them
produces archives whose filenames disagree with the plugin version reported in
`BepInEx/LogOutput.log`:

| File | What |
|---|---|
| `src/SilksongCoop.Mod/SilksongCoop.Mod.csproj` | `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |
| `src/SilksongCoop.Core/SilksongCoop.Core.csproj` | `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |
| `src/SilksongCoop.Mod/SilksongCoopPlugin.cs` | `public const string PluginVersion` |
| `CHANGELOG.md` | new `## [x.y.z] - YYYY-MM-DD` section |
| `README.md`, `README.ru.md` | version badge, archive names, download link |
| `ROADMAP.md` | current release line |

`assets/INSTALL.txt` needs no edit: `scripts/package-release.sh` substitutes
`__VERSION__` while packaging.

Sanity check before committing:

```bash
OLD=2.2.1   # the previous version
grep -rn "$OLD" --include='*.cs' --include='*.csproj' --include='*.md' . \
  | grep -v '/bin/\|/obj/'
```

Or run the cross-platform preflight, which verifies both csproj versions,
`PluginVersion`, `CHANGELOG.md`, both READMEs, `ROADMAP.md`, the working tree,
the build and the tests:

```powershell
pwsh ./scripts/preflight-release.ps1 -AllowDirty
```

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/): the
section is the release body, so write it for players, not only for maintainers.

## 2. Verify

```bash
dotnet build SilksongCoop.sln -c Release
dotnet test tests/SilksongCoop.Tests/SilksongCoop.Tests.csproj -c Release
```

Expected: 0 warnings (warnings are errors in Release) and all tests passing.
The hosted CI job builds Core and runs the Core test project. The Mod job is
eligible on pushes and manual runs on `master` or a `v*` release tag when
`SILKSONG_MOD_RUNNER_ENABLED` is set to the exact string `true`. Otherwise,
GitHub skips it. If the job is eligible but no matching self-hosted runner is
online, it can stay queued. A green Core job alone does not verify Mod compilation
or Steam/Unity behaviour. Build the Mod against the game assemblies and run the
manual smoke checklist before publishing.

Read the **Mod build status** job for the result. To require a successful Mod
build when dispatching CI on protected `master`, enable `require_mod`.

If the protocol changed, say so explicitly in the release notes — mixed
versions refuse to connect, so both players must update.

## 3. Build the archives

```bash
bash scripts/package-release.sh dist
```

On Windows the same archives are produced by:

```powershell
pwsh ./scripts/package-release.ps1 -ManagedDir "D:\SteamLibrary\steamapps\common\Hollow Knight Silksong\Hollow Knight Silksong_Data\Managed" -BepInExSha256 <pinned-hash>
```

`-OutputDir` is optional; when the script is launched with `powershell -File` and
the parameter is omitted, it defaults to `<repo>/dist`.

Produces, with the version read from `SilksongCoop.Mod.csproj`:

| File | Contents |
|---|---|
| `dist/SilksongCoop_vX.Y.Z.zip` | Windows, self-contained (BepInEx 5.4.21 x64 bundled) + docs/licenses |
| `dist/SilksongCoop_vX.Y.Z_plugin-only.zip` | the two DLLs + config + licenses + docs |
| `dist/SilksongCoop_vX.Y.Z_SHA256SUMS.txt` | hashes of both archives |

The script downloads BepInEx unless `BEPINEX_ZIP` points at a local copy;
`SKIP_BUNDLE=1` builds only the plugin-only package. A caller-provided archive
**must** be pinned with `-BepInExSha256`/`BEPINEX_SHA256`; the script refuses to
build otherwise (`-AllowUnpinnedBepInEx` / `ALLOW_UNPINNED_BEPINEX=1` exists only
for local tests, never for a release). A downloaded archive is checked against the
built-in SHA-256 pin. The plugin-only build now writes its own
`SilksongCoop_vX.Y.Z_SHA256SUMS.txt` too. Check that ZIP entry names use `/` so the
plugin-only archive extracts correctly on Unix. Do not publish without the hashes.

Verify the produced files before tagging:

```powershell
pwsh ./scripts/verify-artifacts.ps1 -Version X.Y.Z
# If artifacts were written elsewhere, pass -Directory <artifact-directory>.
```

The script checks both hashes, both DLLs, the BepInEx injector files and rejects
archives that accidentally contain proprietary game assemblies. It reads `dist/`
by default and refuses a `-Version` that does not match the version in
`src/SilksongCoop.Mod/SilksongCoop.Mod.csproj`.

## 4. Commit, tag, push

Tag the commit that contains the release, not an earlier one:

```bash
git add -A && git commit -m "release: vX.Y.Z"
git tag -a vX.Y.Z -m "Silksong Co-op Mod vX.Y.Z — <one-line summary>"
git push origin master
git push origin vX.Y.Z
```

Wait for CI on `master` and on the release tag to go green (`gh run watch`).
The `v*` tag triggers its own workflow run; check both runs before publishing.

## 5. Publish the GitHub release

The release body is the `CHANGELOG` section for this version:

```bash
V=X.Y.Z
awk -v v="$V" 'index($0,"## [" v "]")==1 {f=1} f && /^## \[/ && index($0,"## [" v "]")!=1 {exit} f' \
  CHANGELOG.md > /tmp/notes.md

# PowerShell alternative (same section, written to dist/<version>/release-notes.md):
pwsh ./scripts/release-notes.ps1 -Version X.Y.Z -OutputPath dist/X.Y.Z/release-notes.md

gh release create vX.Y.Z \
  dist/SilksongCoop_vX.Y.Z.zip \
  dist/SilksongCoop_vX.Y.Z_plugin-only.zip \
  dist/SilksongCoop_vX.Y.Z_SHA256SUMS.txt \
  --title "Silksong Co-op Mod vX.Y.Z — <headline>" \
  --notes-file /tmp/notes.md
```

For a beta, add `--prerelease` and use a prerelease tag such as
`vX.Y.Z-beta.1`; the plugin and archive version remain numeric `X.Y.Z`.
`gh release create` publishes immediately and marks a stable release as *Latest*.
Add `--draft` first if the notes need review, then
`gh release edit vX.Y.Z --draft=false`.

## 6. Post-release checks

```bash
gh release view vX.Y.Z                       # assets present, marked Latest
gh run list --limit 3                        # master build green
gh release download vX.Y.Z -D /tmp/relcheck  # archives actually downloadable
```

Verify the downloaded archives against the published SHA256 manifest and that
`dist/` is not committed (`dist/` is ignored by git). Re-download after publishing:
a local file can differ from the uploaded asset.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `fatal: could not read Username for 'https://github.com'` | no git credentials: run `gh auth login`, set the credential helper above |
| `refusing to allow a Personal Access Token to create or update workflow` | token lacks the **Workflows** permission (see step 0) |
| `zip I/O error: No such file or directory` | fixed in `72adf85`; on older revisions pass an absolute output directory |
| `dotnet: command not found` | per-user SDK: `export PATH="$HOME/.dotnet:$PATH"` |
| `release create` returns 403 / `Resource not accessible by personal access token` | token lacks **Contents: Read and write** |
| Release published without assets | assets are uploaded separately — pass them on the `gh release create` command line |
| CI red on `master` after tagging | delete the tag (`git push --delete origin vX.Y.Z`) and the release before re-tagging; do not move a published tag |

# Mod CI job (self-hosted runner)

The `Core` job in `.github/workflows/ci.yml` runs on GitHub-hosted runners and is
green. The `Mod` job cannot run there: the mod compiles against the game's
managed assemblies (`Assembly-CSharp.dll`, `UnityEngine.*.dll`,
`TeamCherry.TK2D.dll`, …), which cannot be redistributed.

The `mod:` job is therefore gated on a repository variable and a self-hosted
runner. By default it is **skipped** (GitHub marks it "skipped", it does not
queue):

```yaml
  mod:
    name: Mod - compile against game assemblies (self-hosted)
    if: ${{ vars.ENABLE_MOD_CI == 'true' && github.event_name != 'pull_request' }}
    runs-on: [self-hosted, linux, silksong]
    environment: silksong-mod-build
```

## Enabling it

1. Register a self-hosted runner with the labels `self-hosted`, `linux` and
   `silksong`, and make sure the runner user can read the game installation.
2. Set two repository variables
   (*Settings → Secrets and variables → Actions → Variables*):

   | Variable | Example |
   |---|---|
   | `ENABLE_MOD_CI` | `true` |
   | `SILKSONG_MANAGED_DIR` | `/home/runner/.local/share/Steam/steamapps/common/Hollow Knight Silksong/Hollow Knight Silksong_Data/Managed` |

3. Push to `master` or open a pull request. The job validates that
   `Assembly-CSharp.dll` exists and then builds the Mod with
   `TreatWarningsAsErrors=true`.

Until `ENABLE_MOD_CI` is `true`, the job stays skipped. Unset the variable if the
runner is offline so the workflow does not queue forever.

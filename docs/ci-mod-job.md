# Mod CI job (self-hosted runner)

The `Core` job in `.github/workflows/ci.yml` runs on GitHub-hosted runners and is
green. The `Mod` job cannot run there: the mod compiles against the game's
managed assemblies (`Assembly-CSharp.dll`, `UnityEngine.*.dll`,
`TeamCherry.TK2D.dll`, …), which cannot be redistributed.

The job is prepared below. It is **not** applied to `ci.yml` yet because the
token used to push this repository does not have the `workflow` scope. Apply it
in one of two ways:

- **GitHub web UI:** `.github/workflows/ci.yml` → *Edit* → replace the existing
  `mod:` job (it currently has `if: false`) with the block below.
- **CLI with a PAT that has the `workflow` scope:** `git apply` the change locally
  and push.

After applying, register a self-hosted runner with the labels
`self-hosted`, `linux`, `silksong`, then set two repository variables
(*Settings → Secrets and variables → Actions → Variables*):

| Variable | Example |
|---|---|
| `ENABLE_MOD_CI` | `true` |
| `SILKSONG_MANAGED_DIR` | `/home/runner/.local/share/Steam/steamapps/common/Hollow Knight Silksong/Hollow Knight Silksong_Data/Managed` |

Until `ENABLE_MOD_CI` is `true`, the job is skipped and never queues.

```yaml
  # The Mod project needs the game's managed assemblies (Assembly-CSharp, UnityEngine,
  # TeamCherry.TK2D, ...), which cannot be redistributed. It therefore runs only on a
  # self-hosted runner that has Silksong installed.
  #
  # Enable it by:
  #   1. registering a self-hosted runner with labels `self-hosted`, `linux`, `silksong`;
  #   2. setting repository variables ENABLE_MOD_CI=true and
  #      SILKSONG_MANAGED_DIR=/path/to/Hollow Knight Silksong_Data/Managed
  # Until then the job is skipped instead of queuing forever.
  mod:
    name: Mod - compile against game assemblies (self-hosted)
    if: ${{ vars.ENABLE_MOD_CI == 'true' }}
    runs-on: [self-hosted, linux, silksong]
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Validate Managed directory
        env:
          MANAGED_DIR: ${{ vars.SILKSONG_MANAGED_DIR }}
        run: |
          if [ -z "$MANAGED_DIR" ]; then
            echo "::error::SILKSONG_MANAGED_DIR repository variable is not set"
            exit 1
          fi
          test -f "$MANAGED_DIR/Assembly-CSharp.dll" || { echo "::error::Assembly-CSharp.dll not found in $MANAGED_DIR"; exit 1; }

      - name: Build Mod (warnings as errors)
        env:
          MANAGED_DIR: ${{ vars.SILKSONG_MANAGED_DIR }}
        run: dotnet build src/SilksongCoop.Mod/SilksongCoop.Mod.csproj -c Release -p:TreatWarningsAsErrors=true -p:ManagedDir="$MANAGED_DIR"
```

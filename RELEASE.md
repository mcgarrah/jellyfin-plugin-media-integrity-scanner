# Release Process

## Version Number Format

| Location | Format | Example |
|----------|--------|---------|
| Git tag | `v{Major}.{Minor}.{Patch}` | `v0.1.0` |
| Directory.Build.props | `{Major}.{Minor}.{Patch}.0` | `0.1.0.0` |
| manifest.json | `{Major}.{Minor}.{Patch}.0` | `0.1.0.0` |

The git tag and the internal version must stay in sync. The only differences are:
- Git tag has a `v` prefix (git convention)
- Assembly version has a fourth `.0` (Jellyfin/NuGet convention)

## Files to Update When Bumping Version

1. **`Directory.Build.props`** — update all three:
   ```xml
   <Version>X.Y.Z.0</Version>
   <AssemblyVersion>X.Y.Z.0</AssemblyVersion>
   <FileVersion>X.Y.Z.0</FileVersion>
   ```

2. **`manifest.json`** — no manual edit needed. `release.yml` runs `scripts/update-manifest.py` after every tagged release, which prepends a new `versions` entry (version, changelog, `targetAbi`, download URL, MD5 checksum, timestamp) and commits it back to `main` automatically. Re-running for the same tag is idempotent (it replaces rather than duplicates that version's entry).

## Release Steps

1. **Bump version** in `Directory.Build.props`
2. **Commit** the version bump:
   ```bash
   git add Directory.Build.props
   git commit -m "Bump version to X.Y.Z"
   ```
3. **Push** to main (or merge a PR):
   ```bash
   git push origin main
   ```
4. **Tag** the release commit:
   ```bash
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```
5. **GitHub Actions** (`release.yml`) automatically:
   - Builds the plugin in Release configuration
   - Packages it as `media-integrity-scanner-vX.Y.Z.zip`
   - Creates a GitHub Release with the zip attached
   - Generates release notes from commit history
   - Runs `scripts/update-manifest.py` to bump `manifest.json` (version, checksum, `targetAbi`, download URL) and pushes that commit to `main` — this assumes the tag was created from the current tip of `main`

No further manual steps are required — verify the GitHub Release and the `manifest.json` commit landed, then confirm the plugin repository picks up the new version in Jellyfin's Catalog.

## Checksum

`scripts/update-manifest.py` computes the MD5 checksum of the release zip automatically; no manual `md5sum` step is needed. Jellyfin uses this checksum to verify plugin downloads.

## When to Bump Which Number

- **Patch** (0.1.0 → 0.1.1): Bug fixes, minor tweaks, no new features
- **Minor** (0.1.0 → 0.2.0): New features, non-breaking changes
- **Major** (0.x.x → 1.0.0): First stable release, or breaking changes after 1.0

## Notes

- The manifest.json `targetAbi` field specifies the minimum Jellyfin version required. `scripts/update-manifest.py` derives it from the `Jellyfin.Controller` package reference in the `.csproj`, so bumping that package reference is enough to change it — no separate manual edit needed.
- Keep old version entries in manifest.json — Jellyfin uses them to show available versions and allow downgrades. The automation only replaces the entry matching the tag being released; older entries are untouched.
- The fourth number (`.0`) can be used for build increments but we keep it at `0` for simplicity.

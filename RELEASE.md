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

2. **`manifest.json`** — add a new entry to the `versions` array (keep old entries for history):
   ```json
   {
     "version": "X.Y.Z.0",
     "changelog": "Description of changes",
     "targetAbi": "10.11.0.0",
     "sourceUrl": "https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/releases/download/vX.Y.Z/media-integrity-scanner-vX.Y.Z.zip",
     "checksum": "<md5 of the zip>",
     "timestamp": "2026-MM-DDT00:00:00Z"
   }
   ```

## Release Steps

1. **Bump version** in `Directory.Build.props` and `manifest.json`
2. **Commit** the version bump:
   ```bash
   git add Directory.Build.props manifest.json
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

6. **Update manifest.json** with the download URL and checksum from the release:
   - Download URL: `https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/releases/download/vX.Y.Z/media-integrity-scanner-vX.Y.Z.zip`
   - Checksum: `md5sum media-integrity-scanner-vX.Y.Z.zip`
   - Commit and push this update to main

## Checksum

Jellyfin uses MD5 checksums in the manifest to verify plugin downloads. After the release zip is built:

```bash
md5sum media-integrity-scanner-vX.Y.Z.zip
```

Put the hash in the `checksum` field of `manifest.json`.

## When to Bump Which Number

- **Patch** (0.1.0 → 0.1.1): Bug fixes, minor tweaks, no new features
- **Minor** (0.1.0 → 0.2.0): New features, non-breaking changes
- **Major** (0.x.x → 1.0.0): First stable release, or breaking changes after 1.0

## Notes

- The manifest.json `targetAbi` field specifies the minimum Jellyfin version required. Update this if the plugin starts using APIs from a newer Jellyfin release.
- Keep old version entries in manifest.json — Jellyfin uses them to show available versions and allow downgrades.
- The fourth number (`.0`) can be used for build increments but we keep it at `0` for simplicity.

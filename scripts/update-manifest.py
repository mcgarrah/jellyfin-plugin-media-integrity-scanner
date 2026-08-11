#!/usr/bin/env python3
"""Updates a plugin manifest with a new release entry after a build.

Usage: update-manifest.py <tag> <zip-path> [--manifest PATH] [--manifest-version VERSION] [--prerelease]

Run from the repository root, after the release zip has been built (see
.github/workflows/release.yml and release-dev.yml). Normalizes the git tag
(e.g. "v0.2.0") into the 4-part version format Jellyfin's plugin manifest
expects, computes the MD5 checksum of the release archive, derives
targetAbi from the Jellyfin.Controller package reference in the csproj, and
prepends a new version entry to the manifest (replacing any existing entry
for the same version, so re-running for the same tag is idempotent).

--manifest-version exists because Jellyfin manifest version strings must be
a clean 4-part numeric System.Version (no semver "-dev"/"-rc" suffixes,
confirmed via reflection against the real MediaBrowser.Model.Updates.VersionInfo
type) -- the dev-channel workflow's tag (e.g. "v0.1.0-dev.147", for a
human-readable GitHub release/changelog) isn't itself a valid manifest
version, so it computes the real 4-part version separately and passes it
through this flag rather than this script trying to parse a "-dev.N" suffix
out of a tag string.
"""

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CSPROJ_PATH = (
    REPO_ROOT
    / "Jellyfin.Plugin.MediaIntegrityScanner"
    / "Jellyfin.Plugin.MediaIntegrityScanner.csproj"
)
REPO_URL = "https://github.com/mcgarrah/jellyfin-plugin-media-integrity-scanner"


def normalize_version(tag: str) -> str:
    """Converts a git tag like 'v0.2.0' or 'v0.2.0.0' into a 4-part version string."""
    parts = tag.lstrip("vV").split(".")
    parts = (parts + ["0"] * 4)[:4]
    return ".".join(parts)


def read_target_abi() -> str:
    """Derives the plugin's targetAbi from the Jellyfin.Controller package reference."""
    text = CSPROJ_PATH.read_text(encoding="utf-8")
    match = re.search(r'Jellyfin\.Controller"\s+Version="([\d.]+)\*?"', text)
    if not match:
        raise RuntimeError(
            "Could not determine targetAbi from csproj Jellyfin.Controller reference"
        )
    base = match.group(1).rstrip(".")
    parts = (base.split(".") + ["0"] * 4)[:4]
    return ".".join(parts)


def compute_checksum(zip_path: Path) -> str:
    """Computes the MD5 checksum of the release archive (Jellyfin manifest convention)."""
    md5 = hashlib.md5()
    with zip_path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            md5.update(chunk)
    return md5.hexdigest()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tag", help="Git tag / GitHub release tag (e.g. v0.2.0 or v0.1.0-dev.147)")
    parser.add_argument("zip_path", help="Path to the built release archive")
    parser.add_argument(
        "--manifest",
        default=str(REPO_ROOT / "manifest.json"),
        help="Manifest file to update (default: manifest.json)",
    )
    parser.add_argument(
        "--manifest-version",
        default=None,
        help="Explicit 4-part manifest version, overriding normalize_version(tag). "
        "Required for tags with a non-numeric suffix (e.g. -dev.N).",
    )
    parser.add_argument(
        "--prerelease",
        action="store_true",
        help="Label the changelog entry as an automated pre-release build.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    zip_path = Path(args.zip_path)
    if not zip_path.is_file():
        raise SystemExit(f"Release archive not found: {zip_path}")

    manifest_path = Path(args.manifest)
    version = args.manifest_version or normalize_version(args.tag)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    plugin = manifest[0]

    release_url = f"{REPO_URL}/releases/tag/{args.tag}"
    changelog = (
        f"Automated pre-release build {args.tag}. **Not guaranteed stable** -- "
        f"see [release notes]({release_url})."
        if args.prerelease
        else f"Automated release {args.tag}. See [release notes]({release_url}) for details."
    )

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": read_target_abi(),
        "sourceUrl": f"{REPO_URL}/releases/download/{args.tag}/{zip_path.name}",
        "checksum": compute_checksum(zip_path),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    versions = [v for v in plugin.get("versions", []) if v.get("version") != version]
    versions.insert(0, entry)
    plugin["versions"] = versions

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"{manifest_path.name} updated with version {version}")


if __name__ == "__main__":
    main()

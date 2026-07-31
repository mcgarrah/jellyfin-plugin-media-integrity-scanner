#!/usr/bin/env python3
"""Updates manifest.json with a new plugin release entry after a tagged build.

Usage: update-manifest.py <tag> <zip-path>

Run from the repository root, after the release zip has been built (see
.github/workflows/release.yml). Normalizes the git tag (e.g. "v0.2.0") into
the 4-part version format Jellyfin's plugin manifest expects, computes the
MD5 checksum of the release archive, derives targetAbi from the
Jellyfin.Controller package reference in the csproj, and prepends a new
version entry to manifest.json (replacing any existing entry for the same
version, so re-running for the same tag is idempotent).
"""

import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = REPO_ROOT / "manifest.json"
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


def main() -> None:
    if len(sys.argv) != 3:
        print("Usage: update-manifest.py <tag> <zip-path>", file=sys.stderr)
        sys.exit(1)

    tag, zip_arg = sys.argv[1], sys.argv[2]
    zip_path = Path(zip_arg)
    if not zip_path.is_file():
        print(f"Release archive not found: {zip_path}", file=sys.stderr)
        sys.exit(1)

    version = normalize_version(tag)
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    plugin = manifest[0]

    entry = {
        "version": version,
        "changelog": f"Automated release {tag}. See GitHub release notes for details.",
        "targetAbi": read_target_abi(),
        "sourceUrl": f"{REPO_URL}/releases/download/{tag}/{zip_path.name}",
        "checksum": compute_checksum(zip_path),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    versions = [v for v in plugin.get("versions", []) if v.get("version") != version]
    versions.insert(0, entry)
    plugin["versions"] = versions

    MANIFEST_PATH.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"manifest.json updated with version {version}")


if __name__ == "__main__":
    main()

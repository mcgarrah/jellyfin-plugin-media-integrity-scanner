#!/usr/bin/env python3
"""Updates a plugin manifest with a new release entry after a build.

Usage: update-manifest.py <tag> <zip-path> --target-framework net9.0|net10.0
       [--manifest PATH] [--manifest-version VERSION] [--prerelease]

Run from the repository root, after the release zip has been built (see
.github/workflows/release.yml and release-dev.yml). Normalizes the git tag
(e.g. "v0.2.0") into the 4-part version format Jellyfin's plugin manifest
expects, computes the MD5 checksum of the release archive, derives
targetAbi from the Jellyfin.Controller package reference for the given
--target-framework's conditional ItemGroup in the csproj, and prepends a
new version entry to the manifest.

Since the csproj multi-targets net9.0/net10.0 with two separate conditional
Jellyfin.Controller references (one per Jellyfin major version), the release
workflow calls this script once per framework/zip for the same tag -- both
entries share the same "version" string but have different "targetAbi"
values, which is expected and matches how the official Jellyfin-repo plugins
publish multi-version support. Dedup is keyed on (version, targetAbi)
together, not version alone, specifically so the second call doesn't clobber
the first call's entry for the same tag.

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


def read_target_abi(target_framework: str) -> str:
    """Derives targetAbi from the Jellyfin.Controller reference for one TFM's conditional ItemGroup.

    The csproj multi-targets net9.0/net10.0, each with its own
    Condition="'$(TargetFramework)' == '...'" ItemGroup carrying a different
    Jellyfin.Controller major version -- a plain whole-file regex would just
    match whichever one appears first, silently picking the wrong ABI half
    the time. Scopes the search to the specific ItemGroup block instead.
    """
    text = CSPROJ_PATH.read_text(encoding="utf-8")
    block_pattern = (
        r"<ItemGroup\s+Condition=\"'\$\(TargetFramework\)'\s*==\s*'"
        + re.escape(target_framework)
        + r"'\">(.*?)</ItemGroup>"
    )
    block_match = re.search(block_pattern, text, re.DOTALL)
    if not block_match:
        raise RuntimeError(
            f"Could not find a conditional ItemGroup for TargetFramework '{target_framework}' in csproj"
        )
    match = re.search(r'Jellyfin\.Controller"\s+Version="([\d.]+)\*?"', block_match.group(1))
    if not match:
        raise RuntimeError(
            f"Could not determine targetAbi from csproj Jellyfin.Controller reference for '{target_framework}'"
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
        "--target-framework",
        required=True,
        choices=["net9.0", "net10.0"],
        help="Which multi-targeted build this zip is for -- selects which conditional "
        "ItemGroup's Jellyfin.Controller reference to read the targetAbi from.",
    )
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
    parser.add_argument(
        "--max-versions-per-abi",
        type=int,
        default=None,
        help="If set, prune each targetAbi group down to its newest N entries after "
        "inserting the new one (CODE-REVIEW-ARCHITECTURE.md L3). Intended for the "
        "dev-channel manifest only -- the stable manifest's older entries are a "
        "documented, intentional downgrade path (see RELEASE.md) and must not be "
        "pruned, so this defaults to off (no --manifest default passes it).",
    )
    return parser.parse_args()


def prune_versions_per_abi(versions: list, max_per_abi: int) -> list:
    """Keeps only the newest max_per_abi entries within each targetAbi group.

    Entries are inserted at index 0 (see main()), so within any single
    targetAbi group, list order is already newest-first -- a simple
    per-group counter while walking the list preserves that and needs no
    separate sort/timestamp comparison.
    """
    counts: dict = {}
    pruned = []
    for entry in versions:
        abi = entry.get("targetAbi")
        seen = counts.get(abi, 0)
        if seen < max_per_abi:
            pruned.append(entry)
        counts[abi] = seen + 1
    return pruned


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

    target_abi = read_target_abi(args.target_framework)

    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": f"{REPO_URL}/releases/download/{args.tag}/{zip_path.name}",
        "checksum": compute_checksum(zip_path),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    # Keyed on (version, targetAbi) together, not version alone: one tag now
    # produces two entries (net9.0/10.11 and net10.0/12.0) that legitimately
    # share the same version string but differ by targetAbi. Deduping on
    # version alone would make the second call silently overwrite the first.
    versions = [
        v
        for v in plugin.get("versions", [])
        if not (v.get("version") == version and v.get("targetAbi") == target_abi)
    ]
    versions.insert(0, entry)
    if args.max_versions_per_abi is not None:
        versions = prune_versions_per_abi(versions, args.max_versions_per_abi)
    plugin["versions"] = versions

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"{manifest_path.name} updated with version {version} (targetAbi {target_abi})")


if __name__ == "__main__":
    main()

#!/bin/bash
# Fails if any restored NuGet package (direct or transitive) has a known
# vulnerability advisory. `dotnet list package --vulnerable` always exits 0
# regardless of what it finds -- it's a report, not a gate -- so this parses
# its --format json output instead of trusting the process exit code.
#
# Shared by two callers with different purposes (see .github/workflows/):
#   - build.yml: PR-blocking, checks only what a given PR actually changes.
#   - dependency-audit.yml: scheduled, checks whatever is on `main` right now,
#     since a CVE disclosed against an already-merged, already-pinned
#     dependency needs no code change to appear -- nothing would ever trigger
#     the PR-blocking check to notice it on its own.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPORT="$(mktemp)"
trap 'rm -f "$REPORT"' EXIT

cd "$PROJECT_ROOT"
dotnet list package --vulnerable --include-transitive --format json > "$REPORT"

VULN_COUNT=$(jq '[.projects[].frameworks[]? // empty
  | (.topLevelPackages // [])[], (.transitivePackages // [])[]
  | select(.vulnerabilities != null)] | length' "$REPORT")

if [ "$VULN_COUNT" -gt 0 ]; then
  echo "Found $VULN_COUNT vulnerable package reference(s):"
  jq -r '.projects[].frameworks[]? // empty
    | (.topLevelPackages // [])[], (.transitivePackages // [])[]
    | select(.vulnerabilities != null)
    | . as $pkg
    | $pkg.vulnerabilities[]
    | "  \($pkg.id) \($pkg.resolvedVersion): \(.severity) - \(.advisoryurl)"' "$REPORT" | sort -u
  exit 1
fi

echo "No vulnerable packages found."

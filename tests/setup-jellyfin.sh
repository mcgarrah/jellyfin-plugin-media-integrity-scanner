#!/bin/bash
# Shared setup for anything that needs a fully-configured, running Jellyfin
# instance with this plugin installed and the test media library already
# populated -- both run-integration-tests.sh (source this) and the Playwright
# suite's global setup (execute this as a subprocess) need the exact same
# sequence: bring the container up, complete the startup wizard, authenticate,
# and create the test-media library. Splitting it out means that sequence is
# maintained in one place instead of drifting between a bash caller and a
# Node one.
#
# Intended to be sourced by another bash script (`source setup-jellyfin.sh`)
# so the caller inherits $TOKEN, $JELLYFIN_URL, $PLUGIN_GUID, and the
# pass/fail/info helpers -- but works fine executed directly too (e.g. from
# Playwright's global setup, which only needs the side effects: it logs in
# through the real web form itself rather than reusing $TOKEN).
#
# Prerequisites:
#   - Plugin built and placed in ./publish (build via CI or cross-compile)
#   - Docker Compose running: docker compose -f tests/docker-compose.integration.yml up -d

set -euo pipefail

JELLYFIN_URL="${JELLYFIN_URL:-http://localhost:8096}"
PLUGIN_GUID="c8f4a3b21d5e4f6a9b7c2e8d0f1a3b5c"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

pass() { echo -e "${GREEN}✓ PASS:${NC} $1"; }
fail() { echo -e "${RED}✗ FAIL:${NC} $1"; exit 1; }
info() { echo -e "${YELLOW}→${NC} $1"; }

# --- Setup ---

info "Setting up integration test environment..."

mkdir -p "$SCRIPT_DIR/jellyfin-config/plugins/MediaIntegrityScanner"
mkdir -p "$SCRIPT_DIR/jellyfin-cache"
mkdir -p "$SCRIPT_DIR/test-media"

# Copy plugin DLLs (must be pre-built — dotnet is not available in WSL)
if [ ! -d "$PROJECT_ROOT/publish" ]; then
    fail "Plugin not built. The ./publish directory does not exist.
    Build via CI or on the LXC build environment, then copy artifacts to ./publish/"
fi

cp "$PROJECT_ROOT/publish/Jellyfin.Plugin.MediaIntegrityScanner.dll" \
   "$SCRIPT_DIR/jellyfin-config/plugins/MediaIntegrityScanner/"
cp "$PROJECT_ROOT/publish/Microsoft.Data.Sqlite.dll" \
   "$SCRIPT_DIR/jellyfin-config/plugins/MediaIntegrityScanner/" 2>/dev/null || true
cp "$PROJECT_ROOT"/publish/SQLitePCLRaw.*.dll \
   "$SCRIPT_DIR/jellyfin-config/plugins/MediaIntegrityScanner/" 2>/dev/null || true

# Create the good/bad test media matrix if not present
if [ ! -f "$SCRIPT_DIR/test-media/good-header.mp4" ]; then
    info "Generating test media matrix (good + corrupted variants)..."
    bash "$SCRIPT_DIR/generate-test-media.sh" "$SCRIPT_DIR/test-media"
fi

# --- Wait for Jellyfin health check ---

info "Waiting for Jellyfin to start..."
for i in $(seq 1 60); do
    if curl -sf "$JELLYFIN_URL/health" > /dev/null 2>&1; then
        pass "Jellyfin health check passed (${i}s)"
        break
    fi
    if [ "$i" -eq 60 ]; then
        fail "Jellyfin failed to start within 60 seconds"
    fi
    sleep 1
done

# --- Wait for Startup Wizard API readiness ---

info "Waiting for startup wizard API to become available..."
for i in $(seq 1 60); do
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "$JELLYFIN_URL/Startup/Configuration" || true)
    if [ "$HTTP_CODE" = "200" ]; then
        pass "Startup wizard API ready (${i}s after health check)"
        break
    fi
    if [ "$i" -eq 60 ]; then
        fail "Startup wizard API not available after 60 seconds (last HTTP code: $HTTP_CODE)"
    fi
    sleep 1
done

# --- Complete Startup Wizard ---

info "Completing startup wizard..."

HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/Configuration" \
    -H "Content-Type: application/json" \
    -d '{
        "UICulture": "en-US",
        "MetadataCountryCode": "US",
        "PreferredMetadataLanguage": "en"
    }' || true)
if [ "$HTTP_CODE" -ge 400 ]; then
    fail "Startup/Configuration failed with HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi
info "  Configuration: HTTP $HTTP_CODE"

# GET /Startup/User triggers user initialization in 10.11+
info "  Initializing default user..."
HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "$JELLYFIN_URL/Startup/User" || true)
if [ "$HTTP_CODE" != "200" ]; then
    info "  WARNING: GET /Startup/User returned HTTP $HTTP_CODE"
fi

info "  Setting admin username and password..."
HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/User" \
    -H "Content-Type: application/json" \
    -d '{
        "Name": "testadmin",
        "Password": "testpassword123"
    }' || true)
if [ "$HTTP_CODE" -ge 400 ]; then
    fail "Startup/User failed with HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi
info "  User creation: HTTP $HTTP_CODE"

# Required by the 10.11 wizard; endpoint may not exist on all versions, so
# only a 5xx is treated as fatal.
HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/RemoteAccess" \
    -H "Content-Type: application/json" \
    -d '{
        "EnableRemoteAccess": true,
        "EnableAutomaticPortMapping": false
    }' || true)
info "  Remote access: HTTP $HTTP_CODE"
if [ "$HTTP_CODE" -ge 500 ]; then
    fail "Startup/RemoteAccess server error HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi

HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/Complete" || true)
if [ "$HTTP_CODE" -ge 400 ]; then
    fail "Startup/Complete failed with HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi

pass "Startup wizard completed"

# Give Jellyfin a moment to reconfigure after wizard completion
sleep 3

# --- Authenticate ---

info "Authenticating..."

AUTH_RESPONSE=$(curl -s -X POST "$JELLYFIN_URL/Users/AuthenticateByName" \
    -H "Content-Type: application/json" \
    -H "X-Emby-Authorization: MediaBrowser Client=\"Integration Test\", Device=\"Local\", DeviceId=\"local-test\", Version=\"1.0.0\"" \
    -d '{
        "Username": "testadmin",
        "Pw": "testpassword123"
    }')

TOKEN=$(echo "$AUTH_RESPONSE" | jq -r '.AccessToken')

if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
    fail "Failed to authenticate (no token received). Response: $AUTH_RESPONSE"
fi

pass "Authenticated successfully"

# --- Create the test media library ---

info "Creating test media library..."

curl -sf -X POST "$JELLYFIN_URL/Library/VirtualFolders?name=TestMovies&collectionType=movies&refreshLibrary=true" \
    -H "X-Emby-Token: $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{
        "LibraryOptions": {
            "PathInfos": [{"Path": "/media"}],
            "EnableRealtimeMonitor": false
        }
    }' > /dev/null

# Poll for all 7 test-media items (not just "any"): the matrix includes
# deliberately unreadable files (bad-empty, bad-garbage) whose own metadata
# probing takes Jellyfin's indexer longer than a single well-formed file --
# breaking on the first item visible leaves the library only partially
# indexed for callers that assert exact pass/fail counts, since the plugin's
# own file count depends on Jellyfin having finished classifying every item
# as Video/Audio media first.
EXPECTED_MEDIA_COUNT=7
for i in $(seq 1 60); do
    ITEMS=$(curl -sf "$JELLYFIN_URL/Items?Recursive=true" -H "X-Emby-Token: $TOKEN")
    ITEM_COUNT=$(echo "$ITEMS" | jq '[.Items[] | select(.MediaType == "Video" or .MediaType == "Audio")] | length')
    if [ "$ITEM_COUNT" -ge "$EXPECTED_MEDIA_COUNT" ]; then
        pass "Media library created with $ITEM_COUNT media item(s) (after ${i}s)"
        break
    fi
    if [ "$i" -eq 60 ]; then
        fail "Expected $EXPECTED_MEDIA_COUNT media items after 60s, only found $ITEM_COUNT"
    fi
    sleep 1
done

# Adding the library triggers Jellyfin's own scan, which fires ItemAdded
# events. Since ScanOnItemAdded defaults to true, the plugin's LibraryMonitor
# kicks off its own fire-and-forget header scan of each new item. Wait for
# that background scan to settle so callers see a stable, fully-scanned
# state (and so a manually-triggered scan right after this doesn't race into
# a 409). 7 files at the default DelayBetweenFilesMs (5000ms) and
# MaxConcurrentScans (1) can take longer than a single file to fully drain.
info "Waiting for automatic scan-on-add to settle..."
for i in $(seq 1 60); do
    AUTO_STATUS=$(curl -sf "$JELLYFIN_URL/MediaIntegrity/Status" -H "X-Emby-Token: $TOKEN")
    AUTO_SCANNING=$(echo "$AUTO_STATUS" | jq -r '.IsScanning')
    if [ "$AUTO_SCANNING" = "false" ]; then
        pass "No automatic scan in progress (after ${i}s)"
        break
    fi
    if [ "$i" -eq 60 ]; then
        info "Automatic scan-on-add still in progress after 60s, proceeding anyway"
    fi
    sleep 1
done

pass "Jellyfin is up, wizard complete, plugin installed, test library populated"

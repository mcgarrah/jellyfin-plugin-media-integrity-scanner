#!/bin/bash
# Integration test runner for local development
# Prerequisites:
#   - Plugin built and placed in ./publish (build via CI or cross-compile)
#   - Docker Compose running: docker compose -f tests/docker-compose.integration.yml up -d
#
# This script mirrors the GitHub Actions integration-test.yml workflow steps.

set -euo pipefail

JELLYFIN_URL="http://localhost:8096"
PLUGIN_GUID="c8f4a3b21d5e4f6a9b7c2e8d0f1a3b5c"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

pass() { echo -e "${GREEN}✓ PASS:${NC} $1"; }
fail() { echo -e "${RED}✗ FAIL:${NC} $1"; exit 1; }
info() { echo -e "${YELLOW}→${NC} $1"; }

# --- Setup ---

info "Setting up integration test environment..."

# Create directories
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

# Create test media if not present
if [ ! -f "$SCRIPT_DIR/test-media/test-video.mp4" ]; then
    info "Creating test media file..."
    ffmpeg -f lavfi -i testsrc=duration=5:size=320x240:rate=25 \
           -f lavfi -i sine=frequency=440:duration=5 \
           -c:v libx264 -c:a aac -shortest \
           "$SCRIPT_DIR/test-media/test-video.mp4" -y 2>/dev/null
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

# Step 1: Set startup configuration
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

# Step 2: Initialize and update admin user
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

# Step 3: Set remote access (required by 10.11 wizard)
HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/RemoteAccess" \
    -H "Content-Type: application/json" \
    -d '{
        "EnableRemoteAccess": true,
        "EnableAutomaticPortMapping": false
    }' || true)
info "  Remote access: HTTP $HTTP_CODE"
# Don't fail on 4xx — endpoint may not exist on all versions
if [ "$HTTP_CODE" -ge 500 ]; then
    fail "Startup/RemoteAccess server error HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi

# Step 4: Complete the wizard
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

# --- Test: Plugin Loaded ---

info "Checking plugin is loaded..."

PLUGINS=$(curl -sf "$JELLYFIN_URL/Plugins" -H "X-Emby-Token: $TOKEN")
PLUGIN_FOUND=$(echo "$PLUGINS" | jq "[.[] | select(.Id == \"$PLUGIN_GUID\")] | length")

if [ "$PLUGIN_FOUND" -eq 0 ]; then
    echo "Loaded plugins:"
    echo "$PLUGINS" | jq '.[].Name'
    fail "Media Integrity Scanner plugin not found"
fi

pass "Media Integrity Scanner plugin is loaded"
echo "$PLUGINS" | jq ".[] | select(.Id == \"$PLUGIN_GUID\") | {Name, Version, Status}"

# --- Test: Plugin Configuration ---

info "Checking plugin configuration endpoint..."

CONFIG=$(curl -sf "$JELLYFIN_URL/Plugins/$PLUGIN_GUID/Configuration" \
    -H "X-Emby-Token: $TOKEN" 2>/dev/null) || CONFIG=""

if [ -n "$CONFIG" ]; then
    pass "Plugin configuration endpoint is accessible"
    echo "$CONFIG" | jq '{MaxConcurrentScans, DelayBetweenFilesMs, PauseDuringPlayback, EnableDeepScan}'
else
    info "Plugin configuration endpoint not available (may be expected for scaffold)"
fi

# --- Test: FFmpeg Available ---

info "Checking FFmpeg in container..."

CONTAINER_NAME="jellyfin-integration-test"
# jellyfin-ffmpeg is not symlinked onto PATH in the jellyfin/jellyfin image,
# so a bare `ffmpeg`/`ffprobe` exec always fails — check the real path instead.
if docker exec "$CONTAINER_NAME" /usr/lib/jellyfin-ffmpeg/ffmpeg -version > /dev/null 2>&1 \
    && docker exec "$CONTAINER_NAME" /usr/lib/jellyfin-ffmpeg/ffprobe -version > /dev/null 2>&1; then
    pass "FFmpeg and ffprobe are available in container"
else
    fail "FFmpeg/ffprobe not found at /usr/lib/jellyfin-ffmpeg/ in Jellyfin container"
fi

# --- Test: Plugin Settings Page Configuration Round-Trip ---

info "Verifying settings page configuration round-trip..."

ORIGINAL_CONFIG=$(curl -sf "$JELLYFIN_URL/Plugins/$PLUGIN_GUID/Configuration" -H "X-Emby-Token: $TOKEN")

UPDATED_CONFIG='{
    "MaxConcurrentScans": 3,
    "DelayBetweenFilesMs": 1234,
    "PauseDuringPlayback": false,
    "EnableDeepScan": true,
    "MaxReadRateMbPerSec": 7,
    "UseQuietHoursOnly": true,
    "QuietHoursStart": "23:00",
    "QuietHoursEnd": "05:30",
    "FfmpegPathOverride": "/custom/ffmpeg",
    "FfprobePathOverride": "/custom/ffprobe",
    "ScanOnItemAdded": false,
    "PurgeOnItemRemoved": false
}'

HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$JELLYFIN_URL/Plugins/$PLUGIN_GUID/Configuration" \
    -H "X-Emby-Token: $TOKEN" \
    -H "Content-Type: application/json" \
    -d "$UPDATED_CONFIG")
if [ "$HTTP_CODE" -ge 300 ]; then
    fail "Settings save (POST Configuration) failed with HTTP $HTTP_CODE"
fi

SAVED_CONFIG=$(curl -sf "$JELLYFIN_URL/Plugins/$PLUGIN_GUID/Configuration" -H "X-Emby-Token: $TOKEN")
SAVED_MAX_CONCURRENT=$(echo "$SAVED_CONFIG" | jq '.MaxConcurrentScans')
SAVED_QUIET_START=$(echo "$SAVED_CONFIG" | jq -r '.QuietHoursStart')
SAVED_FFMPEG_OVERRIDE=$(echo "$SAVED_CONFIG" | jq -r '.FfmpegPathOverride')

if [ "$SAVED_MAX_CONCURRENT" = "3" ] && [ "$SAVED_QUIET_START" = "23:00" ] && [ "$SAVED_FFMPEG_OVERRIDE" = "/custom/ffmpeg" ]; then
    pass "Settings page configuration saved and persisted correctly (this is the exact request the Save button issues)"
else
    fail "Saved configuration did not round-trip correctly: $SAVED_CONFIG"
fi

# Restore the original configuration so this script is safe to re-run locally
HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$JELLYFIN_URL/Plugins/$PLUGIN_GUID/Configuration" \
    -H "X-Emby-Token: $TOKEN" \
    -H "Content-Type: application/json" \
    -d "$ORIGINAL_CONFIG")
if [ "$HTTP_CODE" -ge 300 ]; then
    fail "Failed to restore original configuration after round-trip test (HTTP $HTTP_CODE)"
fi
pass "Original configuration restored"

# --- Test: Plugin Web Pages Served ---

info "Checking plugin web pages are served..."

DASHBOARD_CODE=$(curl -s -o /tmp/dashboard_page.html -w "%{http_code}" \
    "$JELLYFIN_URL/web/configurationpage?name=Media+Integrity+Scanner" -H "X-Emby-Token: $TOKEN")
SETTINGS_CODE=$(curl -s -o /tmp/settings_page.html -w "%{http_code}" \
    "$JELLYFIN_URL/web/configurationpage?name=Media+Integrity+Scanner+Settings" -H "X-Emby-Token: $TOKEN")

if [ "$DASHBOARD_CODE" = "200" ] && grep -q "getPluginConfiguration\|MediaIntegrity" /tmp/dashboard_page.html 2>/dev/null; then
    pass "Dashboard page served (HTTP $DASHBOARD_CODE)"
else
    fail "Dashboard page not served correctly (HTTP $DASHBOARD_CODE)"
fi

if [ "$SETTINGS_CODE" = "200" ] && grep -q "getPluginConfiguration" /tmp/settings_page.html 2>/dev/null; then
    pass "Settings page served (HTTP $SETTINGS_CODE)"
else
    fail "Settings page not served correctly (HTTP $SETTINGS_CODE)"
fi
rm -f /tmp/dashboard_page.html /tmp/settings_page.html

# --- Test: Add Library ---

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

# Poll for library items instead of fixed sleep
for i in $(seq 1 30); do
    ITEMS=$(curl -sf "$JELLYFIN_URL/Items?Recursive=true" -H "X-Emby-Token: $TOKEN")
    ITEM_COUNT=$(echo "$ITEMS" | jq '.TotalRecordCount')
    if [ "$ITEM_COUNT" -gt 0 ]; then
        pass "Media library created with $ITEM_COUNT item(s) (after ${i}s)"
        break
    fi
    if [ "$i" -eq 30 ]; then
        info "Library created but no items detected after 30s (metadata fetch may be slow)"
    fi
    sleep 1
done

# --- Test: Full Scan-and-Verify Flow ---

info "Triggering a header scan of the library..."

HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$JELLYFIN_URL/MediaIntegrity/Scan" \
    -H "X-Emby-Token: $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"deepScan": false}')
if [ "$HTTP_CODE" != "202" ]; then
    fail "Triggering scan failed with HTTP $HTTP_CODE (expected 202 Accepted)"
fi
pass "Scan triggered (HTTP 202)"

info "Waiting for scan to complete..."
for i in $(seq 1 60); do
    STATUS=$(curl -sf "$JELLYFIN_URL/MediaIntegrity/Status" -H "X-Emby-Token: $TOKEN")
    # Note: Jellyfin's host serializes controller responses in PascalCase
    # (not the ASP.NET Core camelCase default) -- confirmed by hand while
    # writing this test, which is also why the dashboard's JS needed the
    # same fix (see CODE_REVIEW.md).
    IS_SCANNING=$(echo "$STATUS" | jq -r '.IsScanning')
    if [ "$IS_SCANNING" = "false" ]; then
        pass "Scan completed (after ${i}s)"
        break
    fi
    if [ "$i" -eq 60 ]; then
        fail "Scan did not complete within 60 seconds. Last status: $STATUS"
    fi
    sleep 1
done

info "Verifying scan results..."

FINAL_STATUS=$(curl -sf "$JELLYFIN_URL/MediaIntegrity/Status" -H "X-Emby-Token: $TOKEN")
SCANNED_FILES=$(echo "$FINAL_STATUS" | jq '.ScannedFiles')
PASSED_FILES=$(echo "$FINAL_STATUS" | jq '.PassedFiles')

if [ "$SCANNED_FILES" -lt 1 ]; then
    fail "Expected at least 1 scanned file, got $SCANNED_FILES. Status: $FINAL_STATUS"
fi
pass "Library health reflects the scan: $SCANNED_FILES scanned, $PASSED_FILES passed"

RESULTS=$(curl -sf "$JELLYFIN_URL/MediaIntegrity/Results?status=1" -H "X-Emby-Token: $TOKEN")
RESULT_COUNT=$(echo "$RESULTS" | jq '.TotalCount')

if [ "$RESULT_COUNT" -lt 1 ]; then
    fail "Expected at least 1 passed scan result via GET /MediaIntegrity/Results?status=1, got $RESULT_COUNT"
fi
pass "GET /MediaIntegrity/Results?status=1 returns $RESULT_COUNT passed result(s)"
echo "$RESULTS" | jq '.Items[0] | {FilePath, ScanStatus, ScanPhase, ScanDurationMs}'

# --- Summary ---

echo ""
echo -e "${GREEN}═══════════════════════════════════════${NC}"
echo -e "${GREEN}  All integration tests passed!${NC}"
echo -e "${GREEN}═══════════════════════════════════════${NC}"

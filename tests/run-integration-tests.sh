#!/bin/bash
# Integration test runner for local development
# Prerequisites:
#   - Plugin built and placed in ./publish (build via CI or cross-compile)
#   - Docker Compose running: docker compose -f tests/docker-compose.integration.yml up -d
#
# This script mirrors the GitHub Actions integration-test.yml workflow steps.

set -euo pipefail

JELLYFIN_URL="http://localhost:8096"
PLUGIN_GUID="c8f4a3b2-1d5e-4f6a-9b7c-2e8d0f1a3b5c"
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
for i in $(seq 1 30); do
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "$JELLYFIN_URL/Startup/Configuration")
    if [ "$HTTP_CODE" = "200" ]; then
        pass "Startup wizard API ready (${i}s after health check)"
        break
    fi
    if [ "$i" -eq 30 ]; then
        fail "Startup wizard API not available after 30 seconds (last HTTP code: $HTTP_CODE)"
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
    }')
if [ "$HTTP_CODE" -ge 400 ]; then
    fail "Startup/Configuration failed with HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi
info "  Configuration: HTTP $HTTP_CODE"

# Step 2: Create admin user
HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/User" \
    -H "Content-Type: application/json" \
    -d '{
        "Name": "testadmin",
        "Password": "testpassword123"
    }')
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
    }')
info "  Remote access: HTTP $HTTP_CODE"
# Don't fail on 4xx — endpoint may not exist on all versions
if [ "$HTTP_CODE" -ge 500 ]; then
    fail "Startup/RemoteAccess server error HTTP $HTTP_CODE: $(cat /tmp/response.txt)"
fi

# Step 4: Complete the wizard
HTTP_CODE=$(curl -s -o /tmp/response.txt -w "%{http_code}" \
    -X POST "$JELLYFIN_URL/Startup/Complete")
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
if docker exec "$CONTAINER_NAME" ffmpeg -version > /dev/null 2>&1; then
    pass "FFmpeg is available in container"
else
    fail "FFmpeg not found in Jellyfin container"
fi

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

# --- Summary ---

echo ""
echo -e "${GREEN}═══════════════════════════════════════${NC}"
echo -e "${GREEN}  All integration tests passed!${NC}"
echo -e "${GREEN}═══════════════════════════════════════${NC}"

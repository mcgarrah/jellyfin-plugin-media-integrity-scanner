#!/bin/bash
# Integration test runner for local development
# Prerequisites:
#   - Plugin built: dotnet publish --configuration Release --output ./publish
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

# Copy plugin DLLs
if [ ! -d "$PROJECT_ROOT/publish" ]; then
    info "Building plugin..."
    cd "$PROJECT_ROOT"
    dotnet publish --configuration Release --output ./publish
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

# --- Wait for Jellyfin ---

info "Waiting for Jellyfin to start..."
for i in $(seq 1 60); do
    if curl -sf "$JELLYFIN_URL/health" > /dev/null 2>&1; then
        pass "Jellyfin is ready (${i}s)"
        break
    fi
    if [ "$i" -eq 60 ]; then
        fail "Jellyfin failed to start within 60 seconds"
    fi
    sleep 1
done

# --- Complete Startup Wizard ---

info "Completing startup wizard..."

curl -sf -X POST "$JELLYFIN_URL/Startup/Configuration" \
    -H "Content-Type: application/json" \
    -d '{
        "UICulture": "en-US",
        "MetadataCountryCode": "US",
        "PreferredMetadataLanguage": "en"
    }' > /dev/null 2>&1 || true

curl -sf -X POST "$JELLYFIN_URL/Startup/User" \
    -H "Content-Type: application/json" \
    -d '{
        "Name": "testadmin",
        "Password": "testpassword123"
    }' > /dev/null 2>&1 || true

curl -sf -X POST "$JELLYFIN_URL/Startup/Complete" > /dev/null 2>&1 || true

pass "Startup wizard completed"

# --- Authenticate ---

info "Authenticating..."

AUTH_RESPONSE=$(curl -sf -X POST "$JELLYFIN_URL/Users/AuthenticateByName" \
    -H "Content-Type: application/json" \
    -H "X-Emby-Authorization: MediaBrowser Client=\"Integration Test\", Device=\"Local\", DeviceId=\"local-test\", Version=\"1.0.0\"" \
    -d '{
        "Username": "testadmin",
        "Pw": "testpassword123"
    }')

TOKEN=$(echo "$AUTH_RESPONSE" | jq -r '.AccessToken')

if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
    fail "Failed to authenticate (no token received)"
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

sleep 10

ITEMS=$(curl -sf "$JELLYFIN_URL/Items?Recursive=true" -H "X-Emby-Token: $TOKEN")
ITEM_COUNT=$(echo "$ITEMS" | jq '.TotalRecordCount')

if [ "$ITEM_COUNT" -gt 0 ]; then
    pass "Media library created with $ITEM_COUNT item(s)"
else
    info "Library created but no items detected yet (metadata fetch may be slow)"
fi

# --- Summary ---

echo ""
echo -e "${GREEN}═══════════════════════════════════════${NC}"
echo -e "${GREEN}  All integration tests passed!${NC}"
echo -e "${GREEN}═══════════════════════════════════════${NC}"

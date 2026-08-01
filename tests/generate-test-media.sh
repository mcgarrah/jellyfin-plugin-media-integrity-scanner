#!/bin/bash
# Generates a matrix of good and deliberately-corrupted video files for the
# integration suite, so it can assert the scanner actually detects
# corruption -- not just that it runs against a single always-valid clip.
#
# Every "bad" file's expected Header (ffprobe) / FullDecode (ffmpeg) outcome
# below was verified for real against the jellyfin-ffmpeg binaries bundled in
# the jellyfin/jellyfin:10.11.11 image (the same one the integration tests
# use), not assumed -- the plugin's success criterion is strict (exit code 0
# AND empty stderr, see FfmpegWrapper.cs), so a technique that merely
# produces a nonzero exit code isn't enough to reason about; verify any new
# technique added here the same way.
#
#   File                       Header (ffprobe)   FullDecode (ffmpeg)
#   good-header.mp4            Pass               Pass
#   good-alt-codec.mkv         Pass               Pass
#   bad-empty.mp4              Fail (moov not found)   Fail
#   bad-garbage.mp4            Fail (moov not found)   Fail
#   bad-header-corrupt.mp4     Fail (moov not found)   Fail
#   bad-truncated.mp4          Pass               Fail (partial NAL units, non-empty stderr)
#   bad-middecode.mp4          Pass               Fail (corrupt NAL units, non-empty stderr)
#
# bad-truncated and bad-middecode are the pair that actually proves the
# plugin's two-phase design: ffprobe only reads structural metadata (which
# survives both a simulated interrupted copy/download and mid-file bit rot,
# since -movflags +faststart keeps moov at the front), while ffmpeg's full
# decode hits the missing/corrupted frame data and reports it -- with exit 0,
# which is why the plugin's success check requires empty stderr too, not
# just a zero exit code.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${1:-$SCRIPT_DIR/test-media}"

mkdir -p "$OUT_DIR"

# Resolve ffmpeg the same way FfmpegResolver.cs does: prefer the
# jellyfin-ffmpeg build (not on PATH in the Jellyfin Docker image, but is the
# real binary the plugin itself will use at scan time), then fall back to
# whatever's on PATH for local/non-Jellyfin-container runs.
if [ -x /usr/lib/jellyfin-ffmpeg/ffmpeg ]; then
  FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
else
  FFMPEG=ffmpeg
fi

GOOD_HEADER="$OUT_DIR/good-header.mp4"
GOOD_ALT="$OUT_DIR/good-alt-codec.mkv"

if [ ! -f "$GOOD_HEADER" ]; then
  echo "Generating good-header.mp4..."
  "$FFMPEG" -f lavfi -i testsrc=duration=5:size=320x240:rate=25 \
            -f lavfi -i sine=frequency=440:duration=5 \
            -c:v libx264 -c:a aac -shortest -movflags +faststart \
            "$GOOD_HEADER" -y 2>/dev/null
fi

if [ ! -f "$GOOD_ALT" ]; then
  echo "Generating good-alt-codec.mkv..."
  "$FFMPEG" -f lavfi -i testsrc=duration=5:size=320x240:rate=25 \
            -f lavfi -i sine=frequency=440:duration=5 \
            -c:v libx264 -c:a aac -shortest \
            "$GOOD_ALT" -y 2>/dev/null
fi

# bad-empty.mp4 -- a zero-byte file. Fails ffprobe's format detection
# outright ("moov atom not found"): both phases fail identically, exercising
# the plain unreadable-file path rather than the header/decode split.
BAD_EMPTY="$OUT_DIR/bad-empty.mp4"
if [ ! -f "$BAD_EMPTY" ]; then
  echo "Generating bad-empty.mp4..."
  : > "$BAD_EMPTY"
fi

# bad-garbage.mp4 -- random bytes with a .mp4 extension. Same failure mode as
# bad-empty (no valid container structure at all for ffprobe to find).
BAD_GARBAGE="$OUT_DIR/bad-garbage.mp4"
if [ ! -f "$BAD_GARBAGE" ]; then
  echo "Generating bad-garbage.mp4..."
  head -c 65536 /dev/urandom > "$BAD_GARBAGE"
fi

# bad-header-corrupt.mp4 -- a valid file with its first 2KB zeroed, which
# destroys ftyp/moov (moov sits right after ftyp thanks to +faststart on the
# source). Fails ffprobe the same way as the two files above.
BAD_HEADER_CORRUPT="$OUT_DIR/bad-header-corrupt.mp4"
if [ ! -f "$BAD_HEADER_CORRUPT" ]; then
  echo "Generating bad-header-corrupt.mp4..."
  cp "$GOOD_HEADER" "$BAD_HEADER_CORRUPT"
  dd if=/dev/zero of="$BAD_HEADER_CORRUPT" bs=1024 count=2 conv=notrunc 2>/dev/null
fi

# bad-truncated.mp4 -- a valid file cut to 50% of its length, simulating an
# interrupted copy/download. moov/ftyp (at the front) survive intact, so
# ffprobe passes cleanly -- but the truncated frame data mid-decode produces
# NAL-unit errors on stderr even though ffmpeg's own exit code is 0.
BAD_TRUNCATED="$OUT_DIR/bad-truncated.mp4"
if [ ! -f "$BAD_TRUNCATED" ]; then
  echo "Generating bad-truncated.mp4..."
  SIZE=$(stat -c%s "$GOOD_HEADER" 2>/dev/null || stat -f%z "$GOOD_HEADER")
  HALF=$((SIZE / 2))
  head -c "$HALF" "$GOOD_HEADER" > "$BAD_TRUNCATED"
fi

# bad-middecode.mp4 -- a valid file with a 4KB range zeroed out starting at
# its midpoint, simulating mid-file bit rot/disk corruption rather than a
# truncated copy. File length and moov/ftyp are untouched, so this lands in
# the same probe-passes/decode-fails bucket as bad-truncated via a distinct,
# equally realistic real-world cause.
BAD_MIDDECODE="$OUT_DIR/bad-middecode.mp4"
if [ ! -f "$BAD_MIDDECODE" ]; then
  echo "Generating bad-middecode.mp4..."
  cp "$GOOD_HEADER" "$BAD_MIDDECODE"
  SIZE=$(stat -c%s "$GOOD_HEADER" 2>/dev/null || stat -f%z "$GOOD_HEADER")
  MID_BLOCK=$((SIZE / 2 / 1024))
  dd if=/dev/zero of="$BAD_MIDDECODE" bs=1024 seek="$MID_BLOCK" count=4 conv=notrunc 2>/dev/null
fi

echo "Test media ready in $OUT_DIR:"
ls -la "$OUT_DIR"

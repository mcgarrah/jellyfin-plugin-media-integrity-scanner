#!/usr/bin/env python3
"""Seeds/removes synthetic scan_results rows directly in the plugin's SQLite
database, for Playwright's pagination.spec.js.

Why this exists: the shared integration-test media matrix is a deliberate,
fixed 7-file set (see generate-test-media.sh) reused by run-integration-
tests.sh, dashboard.spec.js, and settings.spec.js, all of which assert exact
pass/fail counts against it. Real pagination controls never render at all
with only 7 rows -- Web/integrity_dashboard.html's renderPagination() blanks
the whole #pagination div whenever totalPages <= 1, and even the smallest
page-size option (25/page) never produces a second page for 7 rows. Growing
the shared media matrix itself would ripple into every count-based assertion
elsewhere. Seeding synthetic *scan_results* rows directly (not real library
items) sidesteps that entirely: MediaIntegrity/Status's TotalFiles comes from
the real Jellyfin library via ILibraryManager, not from this table, so these
extra rows are invisible to every other spec's stats assertions -- they only
affect Results/GetResultsAsync, which is exactly what pagination.spec.js
needs to exercise.

Usage:
    python3 seed-pagination-rows.py <db_path> seed <count>
    python3 seed-pagination-rows.py <db_path> clear

"seed" always wipes the *entire* scan_results table first (not just prior
seeded rows) before inserting exactly <count> fresh ones, so the resulting
total is deterministic regardless of what earlier specs in the same suite
run left behind (e.g. dashboard.spec.js's Header scan, or database-backup
.spec.js's Header+Deep scan, which together leave up to 2 rows per real
item, not 1) -- hit for real developing this spec, when a hardcoded "+7
real rows" assumption produced a wrong expected total once run after
specs that had already deep-scanned the same 7 items.
"""
import sqlite3
import sys

ITEM_ID_PREFIX = "pw-pagination-seed-"


def seed(db_path, count):
    conn = sqlite3.connect(db_path)
    try:
        conn.execute("DELETE FROM scan_results")
        conn.executemany(
            """
            INSERT INTO scan_results
                (item_id, file_path, file_size, last_modified,
                 scan_phase, scan_status, scan_timestamp,
                 error_output, scan_duration_ms, decode_mode, hardware_accel_type)
            VALUES (?, ?, 1000, '2026-01-01T00:00:00.0000000Z', 1, 1, ?, NULL, 10, 0, NULL)
            """,
            [
                (
                    f"{ITEM_ID_PREFIX}{i:05d}",
                    f"/media/seeded/pagination-test-{i:05d}.mkv",
                    f"2025-01-01T00:{i // 60:02d}:{i % 60:02d}.0000000Z",
                )
                for i in range(count)
            ],
        )
        conn.commit()
    finally:
        conn.close()


def clear(db_path):
    # Wipes the whole table, matching seed()'s own full-reset behavior --
    # deliberately not scoped to just this script's own rows, since seed()
    # already discards whatever was there before it runs. Fine for this
    # spec's own use (pagination.spec.js is the last one in the suite that
    # needs any scan_results data), not a generic partial-cleanup helper.
    conn = sqlite3.connect(db_path)
    try:
        conn.execute("DELETE FROM scan_results")
        conn.commit()
    finally:
        conn.close()


if __name__ == "__main__":
    db_path, action = sys.argv[1], sys.argv[2]
    if action == "seed":
        seed(db_path, int(sys.argv[3]))
    elif action == "clear":
        clear(db_path)
    else:
        raise SystemExit(f"unknown action: {action}")

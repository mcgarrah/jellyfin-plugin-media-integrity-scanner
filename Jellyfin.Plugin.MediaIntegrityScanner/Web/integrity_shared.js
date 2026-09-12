// Jellyfin Media Integrity Scanner - validates media file integrity using FFmpeg
// Copyright (C) 2026  Michael McGarrah <mcgarrah@gmail.com>
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program; if not, see <https://www.gnu.org/licenses/>.

// Shared helpers for integrity_dashboard.html / integrity_settings.html /
// integrity_issues.html (CODE-REVIEW-ARCHITECTURE.md H1). Served through
// Jellyfin's own plugin-page mechanism (registered as a fourth, hidden
// PluginPageInfo in Plugin.cs, GET web/ConfigurationPage?name=...) rather
// than a separate static-file route -- that endpoint already serves
// application/x-javascript for exactly this purpose (see Jellyfin's own
// DashboardController.GetDashboardConfigurationPage). Loaded via a plain
// <script src> tag, not injected dynamically, so these functions are
// guaranteed defined before any later inline <script> block on the page runs.
//
// Scope: only the four helpers that were byte-for-byte identical across all
// three pages with no page-specific coupling. showError/hideError were
// deliberately left page-local -- each targets a different DOM element ID
// (error-msg / issues-error-msg / settings-error-msg), and giving all three
// pages the same id would reintroduce the exact cross-page id collision bug
// PluginTests.GetPages_NoElementIdIsDuplicatedAcrossAnyTwoPages exists to
// catch (Jellyfin keeps every visited plugin page mounted simultaneously in
// one document, so document.getElementById is not page-scoped).

function getApiUrl(path) {
    return ApiClient.getUrl('MediaIntegrity/' + path);
}

function apiFetch(path, options) {
    var url = getApiUrl(path);
    // Deep-merge headers: the default auth header always applies, and a
    // call site's own headers (e.g. Content-Type) layer on top. A single
    // Object.assign over the whole options object is a shallow merge -- any
    // call site passing its own headers used to silently drop the auth
    // header unless it repeated it, which is exactly how the Jellyfin 12
    // auth migration ended up needing the same fix at 14 sites.
    var opts = Object.assign({}, options || {});
    opts.headers = Object.assign(
        { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' },
        opts.headers || {});
    return fetch(url, opts).then(function(resp) {
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        return resp.json();
    });
}

function escapeHtml(str) {
    if (!str) return '';
    return str.replace(/&/g, '&amp;')
              .replace(/</g, '&lt;')
              .replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;');
}

function formatDate(iso) {
    if (!iso) return '—';
    try {
        return new Date(iso).toLocaleString();
    } catch (e) {
        return iso;
    }
}

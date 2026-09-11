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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaIntegrityScanner;

/// <summary>
/// Media Integrity Scanner plugin entry point.
/// Validates media file integrity using FFmpeg to detect corrupt,
/// truncated, and damaged files in your Jellyfin library.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Accessing Configuration here triggers the base class's lazy load
        // from disk, so a hand-edited XML file gets the same sanitization as
        // a settings-page save (see UpdateConfiguration below). Sanitizing
        // the cached instance in place is enough -- every later read returns
        // this same object -- and deliberately does NOT write back to disk,
        // so an out-of-range value in the file stays visible to the admin
        // rather than being silently rewritten.
        Sanitize(Configuration);
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Media Integrity Scanner";

    /// <inheritdoc />
    public override string Description =>
        "Validates media file integrity using FFmpeg. " +
        "Detects corrupt, truncated, and damaged files without impacting playback.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c8f4a3b2-1d5e-4f6a-9b7c-2e8d0f1a3b5c");

    /// <inheritdoc />
    /// <remarks>
    /// The single server-side validation choke point for configuration
    /// (CODE-REVIEW-ARCHITECTURE.md H2). The settings page's own HTML
    /// min/max attributes and JS clamping only guard the normal UI path --
    /// Jellyfin's generic plugin-configuration REST endpoint and a
    /// hand-edited XML file on disk both bypass them entirely, and an
    /// out-of-range value can throw deep inside a scan or at server startup
    /// (the MaxConcurrentScans=0 incident: SemaphoreSlim(0,0) throwing out
    /// of a DI constructor). Scattered Math.Max guards at use sites remain
    /// as belt-and-braces, but this is the one place new settings MUST add
    /// their range.
    /// </remarks>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            Sanitize(config);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Clamps every numeric setting to its documented range (matching the
    /// settings page's own HTML min/max attributes), restores unparseable
    /// quiet-hours times and blank manifest URLs to their defaults, and
    /// drops Radarr/Sonarr server entries with no URL or API key (unusable
    /// -- every client call would fail). Mutates <paramref name="config"/>
    /// in place.
    /// </summary>
    /// <param name="config">The configuration to sanitize.</param>
    internal static void Sanitize(PluginConfiguration config)
    {
        config.MaxConcurrentScans = Math.Clamp(config.MaxConcurrentScans, 1, 16);
        config.DelayBetweenFilesMs = Math.Clamp(config.DelayBetweenFilesMs, 0, 60000);
        config.MaxReadRateMbPerSec = Math.Max(0, config.MaxReadRateMbPerSec);
        config.HistoryLookbackDays = Math.Clamp(config.HistoryLookbackDays, 1, 365);
        config.MaxAutoRemediationsPerDay = Math.Clamp(config.MaxAutoRemediationsPerDay, 1, 1000);
        config.RemediationCooldownHours = Math.Clamp(config.RemediationCooldownHours, 1, 8760);
        config.MaxRemediationCycles = Math.Clamp(config.MaxRemediationCycles, 1, 100);

        // Same lenient parse ScanThrottle itself uses -- sanitization must
        // never reject a value the consumer would have accepted.
        if (!TimeSpan.TryParse(config.QuietHoursStart, CultureInfo.InvariantCulture, out _))
        {
            config.QuietHoursStart = "02:00";
        }

        if (!TimeSpan.TryParse(config.QuietHoursEnd, CultureInfo.InvariantCulture, out _))
        {
            config.QuietHoursEnd = "06:00";
        }

        if (string.IsNullOrWhiteSpace(config.StableManifestUrl))
        {
            config.StableManifestUrl = new PluginConfiguration().StableManifestUrl;
        }

        if (string.IsNullOrWhiteSpace(config.DevManifestUrl))
        {
            config.DevManifestUrl = new PluginConfiguration().DevManifestUrl;
        }

        SanitizeServers(config.RadarrServers);
        SanitizeServers(config.SonarrServers);
    }

    private static void SanitizeServers(List<ArrServerConfig> servers)
    {
        servers.RemoveAll(s => string.IsNullOrWhiteSpace(s.Url) || string.IsNullOrWhiteSpace(s.ApiKey));
        foreach (var server in servers)
        {
            server.Name = server.Name.Trim();
            server.Url = server.Url.Trim();
            server.ApiKey = server.ApiKey.Trim();
            server.LibraryPathPrefixes = server.LibraryPathPrefixes
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Media Integrity Scanner",
                DisplayName = "Media Integrity Scanner",
                EmbeddedResourcePath = GetType().Namespace + ".Web.integrity_dashboard.html",
                EnableInMainMenu = true,
                MenuIcon = "fact_check"
            },
            new PluginPageInfo
            {
                Name = "Media Issues",
                // Explicit DisplayName is required here, not optional -- Jellyfin's
                // own ConfigurationPageInfo (Jellyfin.Api/Models/ConfigurationPageInfo.cs)
                // falls back to the *plugin's* own Name whenever a page's DisplayName
                // is unset, which silently makes every main-menu page from the same
                // plugin show identical sidebar text regardless of each page's own
                // Name. Confirmed live (2026-08-23): without this, both pages showed
                // "Media Integrity Scanner" in the sidebar despite different Name
                // values -- Name only affects the page's URL slug, not what's displayed.
                DisplayName = "Media Issues",
                EmbeddedResourcePath = GetType().Namespace + ".Web.integrity_issues.html",
                EnableInMainMenu = true,
                MenuIcon = "healing"
            },
            new PluginPageInfo
            {
                Name = "Media Integrity Scanner Settings",
                EmbeddedResourcePath = GetType().Namespace + ".Web.integrity_settings.html"
            }
        };
    }
}

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
using Jellyfin.Plugin.MediaIntegrityScanner.Updates;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaIntegrityScanner;

/// <summary>
/// Plugin configuration for Media Integrity Scanner.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of files scanned concurrently. Defaults
    /// to half the host's vCPU count (floored at 1) rather than a flat value,
    /// since a single-core-friendly default undersells a large host and a
    /// large flat default could overwhelm a small one. This default is only
    /// ever applied to a genuinely fresh install with no saved configuration
    /// file yet -- an existing installation's saved value (including this
    /// exact number, if it happens to match) always wins over recomputing it,
    /// since XML deserialization overwrites this property's initializer for
    /// every install that already has a config file on disk. In other words:
    /// once set, an explicit choice here sticks across restarts/upgrades.
    /// </summary>
    public int MaxConcurrentScans { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Gets or sets the delay in milliseconds between scanning each file.
    /// </summary>
    public int DelayBetweenFilesMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets a value indicating whether scanning pauses during active playback.
    /// </summary>
    public bool PauseDuringPlayback { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Phase 2 deep scanning is enabled.
    /// </summary>
    public bool EnableDeepScan { get; set; }

    /// <summary>
    /// Gets or sets the maximum aggregate read rate in MB/s across all
    /// scanning I/O -- a single shared budget every concurrent scan draws
    /// from (see <see cref="Scanner.SharedBandwidthLimiter"/>), not a
    /// per-file-independent cap. Running more concurrent scans divides this
    /// budget among them rather than multiplying total throughput past it.
    /// Header and Deep-phase scans share the same budget; Deep scans get
    /// priority when both are contending for it, since a full decode carries
    /// the larger combined I/O and CPU footprint.
    /// </summary>
    public int MaxReadRateMbPerSec { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether scanning is restricted to quiet hours.
    /// </summary>
    public bool UseQuietHoursOnly { get; set; }

    /// <summary>
    /// Gets or sets the start of the quiet hours window (HH:mm format).
    /// </summary>
    public string QuietHoursStart { get; set; } = "02:00";

    /// <summary>
    /// Gets or sets the end of the quiet hours window (HH:mm format).
    /// </summary>
    public string QuietHoursEnd { get; set; } = "06:00";

    /// <summary>
    /// Gets or sets a user-specified override path for the ffmpeg binary.
    /// </summary>
    public string? FfmpegPathOverride { get; set; }

    /// <summary>
    /// Gets or sets a user-specified override path for the ffprobe binary.
    /// </summary>
    public string? FfprobePathOverride { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly added items are scanned automatically.
    /// </summary>
    public bool ScanOnItemAdded { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether scan records are purged when items are removed.
    /// </summary>
    public bool PurgeOnItemRemoved { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a changed/replaced item (same
    /// Jellyfin item ID, different file contents) triggers an immediate
    /// Header-phase rescan, the same way <see cref="ScanOnItemAdded"/> does
    /// for genuinely new items. On by default, decoupled from
    /// <see cref="ScanOnItemAdded"/> so the two behaviors can be toggled
    /// independently. Even when off, a changed file still eventually gets
    /// caught by <c>IsCurrentAsync</c>'s mtime comparison on the next
    /// scheduled sweep -- this setting only controls whether it happens
    /// immediately via the library event.
    /// </summary>
    public bool ScanOnItemUpdated { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a weekly scheduled task
    /// reconciles this plugin's scan-history database against the real
    /// library contents, purging rows for items no longer present. On by
    /// default -- this is cleanup, not something that changes running code,
    /// in the same spirit as <see cref="EnableAutoDatabaseMaintenance"/>.
    /// Independent of <see cref="PurgeOnItemRemoved"/>'s event-driven purge:
    /// this catches whatever that event-driven path missed (plugin offline
    /// at removal time, the setting was off, an unverified Jellyfin removal
    /// path that doesn't fire <c>ItemRemoved</c>). Always skips itself while
    /// a scan is in progress, regardless of this setting.
    /// </summary>
    public bool EnableAutoReconciliation { get; set; } = true;

    /// <summary>
    /// Gets or sets which release channel to check for updates against.
    /// </summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>
    /// Gets or sets the manifest URL used to classify a discovered version as a
    /// stable release. Only versions Jellyfin already knows about (i.e. from a
    /// repository the admin has registered under Dashboard &gt; Plugins &gt;
    /// Repositories) can ever be found -- this URL is used purely to label
    /// which of those already-discovered versions counts as "stable", not to
    /// fetch anything directly.
    /// </summary>
    public string StableManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest.json";

    /// <summary>
    /// Gets or sets the manifest URL used to classify a discovered version as a
    /// development/pre-release build. See <see cref="StableManifestUrl"/> for
    /// how this is used.
    /// </summary>
    public string DevManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/mcgarrah/jellyfin-plugin-media-integrity-scanner/main/manifest-unstable.json";

    /// <summary>
    /// Gets or sets a value indicating whether a weekly scheduled task should
    /// automatically install (stage) a newer version for the configured
    /// <see cref="UpdateChannel"/>, if one is available. Off by default --
    /// conservative installs can leave this disabled and only ever update via
    /// the dashboard's manual "Update Now" button. Installing a new version
    /// this way does not by itself apply it; Jellyfin still needs a restart
    /// to load it (see <see cref="AutoRestartAfterUpdate"/>).
    /// </summary>
    public bool EnableAutoUpdate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether, after a successful automatic
    /// install, the plugin should also restart Jellyfin itself once no one
    /// has an active playback session -- rather than leaving the update
    /// staged until an admin restarts manually. Off by default, and has no
    /// effect unless <see cref="EnableAutoUpdate"/> is also on.
    /// </summary>
    public bool AutoRestartAfterUpdate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a weekly scheduled task should
    /// run an integrity check and (if it passes) a VACUUM against the
    /// plugin's own SQLite database. On by default -- unlike
    /// <see cref="EnableAutoUpdate"/>, this doesn't change any running code,
    /// and the plugin's whole purpose is catching corruption early, so its
    /// own database getting a periodic integrity check is in the same
    /// spirit. Always skips itself while a scan is in progress, regardless
    /// of this setting.
    /// </summary>
    public bool EnableAutoDatabaseMaintenance { get; set; } = true;

    /// <summary>
    /// Gets or sets the hardware acceleration backend to request via ffmpeg's
    /// <c>-hwaccel</c> flag for Phase 2 (FullDecode) scans. Reuses Jellyfin's own
    /// <see cref="HardwareAccelerationType"/> so this setting reads the same as
    /// Jellyfin's own Playback transcoding settings. <c>none</c> (the default)
    /// means pure CPU software decode, matching this plugin's behavior before
    /// this setting existed. Has no effect on Phase 1 (Header) scans, which use
    /// ffprobe and never decode the file. Types with no supported decode-only
    /// mapping (<c>amf</c>, <c>v4l2m2m</c>, <c>rkmpp</c>) silently fall back to
    /// software decode rather than passing an unverified flag to ffmpeg.
    /// </summary>
    public HardwareAccelerationType HardwareAccelerationType { get; set; } = HardwareAccelerationType.none;

    /// <summary>
    /// Gets or sets the configured Radarr servers a bad movie file can be
    /// forwarded to (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 7). A list
    /// to support the multi-server routing planned for Phase 3 (e.g. a
    /// separate "Family"/Disney server), though Phase 1 only ever uses the
    /// first entry -- no routing logic exists yet. Empty by default; the
    /// Media Issues page's manual "Send to Radarr" action is simply
    /// unavailable for movies until at least one server is configured.
    /// </summary>
    public List<ArrServerConfig> RadarrServers { get; set; } = new();

    /// <summary>Gets or sets the configured Sonarr servers. See <see cref="RadarrServers"/>.</summary>
    public List<ArrServerConfig> SonarrServers { get; set; } = new();

    /// <summary>
    /// Gets or sets how many days back to look for a "grabbed" history
    /// record when deciding what to blocklist alongside a file deletion
    /// (<c>ARR-INTEGRATION-PROPOSAL.md</c> section 4). A grab older than
    /// this is assumed already-superseded and not worth blocklisting -- the
    /// remediation flow just deletes the file and triggers a plain search
    /// instead of blocklisting a stale release.
    /// </summary>
    public int HistoryLookbackDays { get; set; } = 30;
}

/// <summary>
/// One configured Radarr or Sonarr server connection -- see
/// <see cref="PluginConfiguration.RadarrServers"/>/<see cref="PluginConfiguration.SonarrServers"/>.
/// </summary>
public class ArrServerConfig
{
    /// <summary>Gets or sets a display name for this server (e.g. "Main", "Disney/Family").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the server's base URL (e.g. <c>http://192.168.86.52:7878</c>).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the server's API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which library path prefixes route to this server, for
    /// the Phase 3 multi-server support. Unused in Phase 1 (a single
    /// configured server of each type is assumed).
    /// </summary>
    public List<string> LibraryPathPrefixes { get; set; } = new();
}

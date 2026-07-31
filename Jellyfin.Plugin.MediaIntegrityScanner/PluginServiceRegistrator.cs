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

using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.EventHandlers;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaIntegrityScanner;

/// <summary>
/// Registers plugin services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Scanner components
        serviceCollection.AddSingleton<FfmpegResolver>();
        serviceCollection.AddSingleton<FfmpegWrapper>();
        serviceCollection.AddSingleton<IScanEngine, ScanEngine>();

        // Database — registered once as the concrete type; IDatabaseManager
        // forwards to the same singleton instance so both resolve identically.
        serviceCollection.AddSingleton<SqliteDatabaseManager>();
        serviceCollection.AddSingleton<IDatabaseManager>(sp => sp.GetRequiredService<SqliteDatabaseManager>());

        // Event handlers
        serviceCollection.AddHostedService<LibraryMonitor>();
    }
}

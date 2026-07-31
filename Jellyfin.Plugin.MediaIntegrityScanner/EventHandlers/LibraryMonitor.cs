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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaIntegrityScanner.EventHandlers;

/// <summary>
/// Monitors Jellyfin library events to trigger scans on new items
/// and purge records on removed items.
/// </summary>
public class LibraryMonitor : IHostedService, IDisposable
{
    private readonly ILibraryManager _library;
    private readonly IScanEngine _scanner;
    private readonly IDatabaseManager _db;
    private readonly ILogger<LibraryMonitor> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMonitor"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="scanner">Scan engine.</param>
    /// <param name="db">Database manager.</param>
    /// <param name="logger">Logger instance.</param>
    public LibraryMonitor(
        ILibraryManager library,
        IScanEngine scanner,
        IDatabaseManager db,
        ILogger<LibraryMonitor> logger)
    {
        _library = library;
        _scanner = scanner;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Registers library event handlers on server startup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemRemoved += OnItemRemoved;

        _logger.LogInformation("Library event monitor registered");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters library event handlers on server shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemRemoved -= OnItemRemoved;

        _logger.LogInformation("Library event monitor unregistered");
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.ScanOnItemAdded != true)
        {
            return;
        }

        var item = e.Item;
        if (!IsMediaItem(item))
        {
            return;
        }

        _logger.LogDebug("Item added: {Name} ({Path}), queuing for scan", item.Name, item.Path);

        // Fire-and-forget with error logging
        _ = Task.Run(async () =>
        {
            try
            {
                await _scanner.ScanItemAsync(item, ScanPhase.Header, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning newly added item: {Path}", item.Path);
            }
        });
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.PurgeOnItemRemoved != true)
        {
            return;
        }

        var item = e.Item;
        if (!IsMediaItem(item))
        {
            return;
        }

        _logger.LogDebug("Item removed: {Name}, purging scan records", item.Name);

        _ = Task.Run(async () =>
        {
            try
            {
                await _db.PurgeItemAsync(item.Id.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error purging scan records for removed item: {Id}", item.Id);
            }
        });
    }

    private static bool IsMediaItem(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        return item.MediaType == MediaType.Video || item.MediaType == MediaType.Audio;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _library.ItemAdded -= OnItemAdded;
            _library.ItemRemoved -= OnItemRemoved;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

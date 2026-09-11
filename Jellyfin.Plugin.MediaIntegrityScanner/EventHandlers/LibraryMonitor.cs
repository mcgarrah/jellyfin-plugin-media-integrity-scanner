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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
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
public partial class LibraryMonitor : IHostedService, IDisposable
{
    // A large bulk import (e.g. a whole season, or an initial full-library
    // scan of a big collection) fires one ItemAdded/ItemUpdated per file.
    // Dispatching an unbounded Task.Run per event briefly creates thousands
    // of queued continuations before ScanEngine's own concurrency semaphore
    // starts draining them. Routing through a bounded channel with a small,
    // fixed pool of consumers instead caps how much event-driven work can be
    // in flight at once, without changing actual scan throughput (still
    // governed by ScanEngine's MaxConcurrentScans semaphore either way).
    // DropWrite on overflow is intentional, not a bug: nothing is permanently
    // lost -- the scheduled Header/Deep Scan tasks and the weekly
    // reconciliation task are the durable backstop for anything the
    // event-driven path misses, exactly as they already are for a plugin
    // restart mid-burst.
    private const int ScanQueueCapacity = 1000;

    private readonly ILibraryManager _library;
    private readonly IScanEngine _scanner;
    private readonly IDatabaseManager _db;
    private readonly ILogger<LibraryMonitor> _logger;
    private readonly Channel<ScanRequest> _scanQueue = Channel.CreateBounded<ScanRequest>(
        new BoundedChannelOptions(ScanQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? _consumerCts;
    private Task[]? _consumerTasks;
    private bool _disposed;

    // Shared between OnItemAdded and OnItemUpdated: Jellyfin's own metadata
    // refresh commonly fires ItemUpdated moments after ItemAdded for a
    // genuinely new item, before that item's own add-triggered scan has had
    // a chance to run (let alone save a record IsCurrentAsync could compare
    // against) -- without this, a burst of new items roughly doubles the
    // event-driven scan queue. An item ID present here has a scan already
    // dispatched (queued or running) via one of these two handlers; the
    // other handler skips it outright rather than queuing a duplicate.
    private readonly ConcurrentDictionary<string, byte> _itemsWithScanDispatched = new();

    private readonly record struct ScanRequest(BaseItem Item, string ItemId, bool CheckCurrentFirst);

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
    /// Initializes the database schema and registers library event handlers on server startup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _db.InitializeAsync().ConfigureAwait(false);

        // Same clamp ScanEngine applies to this setting -- see the matching
        // comment there. Not the concurrency limit itself (that's still
        // ScanEngine's own semaphore); just how many consumers can be
        // concurrently dequeuing and awaiting it, so the pool doesn't become
        // its own bottleneck.
        var consumerCount = Math.Max(1, Plugin.Instance?.Configuration?.MaxConcurrentScans ?? 1);
        _consumerCts = new CancellationTokenSource();
        _consumerTasks = new Task[consumerCount];
        for (var i = 0; i < consumerCount; i++)
        {
            _consumerTasks[i] = ConsumeScanQueueAsync(_consumerCts.Token);
        }

        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemUpdated;
        _library.ItemRemoved += OnItemRemoved;

        LogMonitorRegistered();
    }

    /// <summary>
    /// Unregisters library event handlers on server shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemUpdated;
        _library.ItemRemoved -= OnItemRemoved;

        // Stop consumers immediately rather than draining whatever's still
        // queued -- consistent with today's fire-and-forget Task.Run
        // behavior, where anything not yet complete is simply abandoned on
        // shutdown too.
        _consumerCts?.Cancel();

        LogMonitorUnregistered();
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

        var itemId = item.Id.ToString();
        if (!_itemsWithScanDispatched.TryAdd(itemId, 0))
        {
            return;
        }

        LogItemQueuedForScan(item.Name, item.Path);

        if (!_scanQueue.Writer.TryWrite(new ScanRequest(item, itemId, CheckCurrentFirst: false)))
        {
            LogScanQueueFull(item.Path);
            _itemsWithScanDispatched.TryRemove(itemId, out _);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.ScanOnItemUpdated != true)
        {
            return;
        }

        var item = e.Item;
        if (!IsMediaItem(item))
        {
            return;
        }

        var itemId = item.Id.ToString();
        if (!_itemsWithScanDispatched.TryAdd(itemId, 0))
        {
            // Most commonly OnItemAdded's own scan for this exact item,
            // dispatched moments earlier by Jellyfin's own post-add metadata
            // refresh -- see the field comment on _itemsWithScanDispatched.
            return;
        }

        // Same queue as OnItemAdded, with CheckCurrentFirst set -- a
        // Header-phase rescan is deliberately used here, not FullDecode,
        // matching the cost of the other event-driven path and enough to
        // pick up the new mtime/size immediately; a full deep rescan still
        // happens on its own schedule if deep scanning is enabled.
        //
        // Unlike OnItemAdded (a genuinely new item can never have an existing
        // record), ItemUpdated fires for reasons that have nothing to do with
        // the file's own bytes -- Jellyfin's metadata refresh commonly raises
        // it right after ItemAdded once technical info (duration, codecs) is
        // populated, with the file itself unchanged. IsCurrentAsync's mtime
        // check (applied by the consumer, before scanning) filters those out
        // for an already-settled item; the dedup guard above only catches the
        // narrower race against a scan already in flight for the very same
        // event burst.
        if (!_scanQueue.Writer.TryWrite(new ScanRequest(item, itemId, CheckCurrentFirst: true)))
        {
            LogScanQueueFull(item.Path);
            _itemsWithScanDispatched.TryRemove(itemId, out _);
        }
    }

    /// <summary>
    /// Drains <see cref="_scanQueue"/> until <paramref name="cancellationToken"/>
    /// fires. A fixed pool of these runs concurrently (started in
    /// <see cref="StartAsync"/>), each processing one queued item at a time --
    /// actual scan concurrency is still bounded by ScanEngine's own semaphore
    /// regardless of how many consumers are pulling from the queue.
    /// </summary>
    private async Task ConsumeScanQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _scanQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (request.CheckCurrentFirst)
                    {
                        if (await _db.IsCurrentAsync(request.ItemId, request.Item.Path, (int)ScanPhase.Header).ConfigureAwait(false))
                        {
                            continue;
                        }

                        LogItemUpdateQueuedForScan(request.Item.Name, request.Item.Path);
                    }

                    await _scanner.ScanItemAsync(request.Item, ScanPhase.Header, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (request.CheckCurrentFirst)
                    {
                        LogScanUpdatedItemError(ex, request.Item.Path);
                    }
                    else
                    {
                        LogScanNewItemError(ex, request.Item.Path);
                    }
                }
                finally
                {
                    _itemsWithScanDispatched.TryRemove(request.ItemId, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown (StopAsync cancels _consumerCts) -- not an error.
        }
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

        LogItemPurgeQueued(item.Name);

        _ = Task.Run(async () =>
        {
            try
            {
                await _db.PurgeItemAsync(item.Id.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogPurgeError(ex, item.Id);
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
            _library.ItemUpdated -= OnItemUpdated;
            _library.ItemRemoved -= OnItemRemoved;
            _consumerCts?.Cancel();
            _consumerCts?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Library event monitor registered")]
    private partial void LogMonitorRegistered();

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Library event monitor unregistered")]
    private partial void LogMonitorUnregistered();

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Item added: {Name} ({Path}), queuing for scan")]
    private partial void LogItemQueuedForScan(string? name, string? path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Error scanning newly added item: {Path}")]
    private partial void LogScanNewItemError(Exception ex, string? path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Item removed: {Name}, purging scan records")]
    private partial void LogItemPurgeQueued(string? name);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error purging scan records for removed item: {Id}")]
    private partial void LogPurgeError(Exception ex, Guid id);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Item updated: {Name} ({Path}), queuing for rescan")]
    private partial void LogItemUpdateQueuedForScan(string? name, string? path);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Error scanning updated item: {Path}")]
    private partial void LogScanUpdatedItemError(Exception ex, string? path);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Event-driven scan queue is full, dropping trigger for: {Path} -- scheduled scans will still catch it")]
    private partial void LogScanQueueFull(string? path);
}

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
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.EventHandlers;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for LibraryMonitor's ItemAdded/ItemRemoved handling. Uses Plugin.Instance
/// reflection plumbing (TestPluginContext) to control ScanOnItemAdded/
/// PurgeOnItemRemoved, so it shares the "PluginInstance" xUnit collection with
/// every other test class that touches that static (see PluginInstanceCollection).
///
/// The event handlers dispatch work via a fire-and-forget Task.Run for the
/// actual scan/purge, but the ScanOnItemAdded/PurgeOnItemRemoved and
/// is-this-a-media-item checks happen synchronously before that dispatch —
/// so the "should do nothing" tests need no waiting, while the "should do
/// something" tests wait on a TaskCompletionSource set from a mock callback.
/// </summary>
[Collection("PluginInstance")]
public class LibraryMonitorTests : IDisposable
{
    public void Dispose() => TestPluginContext.Clear();

    private static Movie MakeMediaItem()
    {
        var id = Guid.NewGuid();
        return new Movie { Id = id, Path = $"/media/{id:N}.mkv" };
    }

    private static LibraryMonitor CreateMonitor(
        Mock<ILibraryManager> library,
        Mock<IScanEngine> scanner,
        Mock<IDatabaseManager> db)
    {
        return new LibraryMonitor(library.Object, scanner.Object, db.Object, NullLogger<LibraryMonitor>.Instance);
    }

    [Fact]
    public async Task StartAsync_InitializesDatabase_AndSubscribesToLibraryEvents()
    {
        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        db.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);

        var monitor = CreateMonitor(library, scanner, db);

        await monitor.StartAsync(CancellationToken.None);

        db.Verify(d => d.InitializeAsync(), Times.Once);

        // Subscription itself is verified indirectly: raising the event after
        // StartAsync should reach the handler (covered by the tests below).
        var item = MakeMediaItem();
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = true });
        var tcs = new TaskCompletionSource();
        scanner.Setup(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = item });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scanner.Verify(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnItemAdded_ScansItem_WhenScanOnItemAddedTrue_AndIsMediaItem()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        var item = MakeMediaItem();
        var tcs = new TaskCompletionSource();
        scanner.Setup(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = item });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scanner.Verify(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnItemAdded_DoesNothing_WhenScanOnItemAddedFalse()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = false });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });

        // The ScanOnItemAdded check happens synchronously before any dispatch,
        // so there is nothing to wait for here.
        scanner.Verify(
            s => s.ScanItemAsync(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnItemAdded_DoesNothing_WhenItemIsNotAMediaItem()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        var nonMediaItem = new Movie { Id = Guid.NewGuid(), Path = null };

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = nonMediaItem });

        scanner.Verify(
            s => s.ScanItemAsync(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnItemUpdated_RescansItem_WhenScanOnItemUpdatedTrue_AndFileActuallyChanged()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemUpdated = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var item = MakeMediaItem();
        db.Setup(d => d.IsCurrentAsync(item.Id.ToString(), item.Path, (int)ScanPhase.Header))
            .ReturnsAsync(false);
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        var tcs = new TaskCompletionSource();
        scanner.Setup(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = item });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scanner.Verify(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnItemUpdated_DoesNotRescan_WhenExistingScanIsAlreadyCurrent()
    {
        // Real scenario this guards against: Jellyfin's metadata refresh fires
        // ItemUpdated right after ItemAdded once technical info is populated,
        // with the file itself unchanged -- rescanning here would double the
        // event-driven scan load for every newly added file.
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemUpdated = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var item = MakeMediaItem();
        var tcs = new TaskCompletionSource();
        db.Setup(d => d.IsCurrentAsync(item.Id.ToString(), item.Path, (int)ScanPhase.Header))
            .ReturnsAsync(true)
            .Callback(() => tcs.TrySetResult());
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = item });

        // Wait for the (async, fire-and-forget) currency check to actually
        // run -- once it has, the "return early" branch is a synchronous
        // continuation, so there's nothing further to race.
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scanner.Verify(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnItemUpdated_SkipsDispatch_WhenOnItemAddedScanForSameItemIsAlreadyInFlight()
    {
        // The real scenario this guards against, and the actual cause of the
        // CI-caught regression this test was added to fix: Jellyfin fires
        // ItemUpdated for a genuinely new item moments after ItemAdded, before
        // that item's own add-triggered scan has completed (or saved a record
        // IsCurrentAsync could compare against) -- without this dedup guard,
        // that doubles the event-driven scan queue on every burst-add.
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = true, ScanOnItemUpdated = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var item = MakeMediaItem();

        // OnItemAdded's scan is held open (as it would be for the real
        // DelayBetweenFilesMs + ffprobe duration) so ItemUpdated fires while
        // it's still "in flight" from this handler's point of view.
        var addedScanStarted = new TaskCompletionSource();
        var releaseAddedScan = new TaskCompletionSource();
        scanner.Setup(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                addedScanStarted.TrySetResult();
                await releaseAddedScan.Task;
            });

        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = item });
        await addedScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = item });

        // The dedup check runs synchronously inside OnItemUpdated, before any
        // Task.Run is scheduled -- nothing to wait for here, unlike the
        // IsCurrentAsync check which only runs once dispatch already won.
        scanner.Verify(s => s.ScanItemAsync(item, ScanPhase.Header, It.IsAny<CancellationToken>()), Times.Once);
        db.Verify(d => d.IsCurrentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);

        releaseAddedScan.TrySetResult();
    }

    [Fact]
    public async Task OnItemUpdated_DoesNothing_WhenScanOnItemUpdatedFalse()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemUpdated = false });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });

        scanner.Verify(
            s => s.ScanItemAsync(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnItemUpdated_DoesNothing_WhenItemIsNotAMediaItem()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemUpdated = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        var nonMediaItem = new Movie { Id = Guid.NewGuid(), Path = null };

        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = nonMediaItem });

        scanner.Verify(
            s => s.ScanItemAsync(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnItemRemoved_PurgesRecord_WhenPurgeOnItemRemovedTrue_AndIsMediaItem()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { PurgeOnItemRemoved = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        var item = MakeMediaItem();
        var tcs = new TaskCompletionSource();
        db.Setup(d => d.PurgeItemAsync(item.Id.ToString()))
            .Returns(Task.CompletedTask)
            .Callback(() => tcs.TrySetResult());

        library.Raise(l => l.ItemRemoved += null, library.Object, new ItemChangeEventArgs { Item = item });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        db.Verify(d => d.PurgeItemAsync(item.Id.ToString()), Times.Once);
    }

    [Fact]
    public async Task OnItemRemoved_DoesNothing_WhenPurgeOnItemRemovedFalse()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { PurgeOnItemRemoved = false });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);

        library.Raise(l => l.ItemRemoved += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });

        db.Verify(d => d.PurgeItemAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromEvents()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = true, ScanOnItemUpdated = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });
        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });

        scanner.Verify(
            s => s.ScanItemAsync(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromEvents()
    {
        TestPluginContext.SetConfiguration(new PluginConfiguration { ScanOnItemAdded = true, ScanOnItemUpdated = true });

        var library = new Mock<ILibraryManager>();
        var scanner = new Mock<IScanEngine>();
        var db = new Mock<IDatabaseManager>();
        var monitor = CreateMonitor(library, scanner, db);
        await monitor.StartAsync(CancellationToken.None);
        monitor.Dispose();

        library.Raise(l => l.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });
        library.Raise(l => l.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = MakeMediaItem() });

        scanner.Verify(
            s => s.ScanItemAsync(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<ScanPhase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

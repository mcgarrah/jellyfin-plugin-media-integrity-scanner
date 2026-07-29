using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// Interface for the media integrity scan engine.
/// </summary>
public interface IScanEngine
{
    /// <summary>
    /// Gets a value indicating whether a scan is currently in progress.
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// Scans a single item for media integrity.
    /// </summary>
    /// <param name="item">The Jellyfin library item to scan.</param>
    /// <param name="phase">The scan phase to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ScanItemAsync(BaseItem item, ScanPhase phase, CancellationToken cancellationToken);

    /// <summary>
    /// Scans all items in a library.
    /// </summary>
    /// <param name="libraryId">Optional library ID to scope the scan.</param>
    /// <param name="phase">The scan phase to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ScanLibraryAsync(string? libraryId, ScanPhase phase, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the currently running scan.
    /// </summary>
    void Cancel();
}

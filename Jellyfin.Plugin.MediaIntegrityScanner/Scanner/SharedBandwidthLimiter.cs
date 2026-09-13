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

namespace Jellyfin.Plugin.MediaIntegrityScanner.Scanner;

/// <summary>
/// A shared, aggregate token-bucket bandwidth budget, replacing the old
/// per-file-independent pacing in the retired <c>ScanThrottle.ComputeReadRateDelay</c>.
/// That mechanism recomputed each file's own delay against the <em>full</em>
/// configured cap with no awareness of other concurrent scans, so real
/// aggregate throughput could reach <c>MaxConcurrentScans &#215; cap</c> instead
/// of the single configured ceiling. Every caller -- regardless of scan phase
/// or how many scans are running concurrently -- draws from this one shared
/// budget instead, so concurrency divides the available bandwidth rather than
/// multiplying past the cap.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConsumeUpTo"/> is a pure, deterministic state transition given
/// the injected <see cref="TimeProvider"/> reading -- no <c>Task.Delay</c> or
/// real waiting happens here, which is what makes this class directly
/// unit-testable without needing real time to pass. The async polling loop
/// that actually waits when it returns less than requested lives in the
/// caller (<see cref="ScanEngine"/>), matching the same pure-logic/async-wrapper
/// split <c>ScanThrottle.ComputeReadRateDelay</c> used before it.
/// </para>
/// <para>
/// <see cref="ConsumeUpTo"/> returns a partial amount rather than an
/// all-or-nothing bool (its pre-2026-09-13 shape, named <c>TryConsume</c>) --
/// a real, demonstrated production bug: <see cref="Refill"/> caps the budget
/// at one second's worth of the configured rate, so a request for an entire
/// real media file's worth (routinely hundreds of MB to several GB) could
/// never be satisfied atomically no matter how long the caller waited, even
/// at a generous rate. Two live jellyfin-test instances wedged completely
/// (zero scan progress for 7+ hours, resolved only by an explicit manual
/// Cancel) at MaxReadRateMbPerSec settings of 5 and 10 -- ordinary, not
/// misconfigured, values. The caller now drains whatever's currently
/// available and asks again for the remainder, so a large file finishes in
/// roughly fileSizeMb / maxRateMbPerSec seconds, the same way a real
/// throttled read behaves, instead of waiting forever.
/// </para>
/// <para>
/// Deep (<see cref="ScanPhase.FullDecode"/>) scans get priority over Header
/// scans when both are contending for the budget -- Header requests back off
/// entirely (rather than draining the budget out from under a waiting Deep
/// request) for as long as any Deep scan is registered as waiting via
/// <see cref="MarkDeepScanWaiting"/>. This is a soft priority, not a hard
/// reservation: a Header scan that already holds the budget it needs is
/// never preempted mid-consumption, and a lone Header scan with no Deep scan
/// contending for the budget is never blocked by this rule.
/// </para>
/// </remarks>
public sealed class SharedBandwidthLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private double _availableMb;
    private DateTimeOffset _lastRefillUtc;
    private int _deepScansWaiting;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedBandwidthLimiter"/> class.
    /// </summary>
    /// <param name="timeProvider">Clock source. Defaults to <see cref="TimeProvider.System"/>; tests inject a fake for deterministic control.</param>
    public SharedBandwidthLimiter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Consumes up to <paramref name="megabytes"/> from the shared budget
    /// right now, without waiting.
    /// </summary>
    /// <param name="megabytes">Amount requested, in MB. Non-positive amounts always return 0.</param>
    /// <param name="maxRateMbPerSec">Configured aggregate cap, MB/s. Zero or negative disables throttling entirely -- the full amount is always returned immediately.</param>
    /// <param name="phase">Which scan phase is requesting -- Deep scans get priority, see remarks on the type.</param>
    /// <returns>
    /// The amount actually consumed, in MB -- may be less than requested
    /// (including zero) if the budget can't cover it yet. Never more than
    /// <paramref name="megabytes"/>. Callers drain a large request across
    /// repeated calls (waiting between them) rather than requiring the whole
    /// amount to be available atomically; see remarks on the type for why
    /// that used to wedge indefinitely on any file bigger than one second's
    /// configured rate.
    /// </returns>
    public double ConsumeUpTo(double megabytes, int maxRateMbPerSec, ScanPhase phase)
    {
        if (maxRateMbPerSec <= 0 || megabytes <= 0)
        {
            return Math.Max(0, megabytes);
        }

        lock (_lock)
        {
            Refill(maxRateMbPerSec);

            if (phase == ScanPhase.Header && Volatile.Read(ref _deepScansWaiting) > 0)
            {
                return 0;
            }

            var consumed = Math.Min(_availableMb, megabytes);
            _availableMb -= consumed;
            return consumed;
        }
    }

    /// <summary>
    /// Registers a Deep-phase scan as currently waiting on the budget, so
    /// concurrent Header requests back off until it's cleared via
    /// <see cref="MarkDeepScanNoLongerWaiting"/>. Callers should only mark
    /// themselves waiting once an initial <see cref="ConsumeUpTo"/> attempt
    /// has already failed, not unconditionally before every attempt.
    /// </summary>
    public void MarkDeepScanWaiting() => Interlocked.Increment(ref _deepScansWaiting);

    /// <summary>
    /// Clears a Deep-phase scan's waiting registration. Must be paired with
    /// exactly one prior <see cref="MarkDeepScanWaiting"/> call, typically in
    /// a <c>finally</c> block so it's cleared even if the wait is cancelled.
    /// </summary>
    public void MarkDeepScanNoLongerWaiting() => Interlocked.Decrement(ref _deepScansWaiting);

    /// <summary>
    /// Refills the available budget based on elapsed time since the last
    /// refill, at <paramref name="maxRateMbPerSec"/> MB/s. Accrual is capped
    /// at one second's worth of budget -- no unbounded burst credit builds up
    /// while the limiter goes unused between scans (e.g. between library
    /// sweeps), which would otherwise let a large backlog of saved-up budget
    /// defeat the cap the moment scanning resumes. The very first call seeds
    /// the budget at a full second's worth immediately, rather than starting
    /// at zero and forcing the first scan of a fresh session to wait purely
    /// because of construction timing.
    /// </summary>
    private void Refill(int maxRateMbPerSec)
    {
        var now = _timeProvider.GetUtcNow();

        if (!_initialized)
        {
            _availableMb = maxRateMbPerSec;
            _lastRefillUtc = now;
            _initialized = true;
            return;
        }

        var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
        _lastRefillUtc = now;

        if (elapsedSeconds <= 0)
        {
            return;
        }

        _availableMb = Math.Min(maxRateMbPerSec, _availableMb + (elapsedSeconds * maxRateMbPerSec));
    }
}

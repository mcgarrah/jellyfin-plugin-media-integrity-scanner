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
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for <see cref="SharedBandwidthLimiter"/>'s pure <c>TryConsume</c> state
/// transitions, driven by a hand-written fake <see cref="TimeProvider"/> for
/// deterministic control -- no real waiting happens in this file.
/// </summary>
public class SharedBandwidthLimiterTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;
    }

    [Fact]
    public void TryConsume_AlwaysSucceeds_WhenRateIsZeroOrNegative()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.True(limiter.TryConsume(1_000_000, 0, ScanPhase.Header));
        Assert.True(limiter.TryConsume(1_000_000, -1, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_AlwaysSucceeds_WhenMegabytesIsZeroOrNegative()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.True(limiter.TryConsume(0, 10, ScanPhase.Header));
        Assert.True(limiter.TryConsume(-5, 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_SucceedsImmediately_ForAFreshLimiter_UpToOneSecondsBudget()
    {
        // The bucket seeds itself at a full second's worth on first use,
        // rather than starting at zero and forcing the very first scan of a
        // session to wait purely because of construction timing.
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_Fails_WhenRequestExceedsAvailableBudget()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        // First call seeds a 10 MB budget and immediately spends all of it.
        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        // No time has passed, so nothing has refilled -- this must fail.
        Assert.False(limiter.TryConsume(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_Succeeds_OnceEnoughTimeHasElapsedToRefill()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.False(limiter.TryConsume(5, maxRateMbPerSec: 10, ScanPhase.Header));

        // At 10 MB/s, 0.5s should refill exactly 5 MB.
        clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.True(limiter.TryConsume(5, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_AccrualIsCapped_AtOneSecondsWorth_NoUnboundedBurstCredit()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        // Drain the initial seed, then let a long time pass -- simulating the
        // limiter sitting unused between library sweeps.
        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        clock.Advance(TimeSpan.FromHours(1));

        // If accrual were unbounded, an hour at 10 MB/s would have built up
        // 36,000 MB of credit. It should be capped at 10 MB (one second's worth).
        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.False(limiter.TryConsume(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_DividesBudget_AcrossConcurrentConsumersRatherThanMultiplyingIt()
    {
        // The whole point of a shared budget: several concurrent "scans" (all
        // calling TryConsume against the same limiter instance) can never
        // collectively exceed the configured cap, regardless of how many
        // there are -- unlike the old per-file-independent calculation.
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.True(limiter.TryConsume(4, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.True(limiter.TryConsume(4, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.True(limiter.TryConsume(2, maxRateMbPerSec: 10, ScanPhase.Header));
        // A fourth concurrent consumer, still within the same 1-second window,
        // finds the shared budget already fully spent by the other three.
        Assert.False(limiter.TryConsume(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_HeaderScan_BacksOff_WhileADeepScanIsWaiting()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        // Drain the budget so there's genuinely nothing available, then
        // register a Deep scan as waiting on it.
        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        limiter.MarkDeepScanWaiting();

        // No time has passed, so there's nothing for a Header request to
        // succeed against anyway -- this alone doesn't prove priority yet.
        Assert.False(limiter.TryConsume(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_HeaderScan_DoesNotDrainBudget_AheadOfAWaitingDeepScan()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        limiter.MarkDeepScanWaiting();

        // Budget refills to 5 MB -- normally enough for this Header request,
        // but a Deep scan is registered as waiting, so it must back off
        // entirely rather than claiming it first.
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.False(limiter.TryConsume(5, maxRateMbPerSec: 10, ScanPhase.Header));

        // The Deep scan itself is not subject to that same back-off, and can
        // claim the same budget the Header request was denied.
        Assert.True(limiter.TryConsume(5, maxRateMbPerSec: 10, ScanPhase.FullDecode));
    }

    [Fact]
    public void TryConsume_HeaderScan_SucceedsNormally_OnceDeepScanIsNoLongerWaiting()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
        limiter.MarkDeepScanWaiting();
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.False(limiter.TryConsume(5, maxRateMbPerSec: 10, ScanPhase.Header));

        limiter.MarkDeepScanNoLongerWaiting();

        Assert.True(limiter.TryConsume(5, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void TryConsume_LoneHeaderScan_IsNeverBlocked_WhenNoDeepScanIsContending()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.True(limiter.TryConsume(10, maxRateMbPerSec: 10, ScanPhase.Header));
    }
}

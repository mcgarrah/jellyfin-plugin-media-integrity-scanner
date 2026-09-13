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
/// Tests for <see cref="SharedBandwidthLimiter"/>'s pure <c>ConsumeUpTo</c> state
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
    public void ConsumeUpTo_ReturnsFullAmount_WhenRateIsZeroOrNegative()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.Equal(1_000_000, limiter.ConsumeUpTo(1_000_000, 0, ScanPhase.Header));
        Assert.Equal(1_000_000, limiter.ConsumeUpTo(1_000_000, -1, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_ReturnsZeroOrTheRequestedAmount_WhenMegabytesIsZeroOrNegative()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.Equal(0, limiter.ConsumeUpTo(0, 10, ScanPhase.Header));
        Assert.Equal(0, limiter.ConsumeUpTo(-5, 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_SucceedsImmediately_ForAFreshLimiter_UpToOneSecondsBudget()
    {
        // The bucket seeds itself at a full second's worth on first use,
        // rather than starting at zero and forcing the very first scan of a
        // session to wait purely because of construction timing.
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_ReturnsZero_WhenBudgetIsAlreadyFullySpent()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        // First call seeds a 10 MB budget and immediately spends all of it.
        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
        // No time has passed, so nothing has refilled -- this must return 0.
        Assert.Equal(0, limiter.ConsumeUpTo(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_ReturnsPartialAmount_WhenRequestExceedsCurrentlyAvailableBudget()
    {
        // Regression test for a real production incident (2026-09-13): the
        // old TryConsume shape required the FULL requested amount to be
        // available atomically, so a request bigger than the one-second-
        // capped budget (see Refill) could never succeed no matter how long
        // the caller waited -- exactly what a real media file's size (almost
        // always far more than a modest MB/s throttle setting) looks like.
        // Two live jellyfin-test instances wedged for 7+ hours with zero
        // progress at MaxReadRateMbPerSec settings of 5 and 10. ConsumeUpTo
        // must instead hand back whatever it can right now.
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        // A single 700 MB file against a 5 MB/s cap: the old code would
        // demand 700 MB be available before proceeding at all, which never
        // happens since Refill caps the budget at 5 MB. The new code must
        // hand back the 5 MB it has right now.
        Assert.Equal(5, limiter.ConsumeUpTo(700, maxRateMbPerSec: 5, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_DrainsALargeFileAcrossRepeatedCalls_WithoutEverBlockingForever()
    {
        // End-to-end regression: a caller that keeps calling ConsumeUpTo with
        // the still-remaining amount (as ScanEngine.ConsumeBandwidthAsync
        // does) must fully drain an arbitrarily large request over enough
        // calls/time, rather than being stuck forever the way a single
        // atomic TryConsume(700, ...) call always was.
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);
        double remaining = 700;

        remaining -= limiter.ConsumeUpTo(remaining, maxRateMbPerSec: 5, ScanPhase.Header);
        Assert.Equal(695, remaining, precision: 3);

        // Simulate the polling loop: each second refills 5 MB more.
        for (var i = 0; i < 139 && remaining > 0; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            remaining -= limiter.ConsumeUpTo(remaining, maxRateMbPerSec: 5, ScanPhase.Header);
        }

        Assert.Equal(0, remaining, precision: 3);
    }

    [Fact]
    public void ConsumeUpTo_Succeeds_OnceEnoughTimeHasElapsedToRefill()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.Equal(0, limiter.ConsumeUpTo(5, maxRateMbPerSec: 10, ScanPhase.Header));

        // At 10 MB/s, 0.5s should refill exactly 5 MB.
        clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.Equal(5, limiter.ConsumeUpTo(5, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_AccrualIsCapped_AtOneSecondsWorth_NoUnboundedBurstCredit()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        // Drain the initial seed, then let a long time pass -- simulating the
        // limiter sitting unused between library sweeps.
        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
        clock.Advance(TimeSpan.FromHours(1));

        // If accrual were unbounded, an hour at 10 MB/s would have built up
        // 36,000 MB of credit. It should be capped at 10 MB (one second's worth).
        Assert.Equal(10, limiter.ConsumeUpTo(11, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_DividesBudget_AcrossConcurrentConsumersRatherThanMultiplyingIt()
    {
        // The whole point of a shared budget: several concurrent "scans" (all
        // calling ConsumeUpTo against the same limiter instance) can never
        // collectively exceed the configured cap, regardless of how many
        // there are -- unlike the old per-file-independent calculation.
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.Equal(4, limiter.ConsumeUpTo(4, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.Equal(4, limiter.ConsumeUpTo(4, maxRateMbPerSec: 10, ScanPhase.Header));
        Assert.Equal(2, limiter.ConsumeUpTo(2, maxRateMbPerSec: 10, ScanPhase.Header));
        // A fourth concurrent consumer, still within the same 1-second window,
        // finds the shared budget already fully spent by the other three.
        Assert.Equal(0, limiter.ConsumeUpTo(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_HeaderScan_BacksOff_WhileADeepScanIsWaiting()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        // Drain the budget so there's genuinely nothing available, then
        // register a Deep scan as waiting on it.
        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
        limiter.MarkDeepScanWaiting();

        // No time has passed, so there's nothing for a Header request to
        // succeed against anyway -- this alone doesn't prove priority yet.
        Assert.Equal(0, limiter.ConsumeUpTo(1, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_HeaderScan_DoesNotDrainBudget_AheadOfAWaitingDeepScan()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
        limiter.MarkDeepScanWaiting();

        // Budget refills to 5 MB -- normally enough for this Header request,
        // but a Deep scan is registered as waiting, so it must back off
        // entirely rather than claiming it first.
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(0, limiter.ConsumeUpTo(5, maxRateMbPerSec: 10, ScanPhase.Header));

        // The Deep scan itself is not subject to that same back-off, and can
        // claim the same budget the Header request was denied.
        Assert.Equal(5, limiter.ConsumeUpTo(5, maxRateMbPerSec: 10, ScanPhase.FullDecode));
    }

    [Fact]
    public void ConsumeUpTo_HeaderScan_SucceedsNormally_OnceDeepScanIsNoLongerWaiting()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SharedBandwidthLimiter(clock);

        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
        limiter.MarkDeepScanWaiting();
        clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(0, limiter.ConsumeUpTo(5, maxRateMbPerSec: 10, ScanPhase.Header));

        limiter.MarkDeepScanNoLongerWaiting();

        Assert.Equal(5, limiter.ConsumeUpTo(5, maxRateMbPerSec: 10, ScanPhase.Header));
    }

    [Fact]
    public void ConsumeUpTo_LoneHeaderScan_IsNeverBlocked_WhenNoDeepScanIsContending()
    {
        var limiter = new SharedBandwidthLimiter(new FakeTimeProvider());

        Assert.Equal(10, limiter.ConsumeUpTo(10, maxRateMbPerSec: 10, ScanPhase.Header));
    }
}

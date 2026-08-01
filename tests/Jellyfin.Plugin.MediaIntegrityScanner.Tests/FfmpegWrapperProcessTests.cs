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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for FfmpegWrapper's internal RunProcessAsync helper. Uses /bin/sh (present
/// on any Linux CI runner or dev environment) instead of real ffmpeg/ffprobe binaries,
/// since RunProcessAsync's process-execution semantics (exit code, stream capture,
/// cancellation) don't depend on what executable is run.
/// </summary>
public class FfmpegWrapperProcessTests
{
    private const string Shell = "/bin/sh";

    [Fact]
    public async Task RunProcessAsync_CapturesExitCodeAndStdout()
    {
        var (exitCode, stdout, _) = await Scanner.FfmpegWrapper.RunProcessAsync(
            Shell, new[] { "-c", "echo hello-world" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello-world", stdout);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesNonZeroExitCode()
    {
        var (exitCode, _, _) = await Scanner.FfmpegWrapper.RunProcessAsync(
            Shell, new[] { "-c", "exit 3" }, CancellationToken.None);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStderr()
    {
        var (_, _, stderr) = await Scanner.FfmpegWrapper.RunProcessAsync(
            Shell, new[] { "-c", "echo error-message 1>&2" }, CancellationToken.None);

        Assert.Contains("error-message", stderr);
    }

    [Fact]
    public async Task RunProcessAsync_PassesMultipleArguments()
    {
        var (exitCode, stdout, _) = await Scanner.FfmpegWrapper.RunProcessAsync(
            Shell, new[] { "-c", "echo \"$1 $2\"", "_", "foo", "bar" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("foo bar", stdout);
    }

    [Fact]
    public async Task RunProcessAsync_LargeStderrOutput_DoesNotDeadlock()
    {
        // Regression test for the pipe-buffer deadlock this method was rewritten to avoid:
        // reading stdout/stderr sequentially after WaitForExitAsync can hang once the OS
        // pipe buffer fills, if the child is still writing to the other stream.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (exitCode, _, stderr) = await Scanner.FfmpegWrapper.RunProcessAsync(
            Shell, new[] { "-c", "yes err | head -c 2000000 >&2" }, cts.Token);

        Assert.Equal(0, exitCode);
        Assert.True(stderr.Length > 1_000_000, $"Expected ~2MB of stderr, got {stderr.Length} bytes");
    }

    [Fact]
    public async Task RunProcessAsync_Cancellation_KillsProcessPromptly()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Scanner.FfmpegWrapper.RunProcessAsync(Shell, new[] { "-c", "sleep 30" }, cts.Token));

        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Expected prompt cancellation, took {sw.Elapsed}");
    }
}

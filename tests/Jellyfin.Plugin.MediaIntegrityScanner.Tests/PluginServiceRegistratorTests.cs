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
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaIntegrityScanner.ArrIntegration;
using Jellyfin.Plugin.MediaIntegrityScanner.Data;
using Jellyfin.Plugin.MediaIntegrityScanner.EventHandlers;
using Jellyfin.Plugin.MediaIntegrityScanner.Scanner;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for PluginServiceRegistrator, the plugin's DI wiring. The most
/// important thing this class guards against is a regression of the
/// duplicate-registration bug fixed in PR #12 -- it inspects the raw
/// ServiceDescriptor list (not just "does resolution work"), since a
/// duplicate that happens to resolve fine via last-registration-wins would
/// otherwise hide the bug. It also verifies IDatabaseManager really forwards
/// to the same SqliteDatabaseManager singleton, by resolving both from a
/// real, fully-wired ServiceProvider rather than asserting on internals.
/// </summary>
public class PluginServiceRegistratorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("mis-registrator-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite may still hold a brief file handle on some platforms.
        }
    }

    /// <summary>
    /// Registers the plugin's services into a fresh collection, without building
    /// a provider -- enough to inspect the raw descriptor list for duplicates
    /// and lifetimes.
    /// </summary>
    private static IServiceCollection CreateRegisteredServices()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        return services;
    }

    /// <summary>
    /// Same as <see cref="CreateRegisteredServices"/>, plus the Jellyfin host
    /// dependencies <see cref="SqliteDatabaseManager"/> needs, so
    /// IDatabaseManager's forwarding registration can actually resolve.
    ///
    /// Deliberately does NOT wire up enough to resolve IScanEngine/LibraryMonitor
    /// for real: FfmpegWrapper's constructor eagerly calls
    /// FfmpegResolver.ResolveFfmpegPath(), which walks real filesystem paths and
    /// depends on ffmpeg actually being installed on whatever machine runs the
    /// tests -- exactly the kind of environment-dependent resolution
    /// FfmpegResolverTests already avoids testing end-to-end. Registration
    /// shape (no duplicates, correct lifetimes, hosted-service descriptor) is
    /// covered above without needing to fully resolve that chain.
    /// </summary>
    private ServiceProvider CreateProvider()
    {
        var services = CreateRegisteredServices();

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_tempDir);

        services.AddSingleton(appPaths.Object);
        services.AddSingleton<ILogger<SqliteDatabaseManager>>(NullLogger<SqliteDatabaseManager>.Instance);

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(typeof(FfmpegResolver))]
    [InlineData(typeof(FfmpegWrapper))]
    [InlineData(typeof(IScanEngine))]
    [InlineData(typeof(SqliteDatabaseManager))]
    [InlineData(typeof(IDatabaseManager))]
    [InlineData(typeof(IArrClientFactory))]
    [InlineData(typeof(IArrItemMatcher))]
    [InlineData(typeof(IArrRemediationService))]
    public void RegisterServices_RegistersEachServiceExactlyOnce(Type serviceType)
    {
        var services = CreateRegisteredServices();

        var count = services.Count(d => d.ServiceType == serviceType);

        Assert.True(count == 1, $"Expected exactly one registration for {serviceType.Name}, found {count}.");
    }

    [Theory]
    [InlineData(typeof(FfmpegResolver))]
    [InlineData(typeof(FfmpegWrapper))]
    [InlineData(typeof(IScanEngine))]
    [InlineData(typeof(SqliteDatabaseManager))]
    [InlineData(typeof(IDatabaseManager))]
    [InlineData(typeof(IArrClientFactory))]
    [InlineData(typeof(IArrItemMatcher))]
    [InlineData(typeof(IArrRemediationService))]
    public void RegisterServices_RegistersAsSingletonLifetime(Type serviceType)
    {
        var services = CreateRegisteredServices();

        var descriptor = services.Single(d => d.ServiceType == serviceType);

        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void RegisterServices_RegistersLibraryMonitorAsAHostedService()
    {
        var services = CreateRegisteredServices();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(LibraryMonitor));
    }

    [Fact]
    public void RegisterServices_RegistersArrRemediationWorkerAsAHostedService()
    {
        var services = CreateRegisteredServices();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ArrRemediationWorker));
    }

    [Fact]
    public void RegisterServices_IDatabaseManagerResolvesToTheSameSqliteInstance()
    {
        using var provider = CreateProvider();

        var concrete = provider.GetRequiredService<SqliteDatabaseManager>();
        var viaInterface = provider.GetRequiredService<IDatabaseManager>();

        Assert.Same(concrete, viaInterface);
    }
}

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
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrityScanner.Tests;

/// <summary>
/// Tests for the Plugin entry point: page registration and the assembly's
/// embedded web resources. <see cref="MediaBrowser.Common.Plugins.BasePlugin{T}"/>'s
/// constructor only builds a data-folder path string from
/// <see cref="IApplicationPaths.PluginsPath"/> -- it never touches disk or the
/// XML serializer -- so a real <see cref="Plugin"/> instance can be built here
/// with bare mocks. That constructor's own real side effect (<c>Instance = this</c>)
/// is exactly the process-wide static <see cref="TestPluginContext"/> exists to
/// guard -- decorated with [Collection("PluginInstance")] for the same reason
/// every other class using that static is, even though this class reaches
/// Plugin.Instance via the real constructor rather than TestPluginContext itself.
/// </summary>
[Collection("PluginInstance")]
public class PluginTests : IDisposable
{
    public void Dispose() => TestPluginContext.Clear();

    private static Plugin CreatePlugin()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(Path.Combine(Path.GetTempPath(), "fake-plugins"));
        return new Plugin(appPaths.Object, Mock.Of<IXmlSerializer>());
    }

    [Fact]
    public void GetPages_ReturnsExactlyThreePages_DashboardIssuesAndSettings()
    {
        var pages = CreatePlugin().GetPages().ToList();

        Assert.Equal(3, pages.Count);
        Assert.Contains(pages, p => p.Name == "Media Integrity Scanner");
        Assert.Contains(pages, p => p.Name == "Media Issues");
        Assert.Contains(pages, p => p.Name == "Media Integrity Scanner Settings");
    }

    [Fact]
    public void GetPages_DashboardPage_IsInMainMenuWithExpectedIcon()
    {
        var dashboardPage = CreatePlugin().GetPages().Single(p => p.Name == "Media Integrity Scanner");

        Assert.True(dashboardPage.EnableInMainMenu);
        Assert.Equal("fact_check", dashboardPage.MenuIcon);
    }

    [Fact]
    public void GetPages_MainMenuPages_EachHaveADistinctExplicitDisplayName()
    {
        // Regression test for a real bug found live (2026-08-23): Jellyfin's
        // own ConfigurationPageInfo (server-side) falls back to the *plugin's*
        // Name whenever a page's own DisplayName is unset -- silently making
        // every main-menu page from this plugin show identical sidebar text
        // regardless of each page's distinct Name (Name only affects the
        // page's URL slug). Both main-menu pages must set DisplayName
        // explicitly, and it must actually differ between them.
        var mainMenuPages = CreatePlugin().GetPages().Where(p => p.EnableInMainMenu).ToList();

        Assert.All(mainMenuPages, p => Assert.False(string.IsNullOrWhiteSpace(p.DisplayName)));

        var distinctDisplayNames = mainMenuPages.Select(p => p.DisplayName).Distinct().Count();
        Assert.Equal(mainMenuPages.Count, distinctDisplayNames);
    }

    [Fact]
    public void GetPages_IssuesPage_IsInMainMenuWithExpectedIcon()
    {
        // A top-level nav entry on purpose (ARR-INTEGRATION-PROPOSAL.md
        // section 8.1) -- the whole point is making it easy to find, not one
        // click deeper than the main dashboard.
        var issuesPage = CreatePlugin().GetPages().Single(p => p.Name == "Media Issues");

        Assert.True(issuesPage.EnableInMainMenu);
        Assert.Equal("healing", issuesPage.MenuIcon);
    }

    [Fact]
    public void GetPages_SettingsPage_IsNotInMainMenu()
    {
        // The settings page is reached from the dashboard/plugin-details page,
        // not surfaced as its own top-level nav entry.
        var settingsPage = CreatePlugin().GetPages().Single(p => p.Name == "Media Integrity Scanner Settings");

        Assert.False(settingsPage.EnableInMainMenu);
    }

    [Fact]
    public void GetPages_EveryEmbeddedResourcePath_ActuallyExistsInTheAssembly()
    {
        // A renamed .html file with GetPages() left pointing at the old name is a
        // real, easy-to-make Jellyfin plugin mistake -- it compiles fine and only
        // fails at runtime with a 404 on the plugin's own dashboard page. Checking
        // against the assembly's real manifest resource names catches it in CI.
        var pages = CreatePlugin().GetPages();
        var resourceNames = typeof(Plugin).Assembly.GetManifestResourceNames();

        foreach (var page in pages)
        {
            Assert.Contains(page.EmbeddedResourcePath, resourceNames);
        }
    }

    [Fact]
    public void Id_MatchesThePluginIdHardcodedInTheSettingsPageJavaScript()
    {
        // integrity_settings.html independently hardcodes this same GUID (dashless,
        // matching how Jellyfin's 10.11.11 REST responses serialize it) as its own
        // PLUGIN_ID constant, since the settings page's JS has no way to ask the
        // server "what is your own plugin ID" -- there is no code-level link between
        // Plugin.Id and that JS literal, so nothing else would catch it drifting.
        var plugin = CreatePlugin();
        var settingsPageInfo = plugin.GetPages().Single(p => p.Name == "Media Integrity Scanner Settings");

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(settingsPageInfo.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        Assert.Contains($"'{plugin.Id:N}'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_IsTheFixedExpectedGuid()
    {
        // This GUID must never change once released -- Jellyfin uses it to track
        // installed-plugin identity across upgrades. Guid.Parse throwing on a typo
        // here would be a real (if unlikely) way to lose that stability silently.
        Assert.Equal(Guid.Parse("c8f4a3b2-1d5e-4f6a-9b7c-2e8d0f1a3b5c"), CreatePlugin().Id);
    }

    [Fact]
    public void NameAndDescription_AreNonEmpty()
    {
        var plugin = CreatePlugin();

        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.False(string.IsNullOrWhiteSpace(plugin.Description));
    }
}

using System;
using System.IO;
using FlaUI.Core;
using FlaUI.UIA3;
using FluentAssertions;
using Xunit;

namespace Rig.Wpf.UiTests;

/// <summary>
/// Tests FlaUI sur Rig.Wpf.Shell.exe. Catégorisés <c>Ui</c> pour être exclus
/// des builds CI sans session interactive.
/// </summary>
[Trait("Category", "Ui")]
public class ShellSmokeTests
{
    private static string ShellExePath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\Rig.Wpf.Shell.exe"));

    [SkippableFact]
    public void ShellExe_Launches_AndShowsMainWindow()
    {
        Skip.IfNot(File.Exists(ShellExePath),
            $"Rig.Wpf.Shell.exe absent — build le projet d'abord. ({ShellExePath})");

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
            window.Should().NotBeNull();
            window!.Title.Should().Contain("RIG");
        }
        finally
        {
            try { app?.Close(); } catch { /* best-effort */ }
            try { app?.Dispose(); } catch { /* best-effort */ }
        }
    }
}

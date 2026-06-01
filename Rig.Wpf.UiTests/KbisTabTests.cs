using System;
using System.IO;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using FluentAssertions;
using Xunit;

namespace Rig.Wpf.UiTests;

[Trait("Category", "Ui")]
public class KbisTabTests
{
    private static string ShellExePath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\Rig.Wpf.Shell.exe"));

    private static string ShellAppSettingsPath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\appsettings.json"));

    private const string KbisOnlyConfig =
        "{\"RigWpf\":{"
        + "\"PluginDirectory\":\"C:\\\\rig\\\\Bin Processus\","
        + "\"DisableNativeFor\":[],"
        + "\"PreOpenTabs\":[\"KBIS\"],"
        + "\"UseLegacyRigMetier\":true,"
        + "\"UseSqlRigMetier\":true,"
        + "\"SqlConnectionString\":\"Server=SQL-DEV\\\\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=5;\","
        + "\"DefaultGreffe\":\"7401\""
        + "}}";

    [SkippableFact]
    public void KbisTab_SearchSociete_ShowsResults()
    {
        Skip.IfNot(File.Exists(ShellExePath), "Rig.Wpf.Shell.exe absent — build d'abord.");

        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath, KbisOnlyConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15));

            System.Threading.Thread.Sleep(2000);

            var textBox = window!.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Select(t => t.AsTextBox())
                .FirstOrDefault();
            textBox.Should().NotBeNull("une TextBox de recherche doit être présente");
            textBox!.Enter("A");

            System.Threading.Thread.Sleep(1500);

            var listItems = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            listItems.Length.Should().BeGreaterThan(0,
                "au moins 1 société commençant par 'A' devrait apparaitre dans RIG_DEV");

            app.HasExited.Should().BeFalse();
        }
        finally
        {
            try { app?.Close(); } catch { }
            try { app?.Dispose(); } catch { }
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }
    }
}

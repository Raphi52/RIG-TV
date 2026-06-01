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
public class TestnlhTabTests
{
    private static string ShellExePath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\Rig.Wpf.Shell.exe"));

    private static string ShellAppSettingsPath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\appsettings.json"));

    // Force PreOpenTabs=["TESTNLH"] le temps du test. Pattern : sauvegarde
    // l'original avant le lancement, restaure dans le finally — pour ne pas
    // dépendre de la config par défaut du shell qui peut changer.
    private const string TestnlhOnlyConfig =
        "{\"RigWpf\":{"
        + "\"PluginDirectory\":\"C:\\\\rig\\\\Bin Processus\","
        + "\"DisableNativeFor\":[],"
        + "\"PreOpenTabs\":[\"TESTNLH\"],"
        + "\"UseLegacyRigMetier\":true,"
        + "\"DefaultGreffe\":\"7401\""
        + "}}";

    [SkippableFact]
    public void TestnlhTab_OpensNatively_AndShowsLibelle()
    {
        Skip.IfNot(File.Exists(ShellExePath),
            $"Rig.Wpf.Shell.exe absent — build le projet d'abord. ({ShellExePath})");

        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath, TestnlhOnlyConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
            window.Should().NotBeNull();

            System.Threading.Thread.Sleep(800);

            var allTexts = window!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            var found = allTexts.Any(t =>
                !string.IsNullOrEmpty(t.Name) && t.Name.Contains("Test NLH"));

            found.Should().BeTrue(
                "le Processus TESTNLH natif doit afficher son libellé 'Test NLH (pilote WPF)'");
        }
        finally
        {
            try { app?.Close(); } catch { /* best-effort */ }
            try { app?.Dispose(); } catch { /* best-effort */ }
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }
    }

    [SkippableFact]
    public void TestnlhTab_SaveButton_StartsDisabled_AndEnablesAfterTyping()
    {
        Skip.IfNot(File.Exists(ShellExePath),
            $"Rig.Wpf.Shell.exe absent — build le projet d'abord. ({ShellExePath})");

        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath, TestnlhOnlyConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));

            System.Threading.Thread.Sleep(800);

            var saveButton = window!
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name == "Enregistrer")
                ?.AsButton();
            saveButton.Should().NotBeNull("le bouton 'Enregistrer' doit être présent");
            saveButton!.IsEnabled.Should().BeFalse("Save désactivé tant que le message est vide");

            var textBox = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .FirstOrDefault()
                ?.AsTextBox();
            textBox.Should().NotBeNull();
            textBox!.Focus();
            textBox.Enter("Bonjour");

            saveButton.IsEnabled.Should().BeTrue(
                "Save activé après saisie d'un message non vide");
        }
        finally
        {
            try { app?.Close(); } catch { /* best-effort */ }
            try { app?.Dispose(); } catch { /* best-effort */ }
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }
    }
}

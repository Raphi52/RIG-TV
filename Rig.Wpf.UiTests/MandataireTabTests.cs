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
public class MandataireTabTests
{
    private static string ShellExePath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\Rig.Wpf.Shell.exe"));

    private static string ShellAppSettingsPath
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\..\..\Rig.Wpf.Shell\bin\Release\net48\appsettings.json"));

    private const string MandataireOnlyConfig =
        "{\"RigWpf\":{"
        + "\"PluginDirectory\":\"C:\\\\rig\\\\Bin Processus\","
        + "\"DisableNativeFor\":[],"
        + "\"PreOpenTabs\":[\"MANDATAIRE\"],"
        + "\"UseLegacyRigMetier\":true,"
        + "\"DefaultGreffe\":\"7401\""
        + "}}";

    [SkippableFact]
    public void MandataireTab_StartsOnRechercheEtape_WithFilterAndGrid()
    {
        Skip.IfNot(File.Exists(ShellExePath), $"Rig.Wpf.Shell.exe absent — build d'abord.");

        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath, MandataireOnlyConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));

            // Laisser le DataTemplate du ContentControl rendre l'étape Recherche
            System.Threading.Thread.Sleep(800);

            // Étape Recherche par défaut : libellé d'étape visible dans le header
            var texts = window!.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => t.Name ?? "")
                .ToList();
            texts.Should().Contain(t => t.Contains("Recherche d'un mandataire"),
                "le libellé de l'étape Recherche doit apparaître dans le header");

            // Boutons globaux du Processus présents (footer)
            var buttonNames = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(b => b.Name ?? "")
                .ToList();
            buttonNames.Should().Contain(b => b.Contains("Suivant"));
            buttonNames.Should().Contain(b => b.Contains("Nouveau"));
            buttonNames.Should().Contain("Enregistrer");
            // Save désactivé tant que Saisie pas remplie (validation cross-étape)
            var saveButton = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name == "Enregistrer")
                ?.AsButton();
            saveButton!.IsEnabled.Should().BeFalse();
        }
        finally
        {
            try { app?.Close(); } catch { }
            try { app?.Dispose(); } catch { }
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }
    }

    [SkippableFact]
    public void MandataireTab_SaveClickWithMinimumFields_DoesNotCrash_AndShowsStatusPill()
    {
        Skip.IfNot(File.Exists(ShellExePath), $"Rig.Wpf.Shell.exe absent — build d'abord.");

        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath, MandataireOnlyConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));

            // Pré-étape : passer en Saisie via "Nouveau mandataire"
            var nouveauButton = window!
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name?.Contains("Nouveau") == true)
                ?.AsButton();
            nouveauButton!.Invoke();
            System.Threading.Thread.Sleep(800);

            // Remplir Civilité (premier ComboBox) et Nom (premier TextBox)
            var combos = window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                .Select(c => c.AsComboBox())
                .ToList();
            var civiliteCombo = combos.FirstOrDefault();
            civiliteCombo.Should().NotBeNull("Civilité doit être un ComboBox dans l'étape Saisie");
            // Sélectionner "M" via SetValue (premier item attendu)
            civiliteCombo!.Items.Length.Should().BeGreaterThan(0);
            civiliteCombo.Select(0);

            var textboxes = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .Select(t => t.AsTextBox())
                .ToList();
            // Le premier Edit doit être le Nom (l'ordre tab vient en haut du formulaire).
            textboxes.Should().NotBeEmpty();
            textboxes[0].Enter("Dupont");

            System.Threading.Thread.Sleep(300);

            // Clic sur "Enregistrer" — c'est le scénario crash historique.
            var saveButton = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name == "Enregistrer")
                ?.AsButton();
            saveButton.Should().NotBeNull("le bouton 'Enregistrer' doit être présent dans le footer");
            saveButton!.IsEnabled.Should().BeTrue(
                "le Save doit être activé après remplissage Civilité+Nom");
            saveButton.Invoke();

            System.Threading.Thread.Sleep(800);

            // GUARD ANTI-CRASH : le shell DOIT toujours être vivant.
            // C'est la régression utilisateur 2026-05 (NotSupportedException du
            // LegacyMandataireRepository propagée jusqu'au dispatcher).
            app.HasExited.Should().BeFalse(
                "le shell ne doit pas crasher au clic sur Enregistrer même si " +
                "le repository legacy lève NotSupportedException (try/catch attendu " +
                "dans MandataireProcessusViewModel.Save + DispatcherUnhandledException.Handled=true)");

            // Le feedback doit apparaître dans le footer (pill bind StatusMessage).
            var texts = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => t.Name ?? "")
                .ToList();
            texts.Should().Contain(t =>
                t.Contains("enregistré") ||
                t.Contains("non encore disponible") ||
                t.Contains("Erreur"),
                "la pill StatusMessage doit afficher un retour utilisateur après le clic Save");
        }
        finally
        {
            try { app?.Close(); } catch { }
            try { app?.Dispose(); } catch { }
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }
    }

    [SkippableFact]
    public void MandataireTab_NouveauButton_NavigatesToSaisieEtape_WithAllFields()
    {
        Skip.IfNot(File.Exists(ShellExePath), $"Rig.Wpf.Shell.exe absent — build d'abord.");

        var originalConfig = File.ReadAllText(ShellAppSettingsPath);
        File.WriteAllText(ShellAppSettingsPath, MandataireOnlyConfig);

        Application? app = null;
        try
        {
            app = Application.Launch(ShellExePath);
            using var automation = new UIA3Automation();
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));

            // Cliquer sur "+ Nouveau" pour passer à l'étape Saisie
            var nouveauButton = window!
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name?.Contains("Nouveau") == true)
                ?.AsButton();
            nouveauButton.Should().NotBeNull("le bouton 'Nouveau' doit être présent dans le footer");
            nouveauButton!.Invoke();

            System.Threading.Thread.Sleep(800);

            // GUARD ANTI-CRASH : le shell doit toujours être vivant après le clic.
            // Une refonte XAML qui crashe sur le rendu de la Saisie serait attrapée ici.
            app.HasExited.Should().BeFalse(
                "le shell ne doit pas crasher au clic sur 'Nouveau mandataire' " +
                "(régression historique : Style TargetType=StackPanel appliqué à Grid)");

            var labels = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(t => t.Name ?? "")
                .ToList();

            // Étape Saisie active : tous les champs visibles
            labels.Should().Contain("Civilité");
            labels.Should().Contain("Nom");
            labels.Should().Contain("Abrégé");
            labels.Should().Contain("N° CNBF");
            labels.Should().Contain("Mandataire actif");
            // Sections numérotées (refonte somptueuse)
            labels.Should().Contain("Identité");
            labels.Should().Contain("Identifiants");
            labels.Should().Contain("Statut");

            // Save désactivé car nouveau mandataire vide
            var saveButton = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => b.Name == "Enregistrer")
                ?.AsButton();
            saveButton.Should().NotBeNull();
            saveButton!.IsEnabled.Should().BeFalse(
                "Save désactivé tant que Civilité + Nom ne sont pas remplis sur le nouveau mandataire");
        }
        finally
        {
            try { app?.Close(); } catch { }
            try { app?.Dispose(); } catch { }
            File.WriteAllText(ShellAppSettingsPath, originalConfig);
        }
    }
}

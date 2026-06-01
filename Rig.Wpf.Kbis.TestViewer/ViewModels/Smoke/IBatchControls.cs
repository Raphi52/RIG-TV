// SPDX-License-Identifier: Proprietary
// SmokePage template — interface bandeau actions batch (Phase 1).

using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// Bandeau d'actions au-dessus de la zone 3-colonnes (Card header).
/// Contient : sélecteur scénario (combo + bouton refresh), options batch
/// (Apply réel, Mode dropdown, Parallelism), commandes batch (Run all,
/// Pause, Stop, Reset DB, Historique), preview CLI.
///
/// Chaque module (Rapture/KBIS/IPE) expose son propre IBatchControls avec
/// ses options spécifiques :
///   - Rapture : Apply réel + mode (A/B/C/Selfdrive) + parallelism 1-32
///   - KBIS    : pas d'Apply réel, mode différent, parallelism plus restreint
///
/// Pour cacher un contrôle sur un module donné, expose Visible=false sur le flag dédié.
/// </summary>
public interface IBatchControls : INotifyPropertyChanged
{
    // ── Sélection scénario ──────────────────────────────────────────────────

    /// <summary>Description courte sous le titre de la page (ex. "Exécute les 16 scénarios…").
    /// Affichée comme sous-titre du Card header. Peut contenir du markup léger.</summary>
    string? Description { get; }

    /// <summary>Catalogue statique des scénarios disponibles (depuis manifest.json typiquement).
    /// Distinct de IScenarioCatalog.ScenariosView qui contient les états RUNTIME.
    /// Utilisé pour peupler le combobox de sélection.</summary>
    IReadOnlyList<IScenarioDescriptor> AvailableScenarios { get; }

    /// <summary>Scénario sélectionné dans le combobox (peut être le sentinel "All scenarios").
    /// Bind TwoWay sur ComboBox.SelectedItem.</summary>
    IScenarioDescriptor? SelectedScenarioDescriptor { get; set; }

    /// <summary>Recharge la liste des scénarios depuis le disque (refresh manifest.json).
    /// Bouton 🔄 à côté du combobox.</summary>
    ICommand ReloadScenariosCommand { get; }

    // ── Options batch ───────────────────────────────────────────────────────

    /// <summary>Checkbox "Apply réel via UI" — si cochée, les workers cliquent vraiment
    /// "Importer Rapture" + restore SQL net-zero. Sinon dry-run / self-drive.
    /// Cacher (Visibility) si non applicable au module (KBIS read-only par ex.).</summary>
    bool ApplyReal { get; set; }

    /// <summary>True = affiche le bloc Apply réel. False = caché (module sans modif RIG).</summary>
    bool ApplyRealVisible { get; }

    /// <summary>Label affiché à côté du checkbox Apply réel. Permet de personnaliser
    /// la sémantique (ex. "Apply réel via UI" pour Rapture, "Écrire en base" pour autre).</summary>
    string? ApplyRealLabel { get; }

    /// <summary>Liste des modes disponibles (ex. ["A — desktop user", "B — HDESK self-snap",
    /// "C — Desktop 2 Sysinternals", "Selfdrive"]). Drive le ComboBox Mode.</summary>
    IReadOnlyList<string> ModeOptions { get; }

    /// <summary>Mode sélectionné. Bind TwoWay sur le ComboBox.</summary>
    string? SelectedMode { get; set; }

    /// <summary>Parallelism (nb workers //). Min 1, Max 32. Bind TwoWay sur NumericUpDown
    /// ou TextBox numeric.</summary>
    int Parallelism { get; set; }

    /// <summary>Preview de la commande CLI équivalente affichée sous la card header.
    /// Auto-calculée à partir de SelectedScenarioDescriptor + ApplyReal + Mode + Parallelism.
    /// Vide si pas de scénario sélectionné.</summary>
    string? PreviewCliCommand { get; }

    // ── Commandes batch ─────────────────────────────────────────────────────

    /// <summary>Lance le batch (Run all / Start E2E). Bind au bouton ▶ Start E2E.
    /// CanExecute selon état (pas déjà en cours, scénario sélectionné, etc.).</summary>
    ICommand RunBatchCommand { get; }

    /// <summary>Pause le batch (toggle). Bind au bouton ⏸ Pause.</summary>
    ICommand PauseBatchCommand { get; }

    /// <summary>Stoppe le batch en cours (kill tous les workers). Bind au bouton ⏹ Stop.</summary>
    ICommand StopBatchCommand { get; }

    /// <summary>Reset DB : restore SQL net-zero (delete AUDIT rows, restore APPEL_AFFAIRE
    /// modifs, etc.). Bind au bouton ⚠ Reset DB. Demande confirmation modale.</summary>
    ICommand ResetDbCommand { get; }

    /// <summary>Ouvre le drawer historique (RunHistoryDrawer). Bind au bouton 🕘 Historique.</summary>
    ICommand OpenHistoryCommand { get; }
}

/// <summary>
/// Descripteur statique d'un scénario (depuis manifest.json) — pas le state runtime.
/// Utilisé pour peupler le ComboBox de sélection. Pour KBIS / IPE etc., chaque module
/// fournit sa propre implémentation.
/// </summary>
public interface IScenarioDescriptor
{
    /// <summary>Id stable (ex. "cas-a-subset" ou le sentinel "_all-scenarios").</summary>
    string Id { get; }

    /// <summary>Label affiché dans le ComboBox (ex. "▶ All scenarios (16)" ou "cas-a-subset").</summary>
    string DisplayName { get; }

    /// <summary>True si c'est le sentinel "tous les scénarios". Drive la logique "lancer la suite complète".</summary>
    bool IsAllScenariosSentinel { get; }

    /// <summary>True si ce scénario est marqué broken/skipped dans le manifest. Greyed dans le combo.</summary>
    bool IsBroken { get; }
}

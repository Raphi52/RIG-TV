// SPDX-License-Identifier: Proprietary
// SmokeHeaderView — bandeau d'actions UNIFIÉ pour tous les modules smoke (KBIS / ALERTES / DCADEMAT / RAPTURE).
// Chaque section est pilotée par un DependencyProperty capability-flag ; les sections non-actives
// sont en Visibility=Collapsed (BoolToVisibilityConverter).
//
// DPs statiques (bool flags, default false sauf mention) :
//   ShowScenarioPicker, ShowAdvancedOverrides, ShowApplyReal
//   ShowModeSelector, ShowParallelism, ShowStress
//   ShowResetDb, ShowHistory, ShowPause, ShowStop
// DPs dynamiques (passés par l'usage-site) :
//   PrimaryRunCommand, PrimaryRunLabel, PrimaryRunAutomationId
//   ModeOptionsSource, SelectedMode, ModeComboAutomationId
//   StressCommand, StopStressCommand, StressRepeat, StressLoopUntilFail
//   StressStatus, StressRunAutomationId, StressStopAutomationId
//   StressRepeatAutomationId, StressLoopAutomationId
//   IsFullscreen (masque le header entier en plein écran)
//
// Bindings RAPTURE (scénario picker + Advanced + Apply réel + Parallelism + Historique + Pause + Stop) :
//   Ces bindings partent vers le DataContext (MainWindowViewModel) car BatchControlsView les liait ainsi.

using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.Views;

/// <summary>
/// Bandeau d'actions unifié pour tous les modules smoke (KBIS / ALERTES / DCADEMAT / RAPTURE).
/// Les DependencyProperties capability-flags pilotent la visibilité de chaque section.
/// </summary>
public partial class SmokeHeaderView : UserControl
{
    public SmokeHeaderView()
    {
        InitializeComponent();
    }

    // ── Fullscreen (header se cache en plein écran) ──────────────────────────

    public static readonly DependencyProperty IsFullscreenProperty =
        DependencyProperty.Register(nameof(IsFullscreen), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        set => SetValue(IsFullscreenProperty, value);
    }

    // ── Titre / description / statuts ────────────────────────────────────────

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata("Smoke"));
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty ExeStatusProperty =
        DependencyProperty.Register(nameof(ExeStatus), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string ExeStatus
    {
        get => (string)GetValue(ExeStatusProperty);
        set => SetValue(ExeStatusProperty, value);
    }

    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.Register(nameof(Summary), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    // ── Primary Run button ───────────────────────────────────────────────────

    public static readonly DependencyProperty PrimaryRunCommandProperty =
        DependencyProperty.Register(nameof(PrimaryRunCommand), typeof(ICommand), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public ICommand PrimaryRunCommand
    {
        get => (ICommand)GetValue(PrimaryRunCommandProperty);
        set => SetValue(PrimaryRunCommandProperty, value);
    }

    public static readonly DependencyProperty PrimaryRunLabelProperty =
        DependencyProperty.Register(nameof(PrimaryRunLabel), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata("▶  Run"));
    public string PrimaryRunLabel
    {
        get => (string)GetValue(PrimaryRunLabelProperty);
        set => SetValue(PrimaryRunLabelProperty, value);
    }

    public static readonly DependencyProperty PrimaryRunAutomationIdProperty =
        DependencyProperty.Register(nameof(PrimaryRunAutomationId), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string PrimaryRunAutomationId
    {
        get => (string)GetValue(PrimaryRunAutomationIdProperty);
        set => SetValue(PrimaryRunAutomationIdProperty, value);
    }

    // ── Capability flags ─────────────────────────────────────────────────────

    public static readonly DependencyProperty ShowScenarioPickerProperty =
        DependencyProperty.Register(nameof(ShowScenarioPicker), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowScenarioPicker
    {
        get => (bool)GetValue(ShowScenarioPickerProperty);
        set => SetValue(ShowScenarioPickerProperty, value);
    }

    public static readonly DependencyProperty ShowAdvancedOverridesProperty =
        DependencyProperty.Register(nameof(ShowAdvancedOverrides), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowAdvancedOverrides
    {
        get => (bool)GetValue(ShowAdvancedOverridesProperty);
        set => SetValue(ShowAdvancedOverridesProperty, value);
    }

    public static readonly DependencyProperty ShowApplyRealProperty =
        DependencyProperty.Register(nameof(ShowApplyReal), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowApplyReal
    {
        get => (bool)GetValue(ShowApplyRealProperty);
        set => SetValue(ShowApplyRealProperty, value);
    }

    public static readonly DependencyProperty ShowModeSelectorProperty =
        DependencyProperty.Register(nameof(ShowModeSelector), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowModeSelector
    {
        get => (bool)GetValue(ShowModeSelectorProperty);
        set => SetValue(ShowModeSelectorProperty, value);
    }

    public static readonly DependencyProperty ShowParallelismProperty =
        DependencyProperty.Register(nameof(ShowParallelism), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowParallelism
    {
        get => (bool)GetValue(ShowParallelismProperty);
        set => SetValue(ShowParallelismProperty, value);
    }

    public static readonly DependencyProperty ShowStressProperty =
        DependencyProperty.Register(nameof(ShowStress), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowStress
    {
        get => (bool)GetValue(ShowStressProperty);
        set => SetValue(ShowStressProperty, value);
    }

    public static readonly DependencyProperty ShowResetDbProperty =
        DependencyProperty.Register(nameof(ShowResetDb), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowResetDb
    {
        get => (bool)GetValue(ShowResetDbProperty);
        set => SetValue(ShowResetDbProperty, value);
    }

    public static readonly DependencyProperty ShowHistoryProperty =
        DependencyProperty.Register(nameof(ShowHistory), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowHistory
    {
        get => (bool)GetValue(ShowHistoryProperty);
        set => SetValue(ShowHistoryProperty, value);
    }

    public static readonly DependencyProperty ShowPauseProperty =
        DependencyProperty.Register(nameof(ShowPause), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowPause
    {
        get => (bool)GetValue(ShowPauseProperty);
        set => SetValue(ShowPauseProperty, value);
    }

    public static readonly DependencyProperty ShowStopProperty =
        DependencyProperty.Register(nameof(ShowStop), typeof(bool), typeof(SmokeHeaderView),
            new PropertyMetadata(false));
    public bool ShowStop
    {
        get => (bool)GetValue(ShowStopProperty);
        set => SetValue(ShowStopProperty, value);
    }

    // ── Mode selector ────────────────────────────────────────────────────────

    public static readonly DependencyProperty ModeOptionsSourceProperty =
        DependencyProperty.Register(nameof(ModeOptionsSource), typeof(IEnumerable), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public IEnumerable ModeOptionsSource
    {
        get => (IEnumerable)GetValue(ModeOptionsSourceProperty);
        set => SetValue(ModeOptionsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedModeProperty =
        DependencyProperty.Register(nameof(SelectedMode), typeof(object), typeof(SmokeHeaderView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public object SelectedMode
    {
        get => GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    public static readonly DependencyProperty ModeComboAutomationIdProperty =
        DependencyProperty.Register(nameof(ModeComboAutomationId), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string ModeComboAutomationId
    {
        get => (string)GetValue(ModeComboAutomationIdProperty);
        set => SetValue(ModeComboAutomationIdProperty, value);
    }

    // ── Stress ───────────────────────────────────────────────────────────────

    public static readonly DependencyProperty StressCommandProperty =
        DependencyProperty.Register(nameof(StressCommand), typeof(ICommand), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public ICommand StressCommand
    {
        get => (ICommand)GetValue(StressCommandProperty);
        set => SetValue(StressCommandProperty, value);
    }

    public static readonly DependencyProperty StopStressCommandProperty =
        DependencyProperty.Register(nameof(StopStressCommand), typeof(ICommand), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public ICommand StopStressCommand
    {
        get => (ICommand)GetValue(StopStressCommandProperty);
        set => SetValue(StopStressCommandProperty, value);
    }

    public static readonly DependencyProperty StressRepeatProperty =
        DependencyProperty.Register(nameof(StressRepeat), typeof(int), typeof(SmokeHeaderView),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public int StressRepeat
    {
        get => (int)GetValue(StressRepeatProperty);
        set => SetValue(StressRepeatProperty, value);
    }

    public static readonly DependencyProperty StressLoopUntilFailProperty =
        DependencyProperty.Register(nameof(StressLoopUntilFail), typeof(bool), typeof(SmokeHeaderView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public bool StressLoopUntilFail
    {
        get => (bool)GetValue(StressLoopUntilFailProperty);
        set => SetValue(StressLoopUntilFailProperty, value);
    }

    public static readonly DependencyProperty StressStatusProperty =
        DependencyProperty.Register(nameof(StressStatus), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string StressStatus
    {
        get => (string)GetValue(StressStatusProperty);
        set => SetValue(StressStatusProperty, value);
    }

    public static readonly DependencyProperty StressRunAutomationIdProperty =
        DependencyProperty.Register(nameof(StressRunAutomationId), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string StressRunAutomationId
    {
        get => (string)GetValue(StressRunAutomationIdProperty);
        set => SetValue(StressRunAutomationIdProperty, value);
    }

    public static readonly DependencyProperty StressStopAutomationIdProperty =
        DependencyProperty.Register(nameof(StressStopAutomationId), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string StressStopAutomationId
    {
        get => (string)GetValue(StressStopAutomationIdProperty);
        set => SetValue(StressStopAutomationIdProperty, value);
    }

    public static readonly DependencyProperty StressRepeatAutomationIdProperty =
        DependencyProperty.Register(nameof(StressRepeatAutomationId), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string StressRepeatAutomationId
    {
        get => (string)GetValue(StressRepeatAutomationIdProperty);
        set => SetValue(StressRepeatAutomationIdProperty, value);
    }

    public static readonly DependencyProperty StressLoopAutomationIdProperty =
        DependencyProperty.Register(nameof(StressLoopAutomationId), typeof(string), typeof(SmokeHeaderView),
            new PropertyMetadata(null));
    public string StressLoopAutomationId
    {
        get => (string)GetValue(StressLoopAutomationIdProperty);
        set => SetValue(StressLoopAutomationIdProperty, value);
    }
}

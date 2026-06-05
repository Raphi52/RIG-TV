// SPDX-License-Identifier: Proprietary
// Adapter ScenarioViewModel (modèle legacy smoke) → IScenarioItem, module DCADEMAT.
// 2026-05-29 soir — miroir instance-based d'AlertesLegacyScenarioAdapter (cf. Smoke\Alertes\).
// Une instance = UNE tuile parallèle d'un scénario DCADEMAT (dca-validation / dca-reclamation /
// dca-refus / dca-interrompue / form-validation / ... / form-interrompue).
// L'instanceId (= workerId = sous-dossier self-snap) route les lignes stdout + les PNG.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Dcademat;

/// <summary>
/// Wrappe UNE instance d'un scénario DCADEMAT pour MosaicView / FocusView / ScenarioListView
/// via <see cref="IScenarioItem"/>. Routing par <see cref="_instanceId"/> (ex "dcademat-dca-validation-1").
/// </summary>
public sealed class DcadematLegacyScenarioAdapter : INotifyPropertyChanged, IScenarioItem
{
    private readonly ScenarioViewModel _baseVm;
    private readonly SmokeRunnerProxy? _legacyProxy;
    private readonly string _instanceId;
    private readonly int _displayIndex;
    private readonly string _kind;       // "dca-validation" | "dca-reclamation" | ... | "form-interrompue"
    private readonly bool _showSuffix;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DcadematLegacyScenarioAdapter(
        ScenarioViewModel baseVm,
        SmokeRunnerProxy? legacyProxy,
        string instanceId,
        int displayIndex,
        string kind,
        bool showSuffix)
    {
        _baseVm = baseVm;
        _legacyProxy = legacyProxy;
        _instanceId = instanceId;
        _displayIndex = displayIndex;
        _kind = kind;
        _showSuffix = showSuffix;

        if (_legacyProxy != null)
            _legacyProxy.LinesChanged += OnLinesChanged;
    }

    private string WorkerId => _instanceId;

    /// <summary>Discriminant : "dca-validation" / "dca-reclamation" / ... Utilisé par
    /// PlayDcadematScenario pour relancer CE scénario seul.</summary>
    public string Kind => _kind;

    /// <summary>Identité unique (workerId + sous-dossier self-snap).</summary>
    public string InstanceId => _instanceId;

    // ── Steps attendus (génériques par kind) ────────────────────────────────
    // 1er jet (scaffolding) : Sanity + Lancement + Login + Ouvrir alerte + Ouvrir demande + Action.
    // Program.cs émet des lignes avec le préfixe "<TAG> : ..." où TAG = kind en MAJUSCULES
    // (ex "DCA-VALIDATION : Ouvrir alerte"). Le step métier "Action" sera affiné en Étape 3.
    private static readonly List<(string prefix, string label)> Common = new()
    {
        ("Sanity : RigClientAccueil.exe", "Sanity binaire RIG"),
        ("Lancement RIG legacy",          "Lancement RIG"),
        ("Click 'Se connecter'",          "Login RIG"),
    };

    private List<(string prefix, string label)> ExpectedSteps
    {
        get
        {
            var tag = _kind.ToUpperInvariant(); // ex "DCA-VALIDATION"
            var steps = new List<(string, string)>(Common)
            {
                ($"{tag} : Ouvrir alerte",  "Alerte RCS + grille des demandes"),
                ($"{tag} : Ouvrir demande", "Demande ouverte (Configurer le dépôt)"),
            };
            // Étape 3 — step terminal "Action" métier. Le préfixe "{tag} : Action" matche la ligne émise
            // par Program.cs (ex "DCA-VALIDATION : Action (validation) - …", "FORM-REFUS : Action (refus) - …").
            // La tuile atteint Done quand cette Action passe.
            //   - validation  : case DCA + Valider + n° dépôt/facture/demande (DCA) ; Valider formalité (form).
            //   - reclamation : motif INPMANQ + texte + Réclamer (DCA).
            //   - refus       : Refuser (DCA + form) — NOUVEAU 2026-06-04.
            //   - form-validation : Valider formalité — NOUVEAU 2026-06-04.
            // Les kinds SANS action mutante (dca-interrompue, form-reclamation, form-interrompue) gardent
            // pour terminal "Ouvrir demande" (l'ouverture = la reprise, comme les scénarios ALERTES).
            bool hasActionStep =
                _kind == "dca-validation" || _kind == "dca-reclamation"
                || _kind == "dca-refus" || _kind == "form-validation" || _kind == "form-refus";
            if (hasActionStep)
                steps.Add(($"{tag} : Action", ActionLabel()));
            return steps;
        }
    }

    private string ActionLabel()
    {
        if (_kind.Contains("validation"))  return "Valider → n° dépôt/facture/demande";
        if (_kind.Contains("reclamation")) return "Réclamer → courrier (motif)";
        if (_kind.Contains("refus"))       return "Refuser";
        if (_kind.Contains("interrompue")) return "Interrompre";
        return "Action métier";
    }

    private List<SmokeResultLine> MyLines =>
        _legacyProxy is null
            ? new List<SmokeResultLine>()
            : _legacyProxy.Lines.Where(l => l.WorkerId == _instanceId).ToList();

    public IReadOnlyList<StepDisplay> Steps
    {
        get
        {
            var expected = ExpectedSteps;
            var lineList = MyLines;
            var result = new List<StepDisplay>(expected.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                var (prefix, label) = expected[i];
                var match = lineList.FirstOrDefault(l => l.Description.StartsWith(prefix));
                result.Add(new StepDisplay(
                    stepNumber: i + 1,
                    shortLabel: label,
                    outcome: match?.Outcome ?? SmokeOutcome.Unknown,
                    fullDescription: match?.Description));
            }
            return result;
        }
    }

    public ScenarioViewModel Underlying => _baseVm;

    // ── IScenarioItem ────────────────────────────────────────────────────────

    public string Id => _showSuffix ? $"{_baseVm.Title}  #{_displayIndex}" : _baseVm.Title;

    public ScenarioPhase Phase => ComputePhase();

    private ScenarioPhase ComputePhase()
    {
        var steps = Steps;
        if (steps.Any(s => s.Outcome == SmokeOutcome.Failed)) return ScenarioPhase.Fail;
        var terminal = steps[steps.Count - 1];
        if (terminal.Outcome == SmokeOutcome.Passed || terminal.Outcome == SmokeOutcome.Skipped)
            return ScenarioPhase.Done;
        bool started = MyLines.Count > 0 || DesktopName != null;
        return started ? ScenarioPhase.Running : ScenarioPhase.Queued;
    }

    public Verdict Verdict
    {
        get
        {
            switch (ComputePhase())
            {
                case ScenarioPhase.Done: return Verdict.Pass;
                case ScenarioPhase.Fail: return Verdict.Fail;
                default:                 return Verdict.Pending;
            }
        }
    }

    public int? WorkerPid
    {
        get { var (pid, _) = ReadHwndTxt(); return pid; }
    }

    public string? DesktopName
    {
        get
        {
            var (pid, desk) = ReadHwndTxt();
            if (pid is null || string.IsNullOrWhiteSpace(desk)) return null;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
                if (p.HasExited) return null;
            }
            catch { return null; }
            return desk;
        }
    }

    private (int? pid, string? desk) ReadHwndTxt()
    {
        try
        {
            var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            if (string.IsNullOrEmpty(runStamp)) return (null, null);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var hwndFile = Path.Combine(local, "rig-wpf-testviewer", "self-snaps", runStamp, _instanceId, "hwnd.txt");
            if (!File.Exists(hwndFile)) return (null, null);
            var lines = File.ReadAllLines(hwndFile);
            if (lines.Length < 3) return (null, null);
            int? pid = int.TryParse(lines[1], out var p) ? p : (int?)null;
            return (pid, lines[2]);
        }
        catch { return (null, null); }
    }

    public string LogsTail
    {
        get
        {
            if (_legacyProxy is null) return _baseVm.ResultMessage ?? "";
            var lines = MyLines;
            if (lines.Count == 0) return "(Aucune ligne encore reçue — worker pas démarré ou en cours d'init)";
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                var icon = l.Outcome switch
                {
                    SmokeOutcome.Passed  => "✓",
                    SmokeOutcome.Failed  => "✗",
                    SmokeOutcome.Skipped => "⊘",
                    _                    => "·",
                };
                sb.AppendLine($"{icon} {l.Description}");
            }
            return sb.ToString();
        }
    }

    private string? _frozenPath;

    public string? LastSnapPath => _frozenPath ?? GetLatestSnap();

    private string? GetLatestSnap()
    {
        try
        {
            var runStamp = Environment.GetEnvironmentVariable("RIG_RUN_STAMP");
            if (string.IsNullOrEmpty(runStamp)) return null;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "rig-wpf-testviewer", "self-snaps", runStamp, _instanceId);
            if (!Directory.Exists(dir)) return null;
            var latest = new DirectoryInfo(dir).GetFiles("snap-*.png")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            return latest?.FullName;
        }
        catch { return null; }
    }

    internal void NotifySnapChanged()
    {
        MaybeFreeze();
        Raise(nameof(LastSnapPath));
        Raise(nameof(DesktopName));
        Raise(nameof(WorkerPid));
        Raise(nameof(Phase));
        Raise(nameof(Verdict));
        Raise(nameof(CanPlay));
        Raise(nameof(CanStop));
    }

    private void OnLinesChanged()
    {
        MaybeFreeze();
        Raise(nameof(Steps));
        Raise(nameof(LogsTail));
        Raise(nameof(Phase));
        Raise(nameof(Verdict));
        Raise(nameof(LastSnapPath));
        Raise(nameof(CanPlay));
        Raise(nameof(CanStop));
    }

    private void MaybeFreeze()
    {
        if (_frozenPath != null) return;
        var ph = ComputePhase();
        if (ph == ScenarioPhase.Done || ph == ScenarioPhase.Fail)
            _frozenPath = GetLatestSnap();
    }

    public string? FailScreenshotPath => null;
    public string DurationFormatted => "—:—";

    /// <summary>Play visible si l'instance n'est PAS en cours ; Stop si son worker est vivant.</summary>
    public bool CanPlay => ComputePhase() != ScenarioPhase.Running;
    public bool CanStop => DesktopName != null;

    internal void ResetFrozenPath()
    {
        if (_frozenPath != null) { _frozenPath = null; Raise(nameof(LastSnapPath)); }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// SPDX-License-Identifier: Proprietary
// Adapter ScenarioViewModel (modèle legacy smoke) → IScenarioItem (template SmokePage).
// 2026-05-28 — permet d'afficher les scénarios KBIS legacy dans le tab Smoke E2E
// via le même layout 3-col que RAPTURE (sans avoir à dupliquer/migrer la donnée).
//
// 2026-05-29 v3 — INSTANCE-BASED : un adapter = UNE instance parallèle d'un scenario
// (kbis-vk-1, kbis-vk-2, kbis-xex-1, …). L'instanceId (passé au ctor) sert à la fois
// de workerId (tag des lignes stdout) ET de sous-dossier self-snap. Phase/Verdict/Steps
// sont calculés PAR INSTANCE depuis les lignes filtrées par instanceId — plus depuis
// le ScenarioViewModel partagé (qui ne peut pas porter N états). Permet N tuiles live
// indépendantes dans la mosaïque selon le parallélisme choisi.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Kbis;

/// <summary>
/// Wrappe UNE instance parallèle d'un scenario KBIS legacy (VK ou XEX) pour qu'elle soit
/// consommable par MosaicView / FocusView / ScenarioListView via <see cref="IScenarioItem"/>.
///
/// Routing par <see cref="_instanceId"/> (ex. "kbis-vk-2") :
///   - workerId : filtre les <see cref="SmokeResultLine"/> de CETTE instance
///   - sous-dossier self-snap : self-snaps/$RUN_STAMP/&lt;instanceId&gt;/ (PNG + hwnd.txt)
/// Phase/Verdict dérivent de la ligne terminale (dernier step attendu) + détection any-failed.
/// </summary>
public sealed class KbisLegacyScenarioAdapter : INotifyPropertyChanged, IScenarioItem
{
    private readonly ScenarioViewModel _baseVm;
    private readonly SmokeRunnerProxy? _legacyProxy;
    private readonly string _instanceId;
    private readonly int _displayIndex;
    private readonly bool _isVk;
    private readonly bool _showSuffix;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="baseVm">Template du scenario (Title/Description). Partagé entre instances.</param>
    /// <param name="instanceId">Identité unique de l'instance : "kbis-vk-1", "kbis-xex-2"… Sert de
    /// workerId (tag lignes) ET de sous-dossier self-snap.</param>
    /// <param name="displayIndex">Numéro affiché (#1, #2…) quand plusieurs instances du type.</param>
    /// <param name="isVk">true = scenario VK, false = XEX (sélectionne la liste de steps).</param>
    /// <param name="showSuffix">true si plusieurs instances de ce type → affiche "#idx" dans le titre.</param>
    public KbisLegacyScenarioAdapter(
        ScenarioViewModel baseVm,
        SmokeRunnerProxy? legacyProxy,
        string instanceId,
        int displayIndex,
        bool isVk,
        bool showSuffix)
    {
        _baseVm = baseVm;
        _legacyProxy = legacyProxy;
        _instanceId = instanceId;
        _displayIndex = displayIndex;
        _isVk = isVk;
        _showSuffix = showSuffix;

        if (_legacyProxy != null)
            _legacyProxy.LinesChanged += OnLinesChanged;
    }

    /// <summary>WorkerId qui tagge les SmokeResultLine de CETTE instance (= instanceId).</summary>
    private string WorkerId => _instanceId;

    // ── Steps pré-définis VK + XEX ─────────────────────────────────────────
    // Liste ordonnée des steps attendus par scenario. Chaque entrée = (prefix
    // qui match la ligne ✓/✗ stdout, label court affiché). Les steps non encore
    // exécutés restent visibles en "Pending".

    private static readonly List<(string prefix, string label)> VkExpectedSteps = new()
    {
        ("Sanity : RigClientAccueil.exe",         "Sanity binaire RIG"),
        ("Lancement RIG legacy",                  "Lancement RIG"),
        ("Click 'Se connecter'",                  "Login RIG"),
        ("VK : Ouvrir PROC_KBIS",                 "Ouvrir PROC_KBIS"),
        ("VK : Le tab K-Bis (VK)",                "Tab K-Bis (VK) ouvert"),
        ("VK : Saisir numéro de gestion",         "Saisir num_gestion → dossier"),
        ("VK : Click 'Visualiser K-bis'",         "Ouvrir le K-bis (PDF)"),
        ("VK : Le PDF K-bis contient",            "PDF contient les bonnes données"),
    };

    private static readonly List<(string prefix, string label)> XexExpectedSteps = new()
    {
        ("Sanity : RigClientAccueil.exe",         "Sanity binaire RIG"),
        ("Lancement RIG legacy",                  "Lancement RIG"),
        ("Click 'Se connecter'",                  "Login RIG"),
        ("XEX : Ouvrir PROC_XEX",                 "Ouvrir PROC_XEX"),
        ("XEX : Saisir numéro de gestion",        "Saisir num_gestion"),
        ("XEX : Alt+V Valider",                   "Alt+V → tableau d'éditions"),
        ("XEX : Décoche imprimante (NE PAS",      "Décoche imprimante → Brouillon"),
    };

    private List<(string prefix, string label)> ExpectedSteps => _isVk ? VkExpectedSteps : XexExpectedSteps;

    /// <summary>Lignes stdout de CETTE instance (filtrées par instanceId).</summary>
    private List<SmokeResultLine> MyLines =>
        _legacyProxy is null
            ? new List<SmokeResultLine>()
            : _legacyProxy.Lines.Where(l => l.WorkerId == _instanceId).ToList();

    /// <summary>Steps internes de CETTE instance sous forme de StepDisplay (Pending visible
    /// pour les steps pas encore exécutés). Merge entre liste pré-définie + lignes reçues.</summary>
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

    /// <summary>Référence à la VM legacy template (Title/Description/Tags).</summary>
    public ScenarioViewModel Underlying => _baseVm;

    // ── IScenarioItem mapping ───────────────────────────────────────────────

    /// <summary>Id unique de tuile : titre du scenario + suffixe #idx quand plusieurs
    /// instances du même type tournent en parallèle.</summary>
    public string Id => _showSuffix ? $"{_baseVm.Title}  #{_displayIndex}" : _baseVm.Title;

    /// <summary>Phase calculée PAR INSTANCE depuis les lignes filtrées :
    /// any-failed → Fail ; ligne terminale Passed/Skipped → Done ; au moins 1 ligne reçue ou
    /// HDESK vivant → Running ; sinon → Queued.</summary>
    public ScenarioPhase Phase => ComputePhase();

    private ScenarioPhase ComputePhase()
    {
        // Calcul basé sur les STEPS ATTENDUS (mappés aux lignes ✓/✗ reçues), PAS sur les lignes
        // brutes : la ligne de récap du runner ("✗ failed  0") commence par ✗ mais n'est pas un
        // step et ne matche aucun préfixe attendu → ignorée. Sinon tout scenario réussi tombait
        // en faux Fail dès l'impression du récap (bug 2026-05-29).
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

    /// <summary>PID du worker SmokeRunner (lu depuis hwnd.txt ligne 2). Null si worker
    /// pas démarré ou déjà terminé. Sert au bouton ✕ Close HDESK (taskkill /T).</summary>
    public int? WorkerPid
    {
        get
        {
            var (pid, _) = ReadHwndTxt();
            return pid;
        }
    }

    /// <summary>Nom du HDESK isolé (RigSmoke_&lt;pid&gt;) lu depuis hwnd.txt ligne 3.
    /// Retourne null si le worker est mort (HDESK détruit) → bouton "Ouvrir HDESK"
    /// auto-grisé après la fin du run. Valide uniquement pendant le run.</summary>
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
            catch { return null; } // process introuvable = mort → HDESK GC'd
            return desk;
        }
    }

    /// <summary>Lit (pid, desktopName) depuis hwnd.txt du dossier self-snap de CETTE instance.
    /// Format 3 lignes : hwnd / pid / desktopName. (null, null) si absent/malformé.</summary>
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

    /// <summary>
    /// LogsTail : concaténation des lignes ✓/✗ reçues pour CETTE instance, affiché dans la
    /// fenêtre LOGS. Affiche les steps déjà exécutés avec leur état.
    /// </summary>
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

    /// <summary>
    /// Path frozen capturé au moment de la transition Pending → Pass/Fail. Permet à une
    /// instance Done de garder son snapshot final pendant que les autres continuent à
    /// updater leur live PNG.
    /// </summary>
    private string? _frozenPath;

    /// <summary>
    /// Path du dernier self-snap PNG du RigClient legacy (PrintWindow 500ms) de CETTE instance.
    /// Done/Fail → _frozenPath (figé) ; sinon → dernier PNG (LIVE feed).
    /// </summary>
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

    /// <summary>Notify TV que LastSnapPath / DesktopName / WorkerPid / Phase ont pu changer
    /// (appelé par le snap timer à chaque tick). DesktopName s'active dès que le worker écrit
    /// hwnd.txt et se désactive quand il meurt → bouton "Ouvrir HDESK" live.</summary>
    internal void NotifySnapChanged()
    {
        MaybeFreeze();
        Raise(nameof(LastSnapPath));
        Raise(nameof(DesktopName));
        Raise(nameof(WorkerPid));
        Raise(nameof(Phase));     // Queued → Running dès que le worker démarre (hwnd.txt écrit)
        Raise(nameof(Verdict));
        Raise(nameof(CanPlay));   // worker démarre/meurt → ▶/⏹ live
        Raise(nameof(CanStop));
    }

    /// <summary>Appelé quand de nouvelles lignes stdout arrivent (LinesChanged) : recalcule
    /// l'état de l'instance et re-raise les props dépendantes.</summary>
    private void OnLinesChanged()
    {
        MaybeFreeze();
        Raise(nameof(Steps));
        Raise(nameof(LogsTail));
        Raise(nameof(Phase));
        Raise(nameof(Verdict));
        Raise(nameof(LastSnapPath));
        Raise(nameof(CanPlay));   // Running → Done/Fail : ▶ Play réapparaît
        Raise(nameof(CanStop));
    }

    /// <summary>Freeze le PNG courant à la transition Pending → Done/Fail (une seule fois).</summary>
    private void MaybeFreeze()
    {
        if (_frozenPath != null) return;
        var ph = ComputePhase();
        if (ph == ScenarioPhase.Done || ph == ScenarioPhase.Fail)
            _frozenPath = GetLatestSnap();
    }

    public string? FailScreenshotPath => null;

    /// <summary>Pas de durée par scénario en smoke legacy.</summary>
    public string DurationFormatted => "—:—";

    /// <summary>true = scenario VK, false = XEX. Utilisé par PlayKbisScenario (1-instance plan).</summary>
    public bool IsVk => _isVk;

    /// <summary>Identité de l'instance (kbis-vk-1, kbis-xex-2…) = workerId + sous-dossier snap.
    /// Utilisé par PlayKbisScenario pour relancer CE scénario EN PLACE (sans rebuild de la mosaïque).</summary>
    public string InstanceId => _instanceId;

    /// <summary>Contrat IScenarioItem (template MosaicView) : Play visible quand l'instance n'est
    /// PAS en cours (Queued/Done/Fail → (re)lançable), Stop visible quand son worker est vivant.
    /// Mirror de ScenarioRunState.CanPlay/CanStop (RAPTURE) → boutons ▶/⏹ identiques dans la tuile.</summary>
    public bool CanPlay => ComputePhase() != ScenarioPhase.Running;
    public bool CanStop => DesktopName != null;

    /// <summary>Reset le frozen path quand un nouveau run smoke démarre.</summary>
    internal void ResetFrozenPath()
    {
        if (_frozenPath != null)
        {
            _frozenPath = null;
            Raise(nameof(LastSnapPath));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

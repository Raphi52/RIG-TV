using System.Collections.Generic;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Catalogue statique des scénarios de <c>Rig.Wpf.Kbis.SmokeRunner.exe</c>.
/// Pas découvert dynamiquement (Smoke n'est pas xUnit) — on liste ce que
/// Program.cs du runner appelle via TryStep/Skip.
/// </summary>
public static class SmokeCatalog
{
    public static IReadOnlyList<SmokeScenario> All { get; } = new List<SmokeScenario>
    {
        // ── DI bootstrap ────────────────────────────────────────────────────
        new("DI",     "Résolution du KbisProcessusViewModel via DI",
            new[] { "Résolution du KbisProcessusViewModel via DI" },
            "Le SmokeRunner reproduit App.xaml.cs DI (Core+Mvvm+RigMetier+Sql+Legacy+Kbis). " +
            "Vérifie que KbisProcessusViewModel se résout sans throw."),
        new("DI",     "Code Processus = \"KBIS\"",
            new[] { "Code Processus = \"KBIS\"" },
            "ProcessusVM.Code = \"KBIS\" — identifie l'onglet côté Shell."),
        new("DI",     "Étape Consultation présente",
            new[] { "1 étape Consultation présente" },
            "Exactement 1 KbisConsultationEtapeViewModel dans Etapes."),

        // ── Recherche société ───────────────────────────────────────────────
        new("Search", "Search(\"A\") via SQL repository",
            new[] { "Search(\"A\") retourne au moins 1 société" },
            "Set FiltreTexte=\"A\" → poll jusqu'à Resultats.Count > 0. " +
            "Touche SQL-DEV/RIG_DEV.DOSSIER_RCS (SELECT TOP @maxResults)."),

        // ── PDF ─────────────────────────────────────────────────────────────
        new("PDF",    "PDF généré sur disque",
            new[] { "PDF généré sur disque", "Génération PDF" },
            "Selection.set → GenerateAsync via IKbisGenerator (Apache FOP). " +
            "Itère jusqu'à 5 sociétés pour trouver une qui matche le greffe local."),
        new("PDF",    "Signature PDF en début de fichier",
            new[] { "Signature PDF en début de fichier" },
            "Magic bytes %PDF dans les 4 premiers octets."),

        // ── Render Views WPF ────────────────────────────────────────────────
        new("Render", "Init Application + Theme.xaml",
            new[] { "Init Application + chargement Theme.xaml" },
            "Charge pack://…/Rig.Wpf.Shell;component/Theme/Theme.xaml. " +
            "Indispensable avant InitializeComponent (StaticResource)."),
        new("Render", "KbisProcessusView → PNG",
            new[] { "Render KbisProcessusView" },
            "Measure+Arrange+UpdateLayout → RenderTargetBitmap 1280×800. " +
            "readyWhen attend Resultats.Count > 0 avant snapshot."),
        new("Render", "Contenu non-blanc (Processus)",
            new[] { "Contenu non-blanc dans kbis-processus-view" },
            "Sample pixels sur 4 régions clés — fail si une zone est blanche."),
        new("Render", "KbisConsultationEtapeView → PNG",
            new[] { "Render KbisConsultationEtapeView" },
            "Rendu de l'étape seule (search + results + fiche rapide)."),
        new("Render", "Contenu non-blanc (Consultation)",
            new[] { "Contenu non-blanc dans kbis-consultation-view" },
            "Sample pixels sur les 3 zones du panneau gauche."),

        // ── PDF preview (vraie photo du K-bis) ──────────────────────────────
        new("Render", "PDF K-bis page 1 → PNG (PDFium sidecar)",
            new[] { "Render PDF K-bis" },
            "PDF Apache FOP converti en PNG via PDFium. La SEULE photo réelle " +
            "du K-bis dans le smoke — RenderTargetBitmap ne capture pas WebView2."),
        new("Render", "Contenu non-blanc (PDF)",
            new[] { "Contenu non-blanc dans kbis-pdf-preview" },
            "Sample 3 bandes verticales sur le PDF."),

        // ── Flux UI complet ─────────────────────────────────────────────────
        new("UI",     "Click flow via FlaUI (Shell.exe)",
            new[] { "UI flow OK", "UI flow —", "UI flow" },
            "Lance Rig.Wpf.Shell.exe + PreOpenTabs=[KBIS], tape 'A', clique " +
            "le 1er résultat, vérifie window vivante. Requiert le toggle « Include UI flow »."),

        // ── Coverage PDF (in-process, 10 sélections de la liste) ────────────
        new("PDF",    "Coverage PDF — 10 sélections itérées",
            new[] { "Coverage PDF :" },
            "Itère sur les 10 premiers résultats de Search('A'), sélectionne chacun, " +
            "attend PdfFilePath ou ErreurGeneration (30s max). Classifie chaque outcome : " +
            "✓ généré (file + magic %PDF) / ⊘ greffe externe (attendu vu la config locale) / " +
            "✗ unexpected. Test PASS si 0 unexpected + au moins 1 OK. Détails par société " +
            "dans le stdout (onglet Logs)."),

        // ── Coverage Buttons (in-process, fiable) ───────────────────────────
        new("Render", "Coverage Buttons — 5 boutons KBIS présents",
            new[] { "Coverage Buttons : 5 boutons" },
            "Instancie la View en mémoire avec Selection sur une société qui génère un PDF " +
            "(toolbar PDF visible). Walk visual tree pour vérifier que les 5 boutons " +
            "(Imprimer/Télécharger/Zoom-/Zoom+/Régénérer) sont présents et IsEnabled. " +
            "Test PASS si les 5 sont là et activés."),
        new("Render", "RegenererCommand — Execute fonctionne",
            new[] { "RegenererCommand : Execute" },
            "Invoke programmatiquement RegenererCommand.Execute(null) avec Selection set. " +
            "Vérifie que la commande CanExecute=true, qu'elle termine son cycle de génération " +
            "(IsGenerating retourne false), et qu'elle ne jette pas d'exception."),

    };

    /// <summary>Cherche dans <paramref name="lines"/> la première ligne match un préfixe.</summary>
    public static SmokeResultLine? Match(SmokeScenario scenario, IEnumerable<SmokeResultLine> lines)
    {
        foreach (var line in lines)
        {
            foreach (var prefix in scenario.MatchPrefixes)
            {
                if (line.Description.StartsWith(prefix))
                    return line;
            }
        }
        return null;
    }
}

public sealed class SmokeScenario
{
    public string Category { get; }
    public string Title { get; }
    public string[] MatchPrefixes { get; }
    public string Description { get; }
    /// <summary>"wpf" (default) ou "legacy". Influe sur le tag dérivé dans ScenarioViewModel.</summary>
    public string Backend { get; }
    /// <summary>Tags supplémentaires explicites (ex. "kbis" pour les scénarios qui exercent KBIS legacy).</summary>
    public string[] ExtraTags { get; }
    public SmokeScenario(string category, string title, string[] matchPrefixes, string description,
        string backend = "wpf", string[]? extraTags = null)
    {
        Category = category; Title = title; MatchPrefixes = matchPrefixes; Description = description;
        Backend = backend;
        ExtraTags = extraTags ?? System.Array.Empty<string>();
    }
}

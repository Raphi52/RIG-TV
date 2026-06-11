using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Logique PURE (sans UI/process/IO) de vérification du K-bis :
///   - classification d'un process viewer/générateur PDF "significatif",
///   - extraction du chemin .pdf depuis une ligne de commande,
///   - détection SIREN / numéro de gestion / marqueurs structurels dans le texte extrait.
///
/// Extraite de <see cref="LegacyDriver"/> pour être testable en xUnit (rapide, sans run RIG).
/// Les bugs réels de 2026-05-29 (SIREN \b qui ne matche pas sur texte PdfPig collé,
/// RigAffichageDoc non whitelisté) sont désormais couverts par des tests unitaires.
/// </summary>
public static class KbisTextChecks
{
    /// <summary>Marqueurs structurels d'un vrai extrait K-bis (RCS).</summary>
    public static readonly string[] KbisMarkers =
    {
        "GREFFE", "R.C.S", "RCS", "EXTRAIT", "IMMATRICULATION",
        "REGISTRE DU COMMERCE", "Kbis", "K-bis", "SIREN"
    };

    /// <summary>
    /// true si le process est un viewer/générateur PDF "significatif" pour le K-bis (preuve que
    /// le document s'est ouvert), vs un hôte COM générique (dllhost/conhost…) à ignorer.
    /// ⚠ RigAffichageDoc = viewer INTERNE RIG : signal le plus fort (RIG l'ouvre pour le K-bis).
    /// </summary>
    public static bool IsMeaningfulKbisProc(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var n = processName;
        return n.IndexOf("RigAffichageDoc", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("KBis", StringComparison.OrdinalIgnoreCase) >= 0        // KBisXML2PDF
            || n.IndexOf("XML2PDF", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("Acro", StringComparison.OrdinalIgnoreCase) >= 0        // Acrobat / AcroRd32
            || n.IndexOf("Foxit", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("Sumatra", StringComparison.OrdinalIgnoreCase) >= 0
            || n.Equals("msedge", StringComparison.OrdinalIgnoreCase)
            || n.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || n.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)         // courrier/lettre généré ouvert dans Word (.doc)
            || n.IndexOf("Vintasoft", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// true si le process est le viewer de pièces dématérialisées "DocDemat" significatif :
    ///   - formalités → `PROC_DOC_DEMAT_EXE` (fenêtre "Visualisation des pièces"),
    ///   - DCADEMAT  → un viewer PDF (réutilise <see cref="IsMeaningfulKbisProc"/>).
    /// </summary>
    public static bool IsMeaningfulDocDematProc(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var n = processName;
        return n.IndexOf("PROC_DOC_DEMAT", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("DocDemat", StringComparison.OrdinalIgnoreCase) >= 0
            || IsMeaningfulKbisProc(n); // DCADEMAT ouvre un PDF (Acrobat/Edge/RigAffichageDoc…)
    }

    /// <summary>
    /// Détermine si une LIGNE de la grille des demandes (texte concaténé des cellules) correspond
    /// au type voulu : DCADEMAT (colonne "Traitement" = DCADEMAT) ou formalité (N° de liaison "J00…"
    /// et PAS DCADEMAT). Pur → testable sans run RIG (gate étape 0).
    /// </summary>
    public static bool MatchesDemandeType(string? rowText, bool wantDcademat)
    {
        if (string.IsNullOrEmpty(rowText)) return false;
        bool isDca = rowText.IndexOf("DCADEMAT", StringComparison.OrdinalIgnoreCase) >= 0;
        if (wantDcademat) return isDca;
        // Formalité : numéro de liaison commençant par "J00" + au moins un chiffre, hors DCADEMAT.
        return !isDca && Regex.IsMatch(rowText, "J00\\d", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Extrait le 1er chemin .pdf d'une ligne de commande. RIG lance le viewer AVEC le K-bis en
    /// argument (ex. RigAffichageDoc.exe "greffe=9995;TypeParam=NomFichier;Param=c:\tmp\rig\...\x.pdf").
    /// </summary>
    public static string? ExtractPdfPathFromCmdline(string? cmdline)
    {
        if (string.IsNullOrEmpty(cmdline)) return null;
        var m = Regex.Match(cmdline, "([A-Za-z]:\\\\[^\"]*?\\.pdf)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Détecte un numéro SIREN (9 chiffres, espacés "982 574 717" ou non "982574717").
    /// PAS de \b : PdfPig colle le texte ("numéro982 574 717") → on borne par des lookarounds
    /// anti-chiffres pour matcher exactement un groupe de 9 chiffres, pas un fragment d'un + long.
    /// </summary>
    public static string? FindSiren(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, "(?<!\\d)\\d{3}\\s?\\d{3}\\s?\\d{3}(?!\\d)");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// true si le numéro de gestion demandé figure dans le texte du PDF (preuve = bon dossier).
    /// Comparaison sans espaces (robuste au formatage / au collage PdfPig).
    /// </summary>
    public static bool ContainsNumGestion(string? text, string? numGestion)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(numGestion)) return false;
        var t = Regex.Replace(text, "\\s+", "");
        var ng = Regex.Replace(numGestion, "\\s+", "");
        return ng.Length > 0 && t.IndexOf(ng, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Marqueurs structurels K-bis présents dans le texte (≥2 attendu pour un vrai K-bis).</summary>
    public static IReadOnlyList<string> FindKbisMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return KbisMarkers.Where(m => text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    /// <summary>true si le K-bis va jusqu'au bout (non tronqué).</summary>
    public static bool IsComplete(string? text) =>
        !string.IsNullOrEmpty(text) && text.IndexOf("FIN DE L'EXTRAIT", StringComparison.OrdinalIgnoreCase) >= 0;

    // ════════════════════════════════════════════════════════════════════════
    // DCADEMAT "Configurer le dépôt" — preuve de succès après Valider
    //
    // Après avoir coché la case "DCA" et cliqué "Valider", la grille "Exercices"
    // affiche 3 nouvelles colonnes (preuve que le dépôt a été créé) :
    //   - n° de dépôt   : "DCA B2026/000052"  (préfixe "DCA " + lettre + année/séquence)
    //   - n° de facture : "26-000389"         (AA-NNNNNN)
    //   - n° de demande : "D2611200138"        (préfixe "D" + 10 chiffres)
    //
    // Le texte lu via MSAA (accName + accValue d'une LIGNE) concatène toutes les
    // cellules ; ces helpers extraient chaque numéro du texte agrégé. Pur → testable.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extrait le n° de demande DCADEMAT ("D" suivi de ≥8 chiffres, ex "D2611200138") du texte
    /// d'une ligne de la grille Exercices. Borné par lookarounds anti-alphanum pour ne pas
    /// attraper un fragment (le "D" doit être un vrai début de jeton). Null si absent.
    /// </summary>
    public static string? FindNumDemande(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // Le "D" peut être collé à la fin d'un libellé ("n° de demandeD2611200138") quand MSAA/PdfPig
        // concatène — on n'exclut que les chiffres et un "D" précédents (pour ne pas capturer un
        // fragment ni couper un "DD…"), pas les autres lettres.
        var m = Regex.Match(text, "(?<![Dd0-9])D\\d{8,}(?![0-9])");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Extrait le n° de dépôt DCADEMAT ("DCA " + lettre + "AAAA/NNNNNN", ex "DCA B2026/000052").
    /// Tolère l'espace collé/absent par PdfPig/MSAA (DCAB2026/000052). Null si absent.
    /// </summary>
    public static string? FindNumDepot(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, "DCA\\s?[A-Z]\\d{4}/\\d{4,}", RegexOptions.IgnoreCase);
        return m.Success ? Regex.Replace(m.Value, "\\s+", " ").Trim() : null;
    }

    /// <summary>
    /// Extrait le n° de facture DCADEMAT ("AA-NNNNNN", ex "26-000389") du texte de la ligne.
    /// Bornes anti-chiffres pour ne pas capturer une sous-séquence d'un nombre plus long. Null si absent.
    /// </summary>
    public static string? FindNumFacture(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, "(?<!\\d)\\d{2}-\\d{5,}(?!\\d)");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Vrai si le texte d'une ligne "Exercices" contient au moins le n° de demande (préfixe "D"),
    /// = critère de succès minimal après Valider (le dépôt DCADEMAT a bien été créé).
    /// </summary>
    public static bool HasDepotSuccess(string? rowText) => FindNumDemande(rowText) != null;
}

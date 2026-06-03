using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rig.Wpf.Kbis.SmokeRunner;

/// <summary>
/// Logique PURE (sans FlaUI/SQL/etat d'instance) extraite de <see cref="LegacyDriver"/> pour etre
/// testable en xUnit (rapide, sans run RIG). Entree -> sortie deterministe, aucun effet de bord.
///
/// Pattern identique a <see cref="KbisTextChecks"/> : on sort les bouts de parsing/matching qui
/// etaient inlines (et souvent dupliques) dans le driver afin de les couvrir par des tests rapides.
///
/// Couvert :
///   - <see cref="FirstToken"/>             : 1er jeton d'un Name lstProcessus (code PROC), duplique 2x
///   - <see cref="IsNumDemandeToken"/>       : token "D" + chiffres (numero de demande), duplique 4x
///   - <see cref="ExtractAudienceId"/>       : "ID=12345" dans le texte d'une popup "Audience creee"
///   - <see cref="IsDataRowName"/>           : ligne DataGridView "Prefixe Ligne N" (N>=1), 2 sites
///   - <see cref="Truncate"/>                : tronque + ellipse pour le dump MSAA
///   - <see cref="AppendMotifMarker"/>       : ajoute un marqueur en fin de texte de motif (scenario reclamation)
///   - <see cref="ContainsMotifMarker"/>     : detecte le marqueur dans un texte (verif courrier de reclamation)
///   - <see cref="MotifAlreadySelected"/>    : la valeur courante du combo motif correspond-elle deja au motif voulu ?
/// </summary>
public static class LegacyParsing
{
    /// <summary>Separateurs de jeton pour un Name d'item lstProcessus ("VK Visualisation - Extrait RCS").</summary>
    private static readonly char[] TokenSeparators = { ' ', '\t', '|', '-' };

    /// <summary>
    /// 1er jeton (code processus) d'un Name d'item lstProcessus. Le Name peut etre le code seul
    /// ("VK") ou "code libelle" concatene ("VK Visualisation - Extrait RCS") -> on prend le 1er
    /// mot. Trimme d'abord (robuste aux espaces de bord), split sur espace/tab/pipe/tiret en
    /// ignorant les vides. Retourne "" si vide/null (jamais null, simplifie les comparaisons).
    /// </summary>
    public static string FirstToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return name.Trim()
                   .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                   .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// true si <paramref name="token"/> est un numero de demande RIG : "D" suivi UNIQUEMENT de
    /// chiffres, longueur totale >= 8 (ex "D2613500028"). Forme jeton-entier (le texte EST le
    /// numero), distincte de <see cref="KbisTextChecks.FindNumDemande"/> qui EXTRAIT un numero
    /// d'un texte agrege. Utilise pour reconnaitre un accName/accValue de cellule == numero.
    /// </summary>
    public static bool IsNumDemandeToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return token.Length >= 8
            && token[0] == 'D'
            && token.Skip(1).All(char.IsDigit);
    }

    /// <summary>
    /// Extrait l'ID d'audience d'un texte de popup "Audience creee (ID=12345)". Cherche le motif
    /// "ID=NNNN" (espaces tolerants autour du '='). Retourne null si absent ou non parsable en int.
    /// Sert au cleanup SQL post-smoke (l'audience creee pendant le test doit etre supprimee).
    /// </summary>
    public static int? ExtractAudienceId(string? popupText)
    {
        if (string.IsNullOrEmpty(popupText)) return null;
        var m = Regex.Match(popupText, @"ID\s*=\s*(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var id)) return id;
        return null;
    }

    /// <summary>
    /// true si le Name d'une cellule DataItem correspond a une VRAIE ligne de data nommee
    /// "&lt;prefixe&gt;Ligne N" avec N &gt;= 1 (ex prefixe "DCA " -> "DCA Ligne 3", prefixe
    /// "Imprimer " -> "Imprimer Ligne 2"). Exclut "&lt;prefixe&gt;Ligne 0" (ligne placeholder/header
    /// du DataGridView WinForms). Comparaison ordinale insensible a la casse.
    /// NB : les exclusions de COLONNE (ex "DCACO", "Imprimante") restent a la charge de l'appelant
    /// (elles dependent de la grille concernee, pas du format du nom de ligne).
    /// </summary>
    public static bool IsDataRowName(string? name, string prefix)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefix)) return false;
        var rowPrefix = prefix + "Ligne ";
        return name.StartsWith(rowPrefix, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(rowPrefix + "0", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tronque <paramref name="s"/> a <paramref name="maxLen"/> caracteres et ajoute une ellipse
    /// (U+2026) si depassement. Pur (formatage d'affichage pour le dump MSAA). null -> "".
    /// </summary>
    public static string Truncate(string? s, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        return s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
    }

    /// <summary>
    /// Ajoute le marqueur <paramref name="marker"/> a la FIN du texte de motif de reclamation
    /// (scenario dca-reclamation : on prouve que la modif du texte est prise en compte). Regles :
    ///   - texte existant (auto-rempli depuis le code motif) -> on enleve les blancs de fin puis on
    ///     ajoute " &lt;marker&gt;" (un espace separateur) -> le marqueur est en toute fin de phrase ;
    ///   - texte vide/null -> on retourne juste le marqueur ;
    ///   - si le texte se termine DEJA par le marqueur (idempotence : re-run / re-saisie) -> inchange,
    ///     pour ne pas accumuler "TEST TEST TEST".
    /// Comparaison du suffixe insensible a la casse. Le marqueur lui-meme est insere tel quel.
    /// Pur : entree texte -> sortie texte, aucun effet de bord.
    /// </summary>
    public static string AppendMotifMarker(string? existingText, string marker)
    {
        if (string.IsNullOrEmpty(marker)) return existingText ?? string.Empty;
        var baseText = (existingText ?? string.Empty).TrimEnd();
        if (baseText.Length == 0) return marker;
        // Idempotence : ne pas ajouter une 2e fois si le texte se termine deja par le marqueur.
        if (baseText.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return baseText;
        return baseText + " " + marker;
    }

    /// <summary>
    /// true si <paramref name="text"/> contient <paramref name="marker"/> (comparaison ordinale
    /// insensible a la casse). Sert a verifier que la modif du texte de motif ("TEST") se retrouve
    /// dans le courrier de reclamation genere (couche texte PDF/doc). null/vide -> false.
    /// </summary>
    public static bool ContainsMotifMarker(string? text, string marker)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker)) return false;
        return text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// true si la valeur DEJA affichee dans le combo « Type de motif » (combo RCS custom
    /// ULT_COMBO_CODE_MOTIF) correspond au motif voulu -> la selection est consideree comme
    /// DEJA FAITE et on n'a pas besoin d'ouvrir le dropdown (qui peuple ses items en lazy : ferme,
    /// UIA FindAll(ListItem) renvoie 0). C'est le 1er recours de la selection robuste (cf. run live :
    /// demande deja en reclamation, combo affichant "INPMANQ - Piece manquante, n...").
    ///
    /// Le texte d'item RCS est de la forme "&lt;CODE&gt; - &lt;libelle&gt;" (ex "INPMANQ - Piece manquante,
    /// non valide ou illisible"). On considere qu'il y a correspondance si, apres trim, la valeur
    /// courante (insensible a la casse) :
    ///   - est EXACTEMENT le motif (combo qui n'affiche que le code), OU
    ///   - COMMENCE par le motif (typiquement "&lt;CODE&gt; ..." ou "&lt;CODE&gt;-..." ou "&lt;CODE&gt; - ..."), OU
    ///   - CONTIENT le motif (libelle saisi sans le code, ou motif demande sous forme de libelle).
    /// Valeur courante vide/null -> false (rien de selectionne -> il faudra ouvrir le dropdown).
    /// motif vide/null -> false (on ne sait pas quoi matcher -> ne pas court-circuiter).
    /// Pur : (valeur courante, motif voulu) -> bool, aucun effet de bord.
    /// </summary>
    public static bool MotifAlreadySelected(string? currentValue, string? motif)
    {
        if (string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(motif)) return false;
        var cur = currentValue.Trim();
        var want = motif.Trim();
        return cur.Equals(want, StringComparison.OrdinalIgnoreCase)
            || cur.StartsWith(want, StringComparison.OrdinalIgnoreCase)
            || cur.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

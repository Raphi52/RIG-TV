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
///   - <see cref="IsDossierLocked"/>         : l'ecran affiche-t-il le verrou "une demande est en cours sur ce dossier" ?
///   - <see cref="IsLoadingOverlay"/>        : l'ecran affiche-t-il l'overlay "Veuillez patienter / Traitement en cours" ?
///   - <see cref="MotifAlreadySelected"/>    : la valeur courante du combo motif correspond-elle deja au motif voulu ?
///   - <see cref="IsCellYVisible"/>          : le centre Y ecran d'une cellule est-il dans la bande visible de la grille ?
///   - <see cref="WheelNotchesToReveal"/>    : crans de molette (signes) pour amener une cellule offscreen dans la vue
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
    /// true si <paramref name="screenText"/> (texte AGREGE de l'ecran DCADEMAT, ex concatenation des
    /// Name des controls Text/Pane UIA) indique une demande VERROUILLEE par un autre utilisateur.
    ///
    /// CONTEXTE (run live, screenshot) : ouvrir au hasard une demande de l'alerte DCADEMAT tombe parfois
    /// sur une demande deja ouverte/lockee par un autre user. L'ecran n'affiche alors AUCUN formulaire
    /// (pas de "Configurer le depot", pas de combo motif), seulement un message rouge du type :
    ///   "Une demande est en cours sur ce dossier - D2608200352 du ... DCADEMAT par RIGAPP23/julien.fontrier"
    /// + un bouton "Quitter". La demande n'est donc PAS exploitable : il faut la fermer et en essayer une autre.
    ///
    /// Detection volontairement LARGE (le message UIA est souvent fragmente en plusieurs Text — on matche
    /// sur le texte agrege, insensible a la casse) : le marqueur central et stable est
    /// "demande est en cours sur ce dossier" (capte aussi "Une demande est en cours sur ce dossier").
    /// On NE matche PAS le simple "en cours" seul (trop ambigu : "Traitement en cours" = overlay de
    /// chargement, cf. <see cref="IsLoadingOverlay"/>, pas un verrou). null/vide -> false.
    /// Pur : (texte ecran) -> bool, aucun effet de bord.
    /// </summary>
    public static bool IsDossierLocked(string? screenText)
    {
        if (string.IsNullOrEmpty(screenText)) return false;
        return screenText.IndexOf("demande est en cours sur ce dossier", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// true si <paramref name="screenText"/> (texte agrege de l'ecran) indique que RIG est encore en train
    /// de CHARGER : overlay/voile "Veuillez patienter..." ou "Traitement en cours...". Pendant cet etat,
    /// chercher le combo/les champs du formulaire est premature (ils n'existent pas encore) -> l'appelant
    /// doit attendre la disparition de l'overlay avant de poursuivre.
    ///
    /// Detection large (insensible a la casse, sur texte agrege) sur les 2 libelles connus :
    /// "veuillez patienter" OU "traitement en cours". null/vide -> false.
    /// Pur : (texte ecran) -> bool, aucun effet de bord.
    /// </summary>
    public static bool IsLoadingOverlay(string? screenText)
    {
        if (string.IsNullOrEmpty(screenText)) return false;
        return screenText.IndexOf("veuillez patienter", StringComparison.OrdinalIgnoreCase) >= 0
            || screenText.IndexOf("traitement en cours", StringComparison.OrdinalIgnoreCase) >= 0;
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

    /// <summary>
    /// true si le centre Y ecran <paramref name="cellCy"/> d'une cellule/ligne est DANS la bande
    /// VISIBLE [<paramref name="viewTop"/> + marge ; <paramref name="viewBottom"/> - marge].
    ///
    /// CONTEXTE (bug run live) : les coordonnees MSAA (accLocation) d'une cellule DataGridView sont
    /// ABSOLUES ecran. Pour une demande en BAS d'une grille scrollable, le Y peut depasser la hauteur
    /// d'ecran (ex Y=4511 sur un ecran de 1080) -> un double-clic a ce point tape HORS de la zone
    /// visible -> rien ne s'ouvre. On verifie donc la visibilite AVANT de cliquer.
    ///
    /// <paramref name="margin"/> : zone de securite haut/bas (defaut 4px) — une ligne dont le centre
    /// tombe pile sur le bord de la grille (en-tete colonne, derniere ligne mi-coupee) n'est pas
    /// fiablement cliquable. viewTop/viewBottom sont typiquement le haut/bas du rectangle ecran de la
    /// GRILLE (pas de tout l'ecran) pour ne pas considerer "visible" une ligne qui tombe sous la grille
    /// mais au-dessus du bas d'ecran. La bande doit etre non vide (viewBottom-viewTop > 2*margin),
    /// sinon -> false (grille degeneree).
    /// Pur : entiers -> bool, aucun effet de bord.
    /// </summary>
    public static bool IsCellYVisible(int cellCy, int viewTop, int viewBottom, int margin = 4)
    {
        if (margin < 0) margin = 0;
        int lo = viewTop + margin;
        int hi = viewBottom - margin;
        if (hi <= lo) return false;
        return cellCy >= lo && cellCy <= hi;
    }

    /// <summary>
    /// Nombre SIGNE de crans de molette (notches) a envoyer a la grille pour amener le centre Y ecran
    /// <paramref name="cellCy"/> au MILIEU de la bande visible [<paramref name="viewTop"/> ;
    /// <paramref name="viewBottom"/>]. Convention molette Windows :
    ///   - notch POSITIF  = molette vers le HAUT  = contenu descend (les Y des lignes AUGMENTENT) ;
    ///   - notch NEGATIF  = molette vers le BAS   = contenu monte   (les Y des lignes DIMINUENT).
    /// Une cellule SOUS la zone (cellCy &gt; centre) doit donc remonter -> notches NEGATIFS ;
    /// une cellule AU-DESSUS (cellCy &lt; centre) doit descendre -> notches POSITIFS.
    ///
    /// On estime le deplacement par cran a <paramref name="rowsPerNotch"/> * <paramref name="rowHeight"/>
    /// pixels (un cran de molette WinForms fait defiler SystemInformation.MouseWheelScrollLines lignes,
    /// 3 par defaut). Le resultat est arrondi au cran le plus proche. cellCy deja dans la bande (delta
    /// faible) -> 0 (rien a scroller). Garde-fous : rowHeight/rowsPerNotch &lt;= 0 -> traite comme 1
    /// (evite la division par zero). Bande degeneree (viewBottom &lt;= viewTop) -> 0.
    /// Le signe et l'amplitude sont PURS ; l'appelant boucle (re-lit les coords apres chaque envoi,
    /// car l'estimation par cran est approximative) jusqu'a <see cref="IsCellYVisible"/>.
    /// </summary>
    public static int WheelNotchesToReveal(int cellCy, int viewTop, int viewBottom, int rowHeight, int rowsPerNotch = 3)
    {
        if (viewBottom <= viewTop) return 0;
        if (rowHeight <= 0) rowHeight = 1;
        if (rowsPerNotch <= 0) rowsPerNotch = 1;
        int center = (viewTop + viewBottom) / 2;
        int deltaPx = center - cellCy;          // >0 : la cellule est AU-DESSUS du centre -> descendre (notch +)
        int pxPerNotch = rowsPerNotch * rowHeight;
        // Arrondi au cran le plus proche (et au moins 1 cran si on est hors-bande mais < 1 cran d'erreur,
        // pour garantir une progression a chaque appel quand l'appelant boucle).
        int notches = (int)Math.Round((double)deltaPx / pxPerNotch, MidpointRounding.AwayFromZero);
        if (notches == 0 && Math.Abs(deltaPx) > pxPerNotch / 2) notches = deltaPx > 0 ? 1 : -1;
        return notches;
    }

    /// <summary>
    /// Regex (compilee) qui capture l'index 1-based d'une ligne de DataGridView dans son Name MSAA.
    /// Un DataGridView WinForms nomme ses lignes accessibles "Ligne N" (locale FR) ou "Row N" (EN),
    /// N etant 1-based (N=1 = 1re ligne data). Le Name d'une LIGNE des demandes (concatenation ';' des
    /// colonnes) ne contient PAS ce motif ; c'est l'enfant LIGNE (role ROW) qui le porte. On accepte
    /// aussi "Ligne N" present n'importe ou dans le texte (ancre lache : certains DGV prefixent le Name).
    /// </summary>
    private static readonly Regex LigneIndexRegex =
        new Regex(@"(?:Ligne|Row)\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Index 0-based d'une ligne de DataGridView a partir de son Name MSAA "Ligne N" / "Row N" (N 1-based).
    /// Sert au FALLBACK clavier d'ouverture d'une demande : focus grille -&gt; Home (1re ligne) -&gt;
    /// Down x (index 0-based) -&gt; Entree. Donc "Ligne 1" -&gt; 0 (deja sur la 1re ligne apres Home, 0 Down),
    /// "Ligne 3" -&gt; 2 (2 Down apres Home).
    ///
    /// Conventions / garde-fous :
    ///   - "Ligne 0" (ligne placeholder/header du DGV WinForms) -&gt; -1 (pas une vraie ligne data, cf.
    ///     <see cref="IsDataRowName"/> qui exclut deja "Ligne 0") ;
    ///   - Name sans motif "Ligne N"/"Row N" (ex Name concatene des colonnes ';') -&gt; -1 (l'appelant
    ///     basculera sur un autre repli) ;
    ///   - null/vide -&gt; -1 ;
    ///   - N negatif impossible (le motif exige \d+), mais par securite tout resultat &lt; 1 -&gt; -1.
    /// Pur : (Name) -&gt; index 0-based ou -1, aucun effet de bord.
    /// </summary>
    public static int ParseLigneIndex(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        var m = LigneIndexRegex.Match(name);
        if (!m.Success) return -1;
        if (!int.TryParse(m.Groups[1].Value, out var oneBased)) return -1;
        if (oneBased < 1) return -1;            // "Ligne 0" = placeholder -> pas de ligne data
        return oneBased - 1;                    // 0-based : nb de Down apres Home
    }
}

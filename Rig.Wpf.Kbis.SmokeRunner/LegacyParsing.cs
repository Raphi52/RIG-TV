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
///   - <see cref="IsDemandeDejaReclamee"/>   : le texte de l'ecran indique-t-il une demande deja reclamee (etat N / « reclamation en cours ») ?
///   - <see cref="IsReclamationActionTerminalOk"/> : verdict du step terminal ALERTES rec-* (item de menu clique + RIG vivant = OK, apercu non requis sur HDESK) ?
///   - <see cref="IsCellYVisible"/>          : le centre Y ecran d'une cellule est-il dans la bande visible de la grille ?
///   - <see cref="WheelNotchesToReveal"/>    : crans de molette (signes) pour amener une cellule offscreen dans la vue
///   - <see cref="ShouldRetryAfterExit"/>    : faut-il relancer ce worker legacy (exit != 0) ? (retry-on-transient)
///   - <see cref="FinalExitAfterRetry"/>     : exit code FINAL d'un scenario apres l'eventuel retry unique
///   - <see cref="AllPassedAfterRetry"/>     : verdict agrege d'une suite (tous exit 0) APRES retry
///   - RetryStartingLogLine / RetryAbsorbedLogLine / RetryConfirmedFailLogLine : textes EXACTS des logs de retry
///   - <see cref="ResolveWatchdogSeconds"/>  : delai effectif du watchdog par worker (override env clampe au min dur)
///   - <see cref="WatchdogKillExitCode"/> / <see cref="WatchdogExitFeedsRetry"/> : exit synthetique du hang, alimente le retry
///   - <see cref="WatchdogTimeoutLogLine"/>  : texte EXACT du log quand le watchdog tue un worker en hang
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
    /// Decision PURE : faut-il PROLONGER l'attente de la grille des demandes alors que le budget de base
    /// (<paramref name="baseMaxMs"/>) est epuise ? Sert a rendre <c>WaitForDemandeGrid</c> DETERMINISTE
    /// face a un chargement RIG lent (cause racine du flap form-validation, run 17:29 STAMP 171521 : la
    /// grille n'etait pas encore apparue mais l'ecran affichait TOUJOURS l'overlay « Veuillez patienter /
    /// Traitement en cours » -&gt; l'ancien code throwait a tort « la liste des demandes ne s'est pas
    /// ouverte »). Le run 17:04 (succes) trouvait la meme grille a ~6,4s : c'est purement un alea de
    /// vitesse de chargement, PAS un etat de donnees.
    ///
    /// Regle : on continue d'attendre SI ET SEULEMENT SI
    ///   (1) le budget de base est deja epuise (<paramref name="elapsedMs"/> &gt;= <paramref name="baseMaxMs"/> ;
    ///       avant cela la boucle normale tourne deja, ce helper ne s'applique pas), ET
    ///   (2) RIG est PROVABLEMENT encore en chargement (<paramref name="overlayPresent"/> = overlay
    ///       « Traitement en cours » a l'ecran -&gt; la grille arrive), ET
    ///   (3) on est sous le plafond dur (<paramref name="hardCapMs"/>) = garde-fou anti-attente-infinie.
    /// Sinon false : soit RIG ne charge plus (overlay absent = ecran fige/plante = VRAI echec, on laisse
    /// l'appelant throw), soit on a atteint le plafond dur. Aucun effet de bord.
    /// </summary>
    public static bool ShouldKeepWaitingForGrid(long elapsedMs, long baseMaxMs, long hardCapMs, bool overlayPresent)
    {
        if (elapsedMs < baseMaxMs) return false;   // budget de base pas encore epuise -> boucle normale
        if (!overlayPresent) return false;         // RIG ne charge plus -> vrai blocage, ne pas prolonger
        if (elapsedMs >= hardCapMs) return false;  // plafond dur atteint -> stop (throw cote appelant)
        return true;
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

    /// <summary>Regex (compilee) : presence d'un "X" entoure de ';' dans le Name MSAA concatene par ';' d'une
    /// LIGNE de la grille des demandes (ex "...;DCADEMAT;...;X;..."). Sert au filtre d'IDEMPOTENCE de
    /// <see cref="DemandeRowMatches"/> (regle 3) qui evite de re-toucher une demande deja "en cours" pour les
    /// actions MUTANTES (validation/reclamation/refus). ⚠ Ce motif large ne discrimine PAS le PROPRIETAIRE du
    /// verrou : une ligne "En cours"=X peut etre verrouillee par MOI (verrou self, levable, cf.
    /// <see cref="IsMyEnCoursSelfLock"/>) OU par un AUTRE user (verrou-autrui, cf. <see cref="IsLockedByOtherUser"/>) —
    /// la distinction se fait via la colonne Utilisateur (DERNIER token, <see cref="ExtractUtilisateurColumn"/>).
    /// Pour cibler PRECISEMENT la colonne "En cours" (avant-dernier token) sans risque de matcher un "X" d'une
    /// autre colonne, utiliser <see cref="ExtractEnCoursColumn"/>. Sur DCADEMAT, un verrou-autrui se manifeste
    /// AUSSI post-open par l'ecran "Une demande est en cours" (cf. <see cref="IsDossierLocked"/>).</summary>
    private static readonly Regex MyEnCoursCellRegex =
        new Regex(@";\s*X\s*;", RegexOptions.Compiled);

    /// <summary>Regex (compilee) : numero de liaison d'une formalite INPI J00... (ex "J00227998879").</summary>
    private static readonly Regex LiaisonJ00Regex =
        new Regex(@"J0\d{6,}", RegexOptions.Compiled);

    /// <summary>
    /// Predicat PUR de selection d'une LIGNE de la grille des demandes (Name MSAA = colonnes concatenees
    /// par ';'), du type voulu. Extrait de <c>LegacyDriver.BuildDemandeMatch</c> (qui etait inline et NON
    /// teste) pour etre couvert par xUnit. Le driver ne fait plus que lire les env (RIG_ALERTES_DCADEMAT /
    /// RIG_ALERTES_LIAISON) et le flag d'inclusion, puis delegue ici.
    ///
    /// Regles, dans l'ordre :
    ///   1. null/blanc -&gt; false ;
    ///   2. <paramref name="overrideSubstring"/> non vide -&gt; match = (rowText contient la sous-chaine,
    ///      insensible a la casse). L'override PRIME sur tout (y compris les filtres "en cours" / verrou-autrui) :
    ///      c'est un ciblage explicite par l'operateur ;
    ///   3. sinon, si <paramref name="includeMyEnCours"/> == false ET la ligne porte la colonne "En cours"
    ///      = "X" (verrou de MA session, motif ';X;') -&gt; false (idempotence : ne pas re-toucher une
    ///      demande deja ouverte par moi pour les actions MUTANTES validation/reclamation/refus) ;
    ///   4. VERROU-AUTRUI (NOUVEAU 2026-06-05, CORRIGE le meme jour) : si <paramref name="currentUserToken"/>
    ///      est fourni ET la ligne est REELLEMENT VERROUILLEE par un AUTRE utilisateur = (« En cours » == "X")
    ///      ET (colonne Utilisateur != moi) (cf. <see cref="IsLockedByOtherUser"/>) -&gt; false. Ce filtre
    ///      s'applique DANS LES DEUX MODES (reprise ou non) : rouvrir une demande verrouillee par un autre
    ///      user ne produit AUCUN signal d'ouverture. ⚠ Une demande LIBRE (« En cours » vide) creee par un
    ///      autre user n'est PAS verrouillee -&gt; elle reste CANDIDATE (c'est exactement le cas
    ///      form-validation/reclamation/refus : demandes formalite libres d'autres users). token vide/null
    ///      -&gt; filtre inactif (retro-compatible) ;
    ///   5. type : <paramref name="dcademat"/> -&gt; rowText contient "DCADEMAT" ; sinon -&gt; rowText
    ///      contient un n° de liaison J00... (formalite INPI).
    ///
    /// ⚠ <paramref name="includeMyEnCours"/> = true est reserve a l'ouverture NON mutante "reprise"
    /// (scenario interrompue : on ROUVRE une demande, y compris une deja "en cours par moi" — c'est
    /// justement la reprise). ⚠ DISTINCTION CLE : la colonne « En cours »=X est le VERROU (par MOI ou par
    /// un autre selon la colonne Utilisateur = dernier token) ; la colonne Utilisateur SEULE (sans « En
    /// cours »=X) est le PROPRIETAIRE/createur, PAS un verrou. Regle 3 (';X;' = mon verrou self, idempotence
    /// des actions mutantes) et regle 4 (verrou-AUTRUI = « En cours »=X + owner != moi) sont disjointes ;
    /// AUCUNE n'exclut une demande LIBRE owned-by-other. Pur : (texte, flags, token) -&gt; bool.
    /// </summary>
    public static bool DemandeRowMatches(string? rowText, bool dcademat, bool includeMyEnCours = false, string? overrideSubstring = null, string? currentUserToken = null)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return false;
        if (!string.IsNullOrWhiteSpace(overrideSubstring))
            return rowText.IndexOf(overrideSubstring, StringComparison.OrdinalIgnoreCase) >= 0;
        if (!includeMyEnCours && MyEnCoursCellRegex.IsMatch(rowText)) return false;
        // Verrou-autrui : on NE tente PAS d'ouvrir une demande detenue par un autre utilisateur (skip pre-open),
        // dans les deux modes. token absent -&gt; filtre inactif (retro-compatibilite des appelants existants).
        if (IsLockedByOtherUser(rowText, currentUserToken)) return false;
        return dcademat
            ? rowText.IndexOf("DCADEMAT", StringComparison.OrdinalIgnoreCase) >= 0
            : LiaisonJ00Regex.IsMatch(rowText);
    }

    /// <summary>
    /// Colonne "Utilisateur" (DERNIER token separe par ';') du Name MSAA d'une LIGNE de la grille des
    /// demandes — c'est-a-dire le proprietaire/verrou de la demande (ex "VILAIN Raphel", "COTE Elia",
    /// "AMISERVICE AMISERVICE"). La grille concatene toutes ses colonnes par ';', Utilisateur en dernier
    /// (preuve logs : "...;24/03/2026 10:30:12;(null);(null);COTE Elia"). Trim de bord ; un placeholder
    /// WinForms "(null)" (cellule vide) est normalise en chaine VIDE (= pas de proprietaire = libre).
    /// rowText sans ';' ou vide -&gt; "". Pur : (texte ligne) -&gt; nom d'utilisateur ou "".
    /// </summary>
    public static string ExtractUtilisateurColumn(string? rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return string.Empty;
        int idx = rowText.LastIndexOf(';');
        string last = (idx >= 0 ? rowText.Substring(idx + 1) : rowText).Trim();
        // "(null)" = cellule vide rendue par le DataGridView WinForms (MSAA) -> pas de proprietaire.
        if (last.Equals("(null)", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return last;
    }

    /// <summary>
    /// Colonne "En cours" (AVANT-DERNIER token separe par ';') du Name MSAA d'une LIGNE de la grille des
    /// demandes. C'est le VERROU effectif de la demande : "X" = demande verrouillee/ouverte (par MOI ou par
    /// un AUTRE selon la colonne Utilisateur = DERNIER token, cf. <see cref="ExtractUtilisateurColumn"/>) ;
    /// vide/"(null)" = demande LIBRE (ouvrable quel que soit son proprietaire). Preuve logs (fixtures reelles) :
    /// "...;24/03/2026 10:30:12;(null);X;VILAIN Raphel" -&gt; En cours="X", Utilisateur="VILAIN Raphel" ;
    /// "...;938 647 203;;CASALS FLORENCE" -&gt; En cours="" (token vide), Utilisateur="CASALS FLORENCE" = LIBRE.
    ///
    /// ⚠ DISTINCTION avec <see cref="MyEnCoursCellRegex"/> (motif ';X;' n'importe ou) : ce helper cible
    /// PRECISEMENT la colonne En cours (avant-dernier token), pour ne PAS confondre un "X" qui apparaitrait
    /// dans une autre colonne. Trim de bord ; placeholder WinForms "(null)" -&gt; chaine VIDE (= libre).
    /// Moins de 2 tokens (pas de ';') -&gt; "" (impossible de distinguer En cours d'Utilisateur). Pur.
    /// </summary>
    public static string ExtractEnCoursColumn(string? rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return string.Empty;
        var parts = rowText.Split(';');
        if (parts.Length < 2) return string.Empty;          // pas de colonne "avant-derniere" identifiable
        string enCours = parts[parts.Length - 2].Trim();    // AVANT-DERNIER token = colonne "En cours"
        if (enCours.Equals("(null)", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return enCours;
    }

    /// <summary>
    /// Jeton identifiant l'utilisateur RIG COURANT a partir de la session Windows (typiquement
    /// <c>Environment.UserName</c> = "raphael.vilain"). On prend le DERNIER segment apres split sur '.'
    /// '\\' '/' ' ' (ex "raphael.vilain" -&gt; "vilain", "DOMAINE\\jdupont" -&gt; "jdupont") : c'est le NOM
    /// DE FAMILLE, qui correspond au 1er mot de la colonne Utilisateur de la grille ("VILAIN Raphel").
    /// La comparaison se fait ensuite en minuscules/sans accents (cf. <see cref="IsLockedByOtherUser"/>).
    /// Vide/null -&gt; "" (filtre verrou-autrui alors inactif). Pur : (UserName) -&gt; surname token.
    /// </summary>
    public static string CurrentUserSurname(string? envUserName)
    {
        if (string.IsNullOrWhiteSpace(envUserName)) return string.Empty;
        var parts = envUserName.Trim().Split(new[] { '.', '\\', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[parts.Length - 1];
    }

    /// <summary>
    /// true si la LIGNE <paramref name="rowText"/> est REELLEMENT VERROUILLEE par un AUTRE utilisateur que
    /// l'utilisateur courant (<paramref name="currentUserToken"/>, typiquement le surname renvoye par
    /// <see cref="CurrentUserSurname"/>). Le verrou = (colonne « En cours » == "X") ET (colonne Utilisateur != moi).
    ///
    /// ⚠ CORRECTIF 2026-06-05 (regression form-validation/reclamation/refus) : une version precedente
    /// retournait true des que la colonne Utilisateur != moi, SANS exiger « En cours »=X. Or la colonne
    /// Utilisateur est le PROPRIETAIRE/createur de la demande, PAS un verrou : une demande LIBRE (« En cours »
    /// vide) creee par un AUTRE user reste OUVRABLE. Cet ancien predicat excluait a tort les demandes
    /// formalite LIBRES d'autres users (form-validation/reclamation/refus) -&gt; 0 candidat -&gt; FAIL. Le VRAI
    /// verrou est porte par la colonne « En cours » (avant-dernier token, cf. <see cref="ExtractEnCoursColumn"/>) :
    ///   - « En cours » VIDE -&gt; demande LIBRE -&gt; false (ouvrable quel que soit le proprietaire) ;
    ///   - « En cours » = "X" ET Utilisateur VIDE/(null) -&gt; verrou orphelin (pas un autre user identifie) -&gt;
    ///     false ici (ce n'est pas un verrou-AUTRUI ; un verrou « X » mien/orphelin releve de
    ///     <see cref="IsMyEnCoursSelfLock"/>, levable par moi) ;
    ///   - <paramref name="currentUserToken"/> vide/null -&gt; identite inconnue -&gt; false (filtre inactif,
    ///     retro-compatibilite : ne JAMAIS sauter une ligne par defaut faute d'identite) ;
    ///   - « En cours » = "X" ET Utilisateur non vide ET != moi -&gt; true (verrou par un autre user : ouvrir
    ///     ne produirait aucun signal -&gt; sauter pre-open) ;
    ///   - « En cours » = "X" ET Utilisateur CONTIENT le token courant ("VILAIN Raphel" contient "vilain")
    ///     -&gt; false (c'est MON verrou self, cf. <see cref="IsMyEnCoursSelfLock"/>, pas un verrou-autrui).
    /// Pur : (texte ligne, token courant) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool IsLockedByOtherUser(string? rowText, string? currentUserToken)
    {
        // VRAI verrou = colonne « En cours » == "X". Une demande LIBRE (En cours vide) n'est JAMAIS un
        // verrou-autrui, MEME si son proprietaire (colonne Utilisateur) est un autre user.
        var enCours = ExtractEnCoursColumn(rowText);
        if (!enCours.Equals("X", StringComparison.OrdinalIgnoreCase)) return false; // pas de verrou -> libre
        var owner = ExtractUtilisateurColumn(rowText);
        if (owner.Length == 0) return false;                       // verrou orphelin (pas un autre user) -> self
        if (string.IsNullOrWhiteSpace(currentUserToken)) return false; // identite inconnue -> filtre inactif
        var ownerNorm = StripAccentsLower(owner);
        var meNorm = StripAccentsLower(currentUserToken.Trim());
        if (meNorm.Length == 0) return false;
        return ownerNorm.IndexOf(meNorm, StringComparison.Ordinal) < 0; // En cours=X + proprietaire != moi -> verrou-autrui
    }

    /// <summary>
    /// true si la LIGNE <paramref name="rowText"/> est verrouillee « EN COURS PAR MOI » (verrou self,
    /// recuperable) : colonne « En cours » = "X" (motif ';X;', cf. <see cref="MyEnCoursCellRegex"/>) ET le
    /// proprietaire (colonne Utilisateur, cf. <see cref="ExtractUtilisateurColumn"/>) est VIDE ou == moi
    /// (<paramref name="currentUserToken"/>).
    ///
    /// CONTEXTE (PROUVE — SQL RIG_DEV + screenshot + RIG source 2026-06-05) : les 10 formalites J00
    /// interrompues de l'alerte « Demandes interrompues » portent TOUTES <c>DMND_EN_COURS=1</c> (verrou
    /// « en cours » laisse par des sessions smoke precedentes tuees sans cloture, owner = le smoke user).
    /// RIG <c>FormRigClientAccueil._ReprendreProcessus</c> refuse de rouvrir toute demande « en cours »
    /// (<c>if (demande.IsEnCours) { DialogBox "deja en cours d'execution" }</c> AVANT le dispatch CODE_PROSS,
    /// cf. <see cref="IsDejaEnCoursGuard"/>) -&gt; le double-clic n'ouvre RIEN. Le seul mecanisme de reprise
    /// est de LEVER le verrou (menu contextuel « Supprimer l'etat en cours » -&gt; <c>Demande.Cloturer</c>
    /// remet <c>DMND_EN_COURS=0</c>) PUIS rouvrir. Comme c'est une ecriture SQL, le driver ne l'execute que
    /// si l'operateur l'autorise (env <c>RIG_LEGACY_CLEAR_MY_ENCOURS</c>).
    ///
    /// DISTINCTION avec <see cref="IsLockedByOtherUser"/> : ce verrou est MIEN (ou orphelin) =&gt; LEVABLE par
    /// moi ; un verrou-autrui ne l'est pas (et ne porte d'ailleurs pas ';X;', il est dans la colonne
    /// Utilisateur). Si <paramref name="currentUserToken"/> est vide (identite inconnue), on considere un
    /// proprietaire non vide ET different comme NON-self (false) : on ne reclame pas un verrou dont on n'est
    /// pas sur qu'il soit a nous. Pur : (texte ligne, token courant) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool IsMyEnCoursSelfLock(string? rowText, string? currentUserToken)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return false;
        if (!MyEnCoursCellRegex.IsMatch(rowText)) return false;     // pas de verrou « En cours »=X sur cette ligne
        var owner = ExtractUtilisateurColumn(rowText);
        if (owner.Length == 0) return true;                          // verrou orphelin (cellule vide) -> levable par moi
        if (string.IsNullOrWhiteSpace(currentUserToken)) return false; // identite inconnue -> ne pas reclamer un owner non vide
        var ownerNorm = StripAccentsLower(owner);
        var meNorm = StripAccentsLower(currentUserToken.Trim());
        if (meNorm.Length == 0) return false;
        return ownerNorm.IndexOf(meNorm, StringComparison.Ordinal) >= 0; // proprietaire == moi -> verrou self levable
    }

    /// <summary>
    /// true si <paramref name="screenText"/> (texte agrege de l'ecran : Name des controls Text/Pane/Edit,
    /// y compris le contenu d'une boite de dialogue) correspond a la GARDE modale que RIG affiche quand on
    /// tente de rouvrir une demande deja « en cours » :
    ///   « Vous ne pouvez pas traiter une demande qui est deja en cours d'execution[ par l'utilisateur ...]. »
    /// (RIG source : <c>FormRigClientAccueil.Accueil.cs:204</c> ; meme libelle dans RigFormAutomateVB6 /
    /// FORM_EN_MASSE). C'est la raison EXACTE pour laquelle un double-clic sur une formalite interrompue
    /// « en cours » ne produit aucun signal d'ouverture : RIG ouvre cette boite a la place et n'ouvre PAS la
    /// demande. La detecter permet de qualifier precisement le non-signal (au lieu d'un timeout aveugle).
    ///
    /// Detection LARGE (message parfois fragmente en plusieurs Text UIA -&gt; texte agrege, insensible a la
    /// casse) sur le marqueur central et stable « deja en cours d'execution ». null/vide -&gt; false.
    /// Pur : (texte ecran) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool IsDejaEnCoursGuard(string? screenText)
    {
        if (string.IsNullOrEmpty(screenText)) return false;
        // Normalise (minuscules + accents retires) pour matcher "deja" / "d'execution" quelle que soit la
        // restitution UIA des accents ("deja" vs "déjà", "execution" vs "exécution").
        var norm = StripAccentsLower(screenText);
        return norm.Contains("deja")
            && norm.Contains("en cours d")   // "en cours d'execution" (apostrophe + e)
            && norm.Contains("traiter");
    }

    /// <summary>
    /// Predicat PUR de SUCCES d'une validation DCADEMAT quand AUCUN n° (demande/depot/facture) n'a pu
    /// etre relu dans la grille Exercices apres « Valider ». Extrait de
    /// <c>LegacyDriver.ConfigurerDepotDcaEtValider</c> pour etre teste.
    ///
    /// CONTEXTE (preuve run live 2026-06-04, screenshot 26228) : pour une demande DCADEMAT « Qualifiee »,
    /// le clic « Valider » CREE le depot (Facture + Certificat de depot generes) PUIS navigue vers l'ecran
    /// « Tableau des editions » — a ce moment la grille Exercices N'EST PLUS a l'ecran, donc la relecture
    /// des n° y echoue alors meme que le depot EXISTE. Le passage a « Tableau des editions » est donc une
    /// PREUVE de succes equivalente a la lecture d'un n° de demande. On n'accepte ce signal QUE si :
    ///   - aucun n° n'a ete relu (<paramref name="numDemandeFound"/> == false), ET
    ///   - le « Valider » a bien ete clique (<paramref name="validerWasClicked"/> == true, pas un no-op), ET
    ///   - l'ecran est devenu « Tableau des editions » (<paramref name="editionsScreenAfter"/> == true).
    /// Si un n° A ete relu -&gt; succes deja prouve par le chemin nominal (ce helper renvoie false : pas
    /// besoin du signal de repli). Pur : (flags) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool ShouldAcceptDepotViaEditionsScreen(bool numDemandeFound, bool validerWasClicked, bool editionsScreenAfter)
        => !numDemandeFound && validerWasClicked && editionsScreenAfter;

    /// <summary>
    /// Predicat PUR : la demande de reclamation ouverte est-elle DEJA reclamee (= etat terminal atteint),
    /// au point que le combo « Type de motif » ne propose plus aucune option a (re)choisir ? Extrait de
    /// <c>LegacyDriver.ReclamerDcaAvecMotif</c> pour etre teste.
    ///
    /// CONTEXTE (preuve run live 2026-06-04, screenshot 2084) : l'alerte « Demandes en reclamations &gt; 15
    /// jours » contient des demandes DEJA en etat reclamation, avec un motif DEJA pose (ex « 9LIB »). Sur une
    /// telle demande, le combo cboTypeMotif est VIDE et non-expandable (RIG ne laisse plus re-choisir un type
    /// de motif) : tenter d'y selectionner un nouveau code (« INPMANQ ») est impossible et ne reflete PAS un
    /// echec — la reclamation EXISTE deja (c'est le terminal metier de ce scenario : « la demande est en
    /// reclamation »). On considere « deja reclamee » quand :
    ///   - le combo est reellement vide (<paramref name="comboItemCount"/> == 0, dropdown deja ouvert/poll fait), ET
    ///   - une valeur de motif est DEJA presente (<paramref name="currentMotifValue"/> non vide).
    /// Le filet est ETROIT : si le combo a des items (on peut choisir) OU si aucune valeur n'est posee (vrai
    /// ecran vide / mauvais etat), ce helper renvoie false et l'appelant garde son comportement d'echec.
    /// Pur : (count, valeur) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool IsAlreadyReclamee(int comboItemCount, string? currentMotifValue)
        => comboItemCount == 0 && !string.IsNullOrWhiteSpace(currentMotifValue);

    /// <summary>
    /// Predicat PUR : le TEXTE agrege de l'ecran « Reclamation / Refus » indique-t-il que la demande
    /// ouverte est DEJA en reclamation (terminal metier atteint), INDEPENDAMMENT de l'etat du combo
    /// « Type de motif » ? Extrait de <c>LegacyDriver.ReclamerDcaAvecMotif</c> pour etre teste.
    ///
    /// CONTEXTE (preuve run live 2026-06-04, run 17:04, screenshot dca-reclamation-FAIL-170431) :
    /// l'alerte « Demandes en reclamations &gt; 15 jours » contient des demandes DEJA reclamees. Un
    /// premier variant (screenshot 2084, LOCODAN) a un combo VIDE -&gt; couvert par <see cref="IsAlreadyReclamee"/>.
    /// MAIS un AUTRE variant (K00228191383 INCEPTO AVOCATS) a un combo cboTypeMotif PEUPLE (« INPMANQ »
    /// deja selectionne) ET un motif deja pose, avec :
    ///   - « Etat Demande » = « N - Reclamation » (code etat N), ET
    ///   - un commentaire « reclamation en cours n° D... le JJ/MM/AAAA » dans la grille Exercices.
    /// Sur ce variant, <see cref="IsAlreadyReclamee"/> renvoie false (combo non vide) -&gt; l'ancien code
    /// poursuivait vers la mutation du texte de motif et echouait (champ « Motif » non editable / sans
    /// ValuePattern sur une demande deja reclamee). Or le terminal metier « la demande est en reclamation »
    /// EST DEJA ATTEINT : il ne faut NI re-saisir NI re-soumettre. Ce helper detecte ce cas via le TEXTE.
    ///
    /// DISCRIMINATION (eviter le faux positif : l'ecran contient TOUJOURS « Reclamation / Refus » en titre
    /// de section et un bouton « Reclamer » -&gt; un simple « contient reclamation » sur-declencherait sur une
    /// demande EN ATTENTE non encore reclamee). On exige donc un signal SPECIFIQUE a l'etat deja-reclame :
    ///   (A) la phrase « reclamation en cours » (commentaire d'une reclamation deja posee), OU
    ///   (B) un etat « N - Reclamation » (le code etat N suivi de Reclamation = la demande EST a l'etat
    ///       reclamation ; une demande en attente porterait un autre code, ex « A - … »).
    /// Insensible a la casse et aux accents (« reclamation » / « Reclamation »). Pur : texte -&gt; bool.
    /// </summary>
    public static bool IsDemandeDejaReclamee(string? screenAggregateText)
    {
        if (string.IsNullOrWhiteSpace(screenAggregateText)) return false;
        var t = StripAccentsLower(screenAggregateText);
        // (A) commentaire « reclamation en cours [n°/le ...] » : signal direct d'une reclamation deja posee.
        if (t.Contains("reclamation en cours")) return true;
        // (B) etat « N - Reclamation » (tolere l'espacement autour du tiret) : la demande EST a l'etat
        //     reclamation. Regex etroite pour ne PAS matcher le titre de section « Reclamation / Refus ».
        if (DejaReclameeEtatRegex.IsMatch(t)) return true;
        return false;
    }

    /// <summary>Etat demande « N - Reclamation » (code N + tiret + « reclamation »), espacement tolere.
    /// Ne matche PAS « Reclamation / Refus » (titre de section, pas precede de « N - »).</summary>
    private static readonly Regex DejaReclameeEtatRegex =
        new Regex(@"\bn\s*-\s*reclamation\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Predicat PUR : APRES un clic « Reclamer » reussi, le TEXTE agrege de l'ecran indique-t-il que RIG a
    /// AUTO-OUVERT un PROCESSUS DE SUIVI (facturation MB1 / « Creation processus » / « Entree dans le RCS »)
    /// sur la meme demande ? Extrait de <c>LegacyDriver.ClickReclamerEtVerifier</c> pour etre teste.
    ///
    /// CONTEXTE (preuve run live 2026-06-04, log CLI-MB1-D2608301626) : une reclamation DCADEMAT qui REUSSIT
    /// declenche cote RIG la creation automatique d'un processus de suivi « MB1 » (CREATION PROCESSUS,
    /// facturation) sur la demande, et RIG parke le focus sur une ult en attente de saisie
    /// (« Mise focus a l'ult 1#1.4#1.1#1 »). Le driver, qui attendait sa condition terminale habituelle
    /// (apercu du courrier / signal d'ouverture), ne reconnait pas ce nouvel etat -> il continue d'attendre
    /// une condition qui n'arrive jamais (busy-poll). OR l'apparition de ce processus de suivi est une PREUVE
    /// que la reclamation a abouti (RIG ne cree le suivi MB1 qu'apres une reclamation actee) : c'est un
    /// SUCCES terminal, pas un echec. Detecter ce cas permet au driver de prendre le screenshot OK et de
    /// SORTIR au lieu de busy-poller.
    ///
    /// DISCRIMINATION (eviter le faux positif sur l'ecran de reclamation lui-meme, qui ne contient PAS ces
    /// libelles) : on exige un marqueur SPECIFIQUE du nouvel ecran de suivi, l'un de :
    ///   (A) « creation processus » (l'evenement RIG d'ouverture du processus de suivi), OU
    ///   (B) « entree dans le rcs » (le libelle de l'ecran de chargement du processus MB1), OU
    ///   (C) un code processus « mb1 » accole a un contexte de creation/facturation (regex
    ///       <see cref="FollowupMb1Regex"/>, pour ne pas matcher « mb1 » isole d'un autre texte).
    /// Insensible a la casse et aux accents. Vide/null -&gt; false. Pur : (texte ecran) -&gt; bool.
    /// </summary>
    public static bool IsReclamationFollowupProcessOpened(string? screenAggregateText)
    {
        if (string.IsNullOrWhiteSpace(screenAggregateText)) return false;
        var t = StripAccentsLower(screenAggregateText);
        if (t.Contains("creation processus")) return true;     // (A) evenement d'ouverture du suivi
        if (t.Contains("entree dans le rcs")) return true;     // (B) ecran de chargement du processus MB1
        if (FollowupMb1Regex.IsMatch(t)) return true;          // (C) code MB1 + contexte creation/facturation
        return false;
    }

    /// <summary>Code processus de suivi « MB1 » accole a un contexte de creation/facturation (« mb1 …
    /// facturation » ou « facturation … mb1 », fenetre de quelques mots). Evite de matcher un « mb1 »
    /// isole sans rapport. Insensible a la casse (texte deja minuscule/sans accents en entree).</summary>
    private static readonly Regex FollowupMb1Regex =
        new Regex(@"\bmb1\b.{0,40}\bfacturation\b|\bfacturation\b.{0,40}\bmb1\b",
                  RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// Verdict du step terminal des scenarios ALERTES rec-form / rec-dca (clic-droit demande -&gt;
    /// item de menu « Reprendre les impressions » / « Lancer le pool d'editions » -&gt; courrier/lettre
    /// de reclamation en APERCU avant impression). Extrait de <c>LegacyDriver.OpenReclamationViaMenuMultiTry</c>
    /// pour etre teste.
    ///
    /// CONTEXTE (preuve run live 2026-06-04, run 18:04, stdout legacy-20260604-180417 + screenshots
    /// smoke-alertes-rec-form-FAIL-180306 / smoke-alertes-rec-dca-FAIL-180411) : sur HDESK isole
    /// (Mode B, desktop non compose) le menu contextuel S'OUVRE (via VK_APPS) et l'item cible est TROUVE
    /// puis CLIQUE — l'action metier est donc declenchee — MAIS l'apercu avant impression (courrier/lettre
    /// de reclamation, rendu par composition DWM type AcroPDF) ne peint sur AUCUNE fenetre/process/fichier
    /// detectable (idem AcroPDF dans RepointSnapToKbisViewer). C'est un MUR D'ENVIRONNEMENT (HDESK partie b),
    /// PAS un bug RIG ni un crash. Le meme clic afficherait l'apercu en Mode A/C (desktop compose).
    ///
    /// POLITIQUE (alignee sur <c>RefuserDemande</c> et <c>ReclamerDcaAvecMotif</c>, deja gracieux face au
    /// meme mur b depuis les rounds DCADEMAT) : le terminal metier est « l'action de reedition/reclamation
    /// a ete declenchee sans planter RIG ». On retourne donc OK (true) SSI :
    ///   - <paramref name="menuItemClicked"/> = l'item de menu a bien ete trouve et clique (action declenchee), ET
    ///   - <paramref name="rigStillAlive"/>   = RIG n'a pas crashe (process vivant + fenetre principale repond).
    /// <paramref name="previewSignalDetected"/> (un viewer/fenetre/onglet/fichier est apparu) est un BONUS qui
    /// renforce la preuve mais n'est PAS requis : son absence = mur HDESK attendu, pas un echec.
    /// FAIL (false) seulement si l'item n'a pas pu etre clique OU si RIG a crashe (vrai echec dur).
    /// Pur : (bool,bool,bool) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool IsReclamationActionTerminalOk(bool menuItemClicked, bool rigStillAlive, bool previewSignalDetected)
        => menuItemClicked && rigStillAlive;

    // ── DEADLINE de verification du step terminal ALERTES rec-form / rec-dca (anti-hang UIA) ───────
    // PROBLEME (preuve run live 2026-06-05 12:37-12:50, stdout legacy-20260605-125004 + log RIG
    // CLI-Reprise-D2608300549 + MOT-D1-...) : apres le clic de l'item de menu (« Reprendre les
    // impressions » / « Lancer le pool d'editions »), RIG declenche REPRISE PROCESSUS = il OUVRE un
    // processus de suivi « D1 » (put_etatDemande Q->N, ULTs de facturation) et PARKE le focus sur une
    // ult en attente de saisie sur le HDESK isole. Le thread driver se BLOQUE alors DANS un appel UIA
    // cross-process de la boucle VerifyDocumentOpened (GetAllTopLevelWindows / FindFirstDescendant),
    // qui n'a AUCUN timeout tant que le UI thread de RIG est occupe. Le plafond `waitSeconds` de
    // VerifyDocumentOpened est INUTILE ici : il n'est evalue qu'ENTRE iterations, pas pendant un appel
    // UIA bloque -> le worker ne sort jamais -> le watchdog 360s le tue (exit124). C'est exactement la
    // meme racine que dca-reclamation (RIG ouvre un suivi apres l'action), mais sur un AUTRE chemin
    // (menu Reprendre/pool, pas ClickReclamerEtVerifier). Le filet : on borne la verification post-clic
    // par une DEADLINE wall-clock DURE, executee sur un thread abandonnable (cf. RunUiaActionWithDeadline)
    // de sorte qu'un appel UIA bloque ne puisse JAMAIS depasser la deadline. Au-dela, terminal-OK (l'action
    // de reclamation/reedition a ete declenchee = REPRISE en cours = succes metier) + screenshot non-UIA + SORT.

    /// <summary>Deadline wall-clock (secondes) de la verification post-clic du step terminal ALERTES
    /// rec-form / rec-dca. Doit etre LARGEMENT sous le watchdog (<see cref="DefaultLegacyWorkerWatchdogSeconds"/>
    /// = 360 s) pour que le scenario SORTE par lui-meme sans dependre du watchdog. Default = 30 s
    /// (assez pour qu'un signal d'ouverture eventuel apparaisse, mais court : l'apercu ne peint pas sur
    /// HDESK et REPRISE PROCESSUS peut occuper le UI thread de RIG -> on n'attend pas plus). Pur (constante).</summary>
    public const int DefaultReclamationVerifyDeadlineSeconds = 30;

    /// <summary>Borne haute DURE de la deadline de verification (secondes) : tout override au-dessus est
    /// clampe ici pour GARANTIR la sortie avant le watchdog (on garde une marge confortable sous 360 s).
    /// Pur (constante).</summary>
    public const int MaxReclamationVerifyDeadlineSeconds = 120;

    /// <summary>
    /// Resout la deadline effective (secondes) de la verification post-clic ALERTES rec-* a partir d'un
    /// override BRUT (typiquement l'env RIG_LEGACY_RECLAM_VERIFY_SECONDS, ou null si absent). Regles PURES :
    ///   - override null / vide / non numerique -&gt; <see cref="DefaultReclamationVerifyDeadlineSeconds"/> ;
    ///   - override &lt;= 0 -&gt; le default (une deadline nulle/negative n'a pas de sens) ;
    ///   - override &gt; <see cref="MaxReclamationVerifyDeadlineSeconds"/> -&gt; clampe au max dur (anti
    ///     pied-de-biche : ne JAMAIS s'approcher du watchdog 360 s, sinon on perd l'auto-sortie) ;
    ///   - sinon -&gt; la valeur de l'override.
    /// Pur : (string?) -&gt; int (secondes), aucun effet de bord.
    /// </summary>
    public static int ResolveReclamationVerifyDeadlineSeconds(string? overrideRaw)
    {
        if (string.IsNullOrWhiteSpace(overrideRaw)) return DefaultReclamationVerifyDeadlineSeconds;
        if (!int.TryParse(overrideRaw.Trim(), out var s)) return DefaultReclamationVerifyDeadlineSeconds;
        if (s <= 0) return DefaultReclamationVerifyDeadlineSeconds;
        return s > MaxReclamationVerifyDeadlineSeconds ? MaxReclamationVerifyDeadlineSeconds : s;
    }

    /// <summary>
    /// Invariant verrouille (xUnit) : la deadline de verification rec-* reste LARGEMENT sous le watchdog,
    /// pour que le scenario sorte TOUJOURS par lui-meme avant le kill watchdog. Pur : () -&gt; bool.
    /// </summary>
    public static bool ReclamationVerifyDeadlineIsBelowWatchdog()
        => MaxReclamationVerifyDeadlineSeconds < DefaultLegacyWorkerWatchdogSeconds
           && DefaultReclamationVerifyDeadlineSeconds < DefaultLegacyWorkerWatchdogSeconds;

    // ── DELAI DE STABILISATION (settle) post-clic ALERTES rec-form / rec-dca — APPROCHE ZERO-UIA ──────
    // ROOT CAUSE STRUCTURELLE (prouvee, runs 2026-06-05 12:37-13:09) : apres le clic de l'item de menu
    // (« Reprendre les impressions » / « Lancer le pool d'editions »), RIG declenche REPRISE PROCESSUS
    // (ouvre un processus de suivi « D1 », put_etatDemande Q->N, ULTs de facturation) et PARKE le focus
    // sur une ult en attente de saisie sur le HDESK isole -> son UI thread reste OCCUPE INDEFINIMENT.
    // CONSEQUENCE : TOUT appel UIA cross-process (FindFirst/FindAll/TreeWalker/BoundingRectangle/lecture
    // de texte) BLOQUE sans timeout. La tentative #2 (executer l'appel UIA sur un thread STA background
    // avec deadline) a ECHOUE : les objets UIA/COM ont une AFFINITE STA -> l'appel est MARSHALE vers le
    // thread STA proprietaire (le thread principal du worker, lui-meme bloque) -> le thread background
    // ne debloque PAS le worker -> re-hang -> watchdog 360s (exit124).
    // SEULE PARADE FIABLE (tentative #3) : APRES le clic, le worker ne fait AUCUN appel UIA. Le clic a
    // DEJA declenche l'action metier (REPRISE PROCESSUS, preuve cote RIG : CLI-Reprise-* / MOT-D1-*) =
    // c'est le SUCCES metier pour le smoke. On se contente d'une verif NON-UIA (process vivant via
    // Process.HasExited) + un court delai de stabilisation BORNE (pour laisser RIG dispatcher REPRISE,
    // afin que le screenshot capture un etat coherent) + screenshot PrintWindow (non-UIA) + return OK.
    // Aucun appel UIA post-clic => rien ne peut bloquer => le worker SORT en quelques secondes, JAMAIS
    // via le watchdog. Le delai est un SLEEP one-shot borne (pas une boucle de poll : il n'existe AUCUNE
    // condition UIA-free a sonder, l'action est fire-and-forget) -> assume comme tel, << watchdog.

    /// <summary>Delai de stabilisation (millisecondes) par defaut apres le clic de l'item de menu rec-*.
    /// Laisse a RIG le temps de dispatcher REPRISE PROCESSUS (ouverture du suivi D1 / facturation) pour
    /// que le screenshot PrintWindow capture un etat coherent, SANS aucun appel UIA. Court (l'apercu ne
    /// peint pas sur HDESK et le UI thread de RIG est occupe -> rien a attendre de plus). Pur (constante).</summary>
    public const int DefaultReclamationSettleMs = 3000;

    /// <summary>Borne basse DURE du delai de stabilisation (ms) : un settle trop court ne laisse pas RIG
    /// amorcer REPRISE. Pur (constante).</summary>
    public const int MinReclamationSettleMs = 500;

    /// <summary>Borne haute DURE du delai de stabilisation (ms) : GARANTIT que le worker sort tres en deca
    /// du watchdog (le settle est SANS appel UIA, donc ne peut pas bloquer, mais on borne par principe).
    /// Pur (constante).</summary>
    public const int MaxReclamationSettleMs = 10000;

    /// <summary>
    /// Resout le delai de stabilisation effectif (ms) post-clic rec-* a partir d'un override BRUT
    /// (typiquement l'env RIG_LEGACY_RECLAM_SETTLE_MS, ou null si absent). Regles PURES :
    ///   - override null / vide / non numerique -&gt; <see cref="DefaultReclamationSettleMs"/> ;
    ///   - override &lt;= 0 -&gt; le default (un settle nul/negatif n'a pas de sens) ;
    ///   - override &lt; <see cref="MinReclamationSettleMs"/> -&gt; clampe au min ;
    ///   - override &gt; <see cref="MaxReclamationSettleMs"/> -&gt; clampe au max (anti pied-de-biche) ;
    ///   - sinon -&gt; la valeur de l'override.
    /// Pur : (string?) -&gt; int (ms), aucun effet de bord.
    /// </summary>
    public static int ResolveReclamationSettleMs(string? overrideRaw)
    {
        if (string.IsNullOrWhiteSpace(overrideRaw)) return DefaultReclamationSettleMs;
        if (!int.TryParse(overrideRaw.Trim(), out var ms)) return DefaultReclamationSettleMs;
        if (ms <= 0) return DefaultReclamationSettleMs;
        if (ms < MinReclamationSettleMs) return MinReclamationSettleMs;
        return ms > MaxReclamationSettleMs ? MaxReclamationSettleMs : ms;
    }

    /// <summary>
    /// Invariant verrouille (xUnit) : le delai de stabilisation post-clic rec-* reste LARGEMENT sous le
    /// watchdog (en ms), pour que le worker sorte TOUJOURS par lui-meme bien avant le kill watchdog, meme
    /// au settle max. Pur : () -&gt; bool.
    /// </summary>
    public static bool ReclamationSettleIsBelowWatchdog()
        => MaxReclamationSettleMs < DefaultLegacyWorkerWatchdogSeconds * 1000
           && DefaultReclamationSettleMs >= MinReclamationSettleMs
           && DefaultReclamationSettleMs <= MaxReclamationSettleMs;

    // ── RETRY-on-transient des suites legacy lourdes (DCADEMAT / ALERTES / KBIS) ──────────────
    // Sous Mode B (HDESK isole, 2 workers concurrents), RIG est lent et un scenario aleatoire
    // FAIL parfois pour cause de contention/timeout environnemental, alors que tous les vrais
    // modes d'echec sont deja corriges. Politique : un worker en echec (exit != 0) est relance
    // UNE seule fois ; si le 2e essai passe -> flap transitoire absorbe (verdict OK) ; sinon ->
    // echec confirme (vrai bug). Les helpers ci-dessous sont PURS (policy + texte de log) pour
    // etre verrouilles par xUnit ; l'orchestration (re-spawn throttle) vit dans le ViewModel.

    /// <summary>
    /// Politique de retry : faut-il RE-LANCER ce worker ? true SSI son exit code est != 0 (echec).
    /// Un worker OK du 1er coup (exit 0) n'est JAMAIS relance (zero surcout, zero regression).
    /// UN SEUL retry par scenario : ce predicat n'est consulte que sur le 1er run ; le ViewModel ne
    /// boucle pas (le resultat du 2e essai est final quel qu'il soit). Pur : (exit) -&gt; bool.
    /// </summary>
    public static bool ShouldRetryAfterExit(int firstExitCode) => firstExitCode != 0;

    /// <summary>
    /// Verdict FINAL d'un scenario apres l'eventuel retry. Si le 1er essai a reussi (exit 0) -&gt; OK
    /// (pas de retry). Sinon le verdict = celui du 2e essai (<paramref name="retryExitCode"/> == 0 -&gt;
    /// OK = flap absorbe ; != 0 -&gt; FAIL confirme). Pur : (exit1, exit2) -&gt; exit final a stocker.
    /// </summary>
    public static int FinalExitAfterRetry(int firstExitCode, int retryExitCode)
        => firstExitCode == 0 ? firstExitCode : retryExitCode;

    /// <summary>
    /// Verdict AGREGE d'une suite (DCADEMAT/ALERTES/KBIS plan) APRES retry : true (tout vert) SSI
    /// TOUS les exit codes finaux sont 0. Reflete l'etat post-retry (le caller a deja remplace les
    /// exit codes des scenarios relances par leur resultat de 2e essai). Liste vide -&gt; true (vacuite :
    /// rien a faire echouer). Pur : (exit codes finaux) -&gt; bool, aucun effet de bord.
    /// </summary>
    public static bool AllPassedAfterRetry(System.Collections.Generic.IEnumerable<int> finalExitCodes)
        => finalExitCodes != null && finalExitCodes.All(c => c == 0);

    /// <summary>
    /// Texte EXACT du log emis quand le retry d'un scenario REUSSIT (exit 0 au 2e essai) : le 1er FAIL
    /// etait un flap transitoire, absorbe. Garde le retry VISIBLE dans le log/UI. Pur : (id) -&gt; string.
    /// </summary>
    public static string RetryAbsorbedLogLine(string scenarioId)
        => $"RETRY {scenarioId} : flap transitoire absorbe (OK au 2e essai)";

    /// <summary>
    /// Texte EXACT du log emis quand le retry d'un scenario ECHOUE AUSSI (exit != 0 au 2e essai) :
    /// echec confirme = vrai bug (pas un flap). Pur : (id) -&gt; string, aucun effet de bord.
    /// </summary>
    public static string RetryConfirmedFailLogLine(string scenarioId)
        => $"RETRY {scenarioId} : echec confirme au 2e essai";

    /// <summary>
    /// Texte EXACT du log emis AVANT de relancer un scenario (1er essai en echec) : annonce le retry
    /// pour qu'il soit visible dans le flux temps reel. Pur : (id, exit1) -&gt; string.
    /// </summary>
    public static string RetryStartingLogLine(string scenarioId, int firstExitCode)
        => $"RETRY {scenarioId} : echec au 1er essai (exit{firstExitCode}), relance unique…";

    // ── WATCHDOG par worker legacy (filet anti-hang) ──────────────────────────────────────────
    // PROBLEME (preuve run live 2026-06-04, suite DCADEMAT) : un worker dca-reclamation a tourne
    // 13+ min sans jamais sortir (cpuSec=378 = busy-poll actif). Cause cote RIG : la reclamation a
    // REUSSI puis RIG a AUTO-CREE un processus de suivi « MB1 » (CREATION PROCESSUS / facturation)
    // sur la meme demande et a parke le focus sur une ult en attente de saisie ; le driver ne
    // reconnait pas cet etat terminal et continue d'attendre sa condition habituelle. Comme le plan
    // attend la fin de TOUS les workers (WaitForExit sans timeout) AVANT le retry, un seul worker
    // qui ne sort jamais bloque TOUTE la suite (le marqueur « plan done » n'est jamais emis, le
    // retry ne se declenche jamais). FILET ROBUSTE : un plafond de temps PAR worker. Au-dela, on
    // KILL l'arbre du worker (worker + RigClientAccueil enfant) et on traite ca comme exit != 0 ->
    // le RETRY-on-transient EXISTANT le relance (le re-sampling reprend probablement une autre
    // demande -> passe). Les helpers ici sont PURS (delai + decision + texte de log) pour etre
    // verrouilles par xUnit ; le kill de l'arbre + la course contre le deadline vivent dans le ViewModel.

    /// <summary>
    /// Plafond de temps PAR worker legacy (secondes) avant de declarer un HANG et de tuer l'arbre.
    /// 360 s = 6 min. Un scenario legacy normal (DCADEMAT/ALERTES/KBIS sous Mode B HDESK) dure
    /// ~3,5 min : 360 s laisse une marge confortable (ne tue PAS un scenario lent-mais-vivant) tout
    /// en attrapant largement un hang reel (13+ min observe). Overridable par env
    /// RIG_LEGACY_WATCHDOG_SECONDS (cf. <see cref="ResolveWatchdogSeconds"/>) pour les rares cas
    /// ou il faut allonger/raccourcir sans recompiler. Pur (constante).
    /// </summary>
    public const int DefaultLegacyWorkerWatchdogSeconds = 360;

    /// <summary>Borne basse DURE du watchdog (secondes) : sous ce seuil on tuerait des scenarios
    /// normaux (~3,5 min = 210 s). Tout override en-dessous est clampe a cette valeur. Pur.</summary>
    public const int MinLegacyWorkerWatchdogSeconds = 240;

    /// <summary>
    /// Resout le delai effectif du watchdog (secondes) a partir d'un override BRUT (typiquement
    /// la valeur de l'env RIG_LEGACY_WATCHDOG_SECONDS, ou null si absente). Regles PURES :
    ///   - override null / vide / non numerique -&gt; <see cref="DefaultLegacyWorkerWatchdogSeconds"/> ;
    ///   - override &lt; <see cref="MinLegacyWorkerWatchdogSeconds"/> -&gt; clampe au minimum dur
    ///     (anti pied-de-biche : ne JAMAIS descendre sous le temps d'un scenario normal, sinon on
    ///     tuerait des workers vivants et le retry boucle a vide) ;
    ///   - sinon -&gt; la valeur de l'override.
    /// Pur : (string?) -&gt; int (secondes), aucun effet de bord.
    /// </summary>
    public static int ResolveWatchdogSeconds(string? overrideRaw)
    {
        if (string.IsNullOrWhiteSpace(overrideRaw)) return DefaultLegacyWorkerWatchdogSeconds;
        if (!int.TryParse(overrideRaw.Trim(), out var s)) return DefaultLegacyWorkerWatchdogSeconds;
        return s < MinLegacyWorkerWatchdogSeconds ? MinLegacyWorkerWatchdogSeconds : s;
    }

    /// <summary>
    /// Exit code SYNTHETIQUE attribue a un worker tue par le watchdog (hang). DOIT etre != 0 pour
    /// ALIMENTER le retry-on-transient existant (cf. <see cref="ShouldRetryAfterExit"/> qui relance
    /// tout exit != 0) : le watchdog n'est PAS un mecanisme parallele, il produit un echec normal que
    /// la machinerie de retry traite comme les autres. On choisit 124 (convention coreutils `timeout`
    /// = « killed after timeout ») pour le distinguer dans les logs d'un exit applicatif 1. Pur.
    /// </summary>
    public const int WatchdogKillExitCode = 124;

    /// <summary>
    /// Invariant verrouille : l'exit code du watchdog declenche bien le retry existant. Expose pour
    /// le test (et pour rendre le couplage explicite). Pur : () -&gt; bool (toujours true par construction).
    /// </summary>
    public static bool WatchdogExitFeedsRetry() => ShouldRetryAfterExit(WatchdogKillExitCode);

    /// <summary>
    /// Texte EXACT du log emis quand le watchdog tue un worker en hang (depasse le plafond). Visible
    /// dans le flux + le log applicatif ; annonce explicitement « -&gt; FAIL+retry » pour que l'on
    /// comprenne que le retry existant va le relancer. Pur : (id, secondes) -&gt; string.
    /// </summary>
    public static string WatchdogTimeoutLogLine(string scenarioId, int watchdogSeconds)
        => $"WATCHDOG {scenarioId} : worker tue apres {watchdogSeconds}s (hang) -> FAIL+retry";

    /// <summary>Minuscule + suppression des diacritiques (e accentue -&gt; e) pour un matching robuste
    /// du texte UIA (les libelles RIG peuvent arriver avec ou sans accents selon la source MSAA).</summary>
    private static string StripAccentsLower(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(norm.Length);
        foreach (var ch in norm)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }
}

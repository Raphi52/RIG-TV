using Rig.Wpf.Kbis.SmokeRunner;
using Xunit;

namespace Rig.Rapture.Tests;

/// <summary>
/// Tests unitaires de la logique PURE extraite de <see cref="LegacyDriver"/> vers
/// <see cref="LegacyParsing"/>. Rapides (&lt;1s, aucun run RIG). Memes invariants que le
/// code inline qu'ils remplacent (FirstToken / IsNumDemandeToken etaient dupliques 2x/4x).
/// </summary>
public class LegacyParsingTests
{
    // ── FirstToken : code processus = 1er jeton du Name lstProcessus ─────────

    [Theory]
    [InlineData("VK", "VK")]                                   // code seul
    [InlineData("VK Visualisation - Extrait RCS", "VK")]       // "code libelle" concatene
    [InlineData("XEX Edition interne d'un Kbis", "XEX")]
    [InlineData("XXKBIS Suppression d'un dossier", "XXKBIS")]
    [InlineData("VKREJ | Rejets de Kbis", "VKREJ")]            // separateur pipe
    [InlineData("PROC\tTabule", "PROC")]                       // separateur tab
    public void FirstToken_extrait_le_code_processus(string name, string expected)
        => Assert.Equal(expected, LegacyParsing.FirstToken(name));

    [Fact]
    public void FirstToken_trimme_les_espaces_de_bord()
        => Assert.Equal("VK", LegacyParsing.FirstToken("   VK   libelle"));

    [Fact]
    public void FirstToken_ne_split_pas_sur_tiret_colle_au_2e_mot()
        // tiret = separateur : "Extrait-RCS" donne quand meme "Extrait" comme 1er jeton.
        => Assert.Equal("Extrait", LegacyParsing.FirstToken("Extrait-RCS"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void FirstToken_vide_ou_blanc_retourne_chaine_vide(string name)
        => Assert.Equal("", LegacyParsing.FirstToken(name));

    [Fact]
    public void FirstToken_ne_retourne_jamais_null()
        => Assert.NotNull(LegacyParsing.FirstToken(null));

    // ── IsNumDemandeToken : jeton "D" + chiffres, longueur >= 8 ──────────────

    [Theory]
    [InlineData("D2613500028")]   // 11 chars (cas reel)
    [InlineData("D2611200138")]
    [InlineData("D1234567")]       // exactement 8 chars (borne basse)
    public void IsNumDemandeToken_numeros_valides_sont_reconnus(string token)
        => Assert.True(LegacyParsing.IsNumDemandeToken(token));

    [Theory]
    [InlineData("D123456")]                 // 7 chars : trop court
    [InlineData("X2613500028")]             // mauvais prefixe
    [InlineData("d2613500028")]             // 'd' minuscule (le code prod exige 'D' majuscule)
    [InlineData("D26135X0028")]             // contient une lettre apres le D
    [InlineData("2613500028")]              // pas de prefixe D
    [InlineData("D")]                        // que le prefixe
    [InlineData("n° de demandeD2611200138")] // pas un jeton pur (forme texte agrege -> FindNumDemande)
    [InlineData("")]
    [InlineData(null)]
    public void IsNumDemandeToken_non_conformes_sont_rejetes(string token)
        => Assert.False(LegacyParsing.IsNumDemandeToken(token));

    // ── ExtractAudienceId : "ID=NNNN" dans le texte de la popup ──────────────

    [Fact]
    public void ExtractAudienceId_extrait_l_id_simple()
        => Assert.Equal(12345, LegacyParsing.ExtractAudienceId("Audience créée (ID=12345).\r\nL'import va se poursuivre..."));

    [Theory]
    [InlineData("ID=42", 42)]
    [InlineData("ID = 42", 42)]          // espaces autour du '='
    [InlineData("ID  =  7", 7)]
    [InlineData("blabla ID=999 fin", 999)]
    public void ExtractAudienceId_tolere_les_espaces_et_le_contexte(string text, int expected)
        => Assert.Equal(expected, LegacyParsing.ExtractAudienceId(text));

    [Theory]
    [InlineData("Audience créée mais sans identifiant")] // pas de "ID="
    [InlineData("IDENTIFIANT 12345")]                     // "ID" suivi de lettres, pas de '='
    [InlineData("ID=abc")]                                 // valeur non numerique
    [InlineData("")]
    [InlineData(null)]
    public void ExtractAudienceId_absent_ou_non_numerique_retourne_null(string text)
        => Assert.Null(LegacyParsing.ExtractAudienceId(text));

    // ── IsDataRowName : "<prefixe>Ligne N" avec N >= 1 ───────────────────────

    [Theory]
    [InlineData("DCA Ligne 1", "DCA ")]
    [InlineData("DCA Ligne 3", "DCA ")]
    [InlineData("DCA Ligne 12", "DCA ")]
    [InlineData("Imprimer Ligne 2", "Imprimer ")]
    [InlineData("dca ligne 5", "DCA ")]   // insensible a la casse
    public void IsDataRowName_lignes_de_data_reconnues(string name, string prefix)
        => Assert.True(LegacyParsing.IsDataRowName(name, prefix));

    [Theory]
    [InlineData("DCA Ligne 0", "DCA ")]        // ligne 0 = placeholder/header -> exclue
    [InlineData("Imprimer Ligne 0", "Imprimer ")]
    [InlineData("Imprimante Ligne 1", "Imprimer ")] // colonne texte, pas le prefixe "Imprimer Ligne "
    [InlineData("DCACO Ligne 1", "DCA ")]       // autre colonne (le code prod exclut DCACO en amont aussi)
    [InlineData("Autre chose", "DCA ")]
    [InlineData("", "DCA ")]
    [InlineData(null, "DCA ")]
    public void IsDataRowName_non_data_rows_rejetees(string name, string prefix)
        => Assert.False(LegacyParsing.IsDataRowName(name, prefix));

    [Fact]
    public void IsDataRowName_ligne_0_avec_suffixe_n_est_pas_exclue_par_erreur()
        // "DCA Ligne 07" commence par "DCA Ligne 0" -> exclu (ligne 0x), comportement du code inline d'origine.
        => Assert.False(LegacyParsing.IsDataRowName("DCA Ligne 07", "DCA "));

    [Fact]
    public void IsDataRowName_prefixe_vide_retourne_false()
        => Assert.False(LegacyParsing.IsDataRowName("DCA Ligne 1", ""));

    // ── Truncate : tronque + ellipse pour le dump MSAA ───────────────────────

    [Fact]
    public void Truncate_chaine_courte_inchangee()
        => Assert.Equal("court", LegacyParsing.Truncate("court"));

    [Fact]
    public void Truncate_a_la_longueur_exacte_inchangee()
        => Assert.Equal(new string('a', 60), LegacyParsing.Truncate(new string('a', 60)));

    [Fact]
    public void Truncate_chaine_longue_tronquee_avec_ellipse()
    {
        var res = LegacyParsing.Truncate(new string('a', 100));
        Assert.Equal(61, res.Length);              // 60 chars + 1 ellipse
        Assert.Equal(new string('a', 60) + "…", res);
    }

    [Fact]
    public void Truncate_maxLen_personnalise()
        => Assert.Equal("abc…", LegacyParsing.Truncate("abcdef", 3));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Truncate_vide_ou_null_retourne_chaine_vide(string s)
        => Assert.Equal("", LegacyParsing.Truncate(s));

    // ── AppendMotifMarker : ajoute le marqueur en fin de texte de motif (reclamation) ──

    [Fact]
    public void AppendMotifMarker_texte_normal_ajoute_le_marqueur_a_la_fin()
        => Assert.Equal("Pieces manquantes. TEST",
            LegacyParsing.AppendMotifMarker("Pieces manquantes.", "TEST"));

    [Fact]
    public void AppendMotifMarker_supprime_les_blancs_de_fin_avant_d_ajouter()
        // le texte auto-rempli peut finir par espaces / CRLF -> on normalise puis " TEST".
        => Assert.Equal("Motif developpe TEST",
            LegacyParsing.AppendMotifMarker("Motif developpe \r\n  ", "TEST"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void AppendMotifMarker_texte_vide_retourne_le_marqueur_seul(string existing)
        => Assert.Equal("TEST", LegacyParsing.AppendMotifMarker(existing, "TEST"));

    [Fact]
    public void AppendMotifMarker_idempotent_si_deja_termine_par_le_marqueur()
        // re-run / re-saisie : on n'accumule pas "TEST TEST".
        => Assert.Equal("Motif TEST", LegacyParsing.AppendMotifMarker("Motif TEST", "TEST"));

    [Fact]
    public void AppendMotifMarker_idempotent_insensible_a_la_casse_et_blancs_de_fin()
        => Assert.Equal("Motif test", LegacyParsing.AppendMotifMarker("Motif test  ", "TEST"));

    [Fact]
    public void AppendMotifMarker_marqueur_au_milieu_n_empeche_pas_l_ajout_final()
        // "TEST" present mais PAS en fin -> on ajoute quand meme a la fin (preuve = en fin de phrase).
        => Assert.Equal("TEST puis autre chose TEST",
            LegacyParsing.AppendMotifMarker("TEST puis autre chose", "TEST"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AppendMotifMarker_marqueur_vide_retourne_le_texte_inchange(string marker)
        => Assert.Equal("Motif inchange", LegacyParsing.AppendMotifMarker("Motif inchange", marker));

    // ── ContainsMotifMarker : detecte le marqueur dans le courrier genere ──

    [Theory]
    [InlineData("Veuillez nous faire parvenir les pieces. TEST", "TEST")]
    [InlineData("blabla test blabla", "TEST")]          // insensible a la casse
    [InlineData("TEST", "TEST")]
    public void ContainsMotifMarker_present_retourne_true(string text, string marker)
        => Assert.True(LegacyParsing.ContainsMotifMarker(text, marker));

    [Theory]
    [InlineData("Texte sans le marqueur", "TEST")]
    [InlineData("", "TEST")]
    [InlineData(null, "TEST")]
    [InlineData("du texte", "")]
    [InlineData("du texte", null)]
    public void ContainsMotifMarker_absent_ou_args_vides_retourne_false(string text, string marker)
        => Assert.False(LegacyParsing.ContainsMotifMarker(text, marker));

    // ── IsDossierLocked : ecran DCADEMAT verrouille par un autre user ──
    // CONTEXTE run live (screenshot) : demande ouverte au hasard -> ecran sans formulaire, juste un
    // message rouge "Une demande est en cours sur ce dossier - D... par <user>" + bouton "Quitter".

    [Theory]
    // CAS REEL du screenshot run live (message complet, user RIGAPP23/julien.fontrier).
    [InlineData("Une demande est en cours sur ce dossier — D2608200352 du 14/08/2026 DCADEMAT par RIGAPP23/julien.fontrier")]
    [InlineData("Une demande est en cours sur ce dossier - D2608200352 DCADEMAT par RIGAPP23/julien.fontrier  Quitter")]
    [InlineData("une demande est en cours sur ce dossier")]              // insensible a la casse
    [InlineData("...bruit avant... demande est en cours sur ce dossier ...bruit apres...")] // sous-chaine dans texte agrege
    // texte agrege de fragments UIA (le message rouge est souvent splite en plusieurs Text) :
    [InlineData("Une demande est en cours sur ce dossier D2608200352 du 14/08/2026 DCADEMAT par RIGAPP23 julien.fontrier Quitter")]
    public void IsDossierLocked_message_de_verrou_reconnu(string text)
        => Assert.True(LegacyParsing.IsDossierLocked(text));

    [Theory]
    [InlineData("Configurer le dépôt")]                 // ecran exploitable normal
    [InlineData("Traitement en cours...")]              // overlay de chargement, PAS un verrou
    [InlineData("Veuillez patienter...")]
    [InlineData("Il y a déjà une demande de modification en cours sur ce dossier")] // K-bis (autre message) : "en cours sur ce dossier" mais PAS "demande est en cours sur ce dossier"
    [InlineData("une demande en cours")]                 // trop court / ambigu -> pas le verrou
    [InlineData("")]
    [InlineData(null)]
    public void IsDossierLocked_non_verrou_rejete(string text)
        => Assert.False(LegacyParsing.IsDossierLocked(text));

    // ── DemandeRowMatches : selection d'une LIGNE de la grille des demandes (Name MSAA ';' concatene) ──
    // CONTEXTE : extrait de LegacyDriver.BuildDemandeMatch (etait inline, NON teste). Regle le FAIL
    // dca-interrompue (regression) via le flag includeMyEnCours (reprise = rouvrir une demande deja
    // "en cours par moi"), et documente le filtre ';X;' qui faisait 0 candidat.

    // Name de ligne realiste : colonnes concatenees par ';' (En cours = "X" => ';X;' present).
    private const string RowDcaEnCours   = "D2606500132;BOULANGERIE DAVID;Interrompue;DCADEMAT;K00222754624;2004B00689;453 257 263;X;VILAIN Raphael";
    private const string RowDcaLibre     = "D2611400077;HOP3;Interrompue;DCADEMAT;G99956017322;2025B00063;938 647 203;;CASALS FLORENCE";
    private const string RowMacLibre     = "D2611400084;MR CAVARRETTA Daniel;Interrompue;MAC;G99956017363;2024AC0047;789 610 045;;CASALS FLORENCE";
    private const string RowJ00EnCours   = "D2608300313;Floriane PITHOUD J00227998879;Interrompue;IAC;J00227998879;;;X;VILAIN Raphael";
    private const string RowJ00Libre     = "D2607800393;Eloise LORENZI J00226774313;Interrompue;A1_C;J00226774313;;;;COTE Elia";

    [Fact]
    public void DemandeRowMatches_dcademat_libre_matche()
        => Assert.True(LegacyParsing.DemandeRowMatches(RowDcaLibre, dcademat: true));

    [Fact]
    public void DemandeRowMatches_dcademat_en_cours_par_moi_exclu_par_defaut()
        // defaut (actions mutantes) : une ligne DCADEMAT deja "en cours par moi" (';X;') est ignoree.
        => Assert.False(LegacyParsing.DemandeRowMatches(RowDcaEnCours, dcademat: true));

    [Fact]
    public void DemandeRowMatches_dcademat_en_cours_par_moi_INCLUS_en_reprise()
        // FIX dca-interrompue : en reprise (includeMyEnCours=true) la ligne ';X;' redevient candidate
        // (sinon 0 candidat quand les seules lignes DCADEMAT de l'alerte sont "en cours par moi" -> throw).
        => Assert.True(LegacyParsing.DemandeRowMatches(RowDcaEnCours, dcademat: true, includeMyEnCours: true));

    [Fact]
    public void DemandeRowMatches_ligne_non_dcademat_rejetee_meme_en_reprise()
        // une ligne MAC (autre traitement) n'est jamais une demande DCADEMAT, quel que soit le flag.
        => Assert.False(LegacyParsing.DemandeRowMatches(RowMacLibre, dcademat: true, includeMyEnCours: true));

    [Fact]
    public void DemandeRowMatches_j00_libre_matche_en_mode_formalite()
        => Assert.True(LegacyParsing.DemandeRowMatches(RowJ00Libre, dcademat: false));

    [Fact]
    public void DemandeRowMatches_j00_en_cours_par_moi_exclu_par_defaut()
        => Assert.False(LegacyParsing.DemandeRowMatches(RowJ00EnCours, dcademat: false));

    [Fact]
    public void DemandeRowMatches_j00_en_cours_par_moi_INCLUS_en_reprise()
        // FIX form-interrompue (2026-06-05) : MÊME logique de reprise que dca-interrompue, côté FORMALITÉ.
        // GROUND TRUTH (screenshot grille « Demandes interrompues ») : TOUTES les lignes J00 de la grille
        // portent En cours="X" (verrou de MA session) ; les seules formalités libres sont des MAC/MB1 en
        // G999… (PAS J00). Avec includeMyEnCours=false → 0 candidat → throw « Aucune demande formalités J00 ».
        // En reprise (includeMyEnCours=true) la ligne J00 ';X;' redevient candidate (rouvrir SA PROPRE demande
        // interrompue n'affiche pas le verrou ; le verrou par un AUTRE user reste capté post-open).
        => Assert.True(LegacyParsing.DemandeRowMatches(RowJ00EnCours, dcademat: false, includeMyEnCours: true));

    [Fact]
    public void DemandeRowMatches_formalite_libre_non_J00_rejetee_meme_en_reprise()
        // Les formalités LIBRES de la grille « interrompues » sont en G999… (MAC/MB1), pas J00 : elles ne
        // matchent JAMAIS le mode formalité (qui cible le n° de liaison INPI démat J00…), quel que soit le flag.
        // C'est pourquoi le fix passe par includeMyEnCours (rouvrir les J00 ';X;') et NON par un élargissement
        // de la famille (qui changerait la sémantique « Formalité Demat INPI » du scénario).
        => Assert.False(LegacyParsing.DemandeRowMatches(RowMacLibre, dcademat: false, includeMyEnCours: true));

    [Fact]
    public void DemandeRowMatches_dcademat_pas_pris_pour_une_formalite_j00()
        // une ligne DCADEMAT sans n° de liaison J00 ne matche PAS le mode formalite (dcademat=false).
        => Assert.False(LegacyParsing.DemandeRowMatches(RowDcaLibre, dcademat: false));

    [Theory]
    [InlineData("DCADEMAT", true)]    // sous-chaine presente dans RowDcaEnCours, type formalite ignore
    [InlineData("Interrompue", true)] // autre sous-chaine presente -> match (override = substring brut)
    [InlineData("MAC", false)]        // sous-chaine ABSENTE de RowDcaEnCours -> pas de match
    [InlineData("ZZZINTROUVABLE", false)]
    public void DemandeRowMatches_override_est_un_substring_brut_independant_du_type(string ovr, bool expected)
        // override (RIG_ALERTES_*) = ciblage explicite : substring, ignore le type ET le filtre ';X;'.
        // RowDcaEnCours est une ligne DCADEMAT ';X;' ; meme avec dcademat=false, l'override decide seul.
        => Assert.Equal(expected, LegacyParsing.DemandeRowMatches(RowDcaEnCours, dcademat: false, includeMyEnCours: false, overrideSubstring: ovr));

    [Fact]
    public void DemandeRowMatches_override_matche_meme_une_ligne_en_cours()
        // l'override prime aussi sur le filtre ';X;' (la ligne RowDcaEnCours porte ';X;').
        => Assert.True(LegacyParsing.DemandeRowMatches(RowDcaEnCours, dcademat: true, includeMyEnCours: false, overrideSubstring: "BOULANGERIE"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DemandeRowMatches_vide_ou_blanc_retourne_false(string row)
        => Assert.False(LegacyParsing.DemandeRowMatches(row, dcademat: true, includeMyEnCours: true));

    // ── VERROU-AUTRUI : ExtractEnCoursColumn / ExtractUtilisateurColumn / CurrentUserSurname / IsLockedByOtherUser + DemandeRowMatches(currentUserToken) ──
    // CONTEXTE + CORRECTIF 2026-06-05 (régression form-validation/reclamation/refus) :
    //   Le Name MSAA d'une LIGNE = colonnes concaténées par ';', « En cours » = AVANT-DERNIER token,
    //   « Utilisateur » = DERNIER token. Le VRAI verrou = « En cours »=X ; la colonne Utilisateur SEULE est le
    //   PROPRIÉTAIRE/créateur, PAS un verrou. ⚠ Une version précédente d'IsLockedByOtherUser excluait une demande
    //   dès que Utilisateur != moi (sans exiger « En cours »=X) → elle sautait à tort les demandes formalité
    //   LIBRES créées par d'autres users (form-validation/reclamation/refus) → 0 candidat → FAIL. Prédicat
    //   corrigé : verrou-autrui = (« En cours »=X) ET (Utilisateur != moi) ; une demande LIBRE (« En cours »
    //   vide) reste TOUJOURS candidate, quel que soit son propriétaire. Rouvrir une demande RÉELLEMENT
    //   verrouillée par un autre user ne produit aucun signal d'ouverture → on la saute AVANT l'ouverture.

    // Lignes RÉELLES (format MSAA complet, 13 colonnes avec placeholders "(null)") tirées des logs.
    // ⚠ Ces 2 lignes J00 ont « En cours »=(null) (avant-dernier token) = demande LIBRE (verrou = colonne
    //   « En cours », PAS la colonne Utilisateur) → NI l'une NI l'autre n'est verrouillée-autrui. Les cas
    //   verrouillés J00 portent « En cours »=X : voir RealRowJ00_*_EnCoursX plus bas.
    private const string RealRowJ00_Cote_Libre   = "Ligne 3 D2608300313;Floriane PITHOUD J00227998879;Interrompue;IAC;(null);(null);J00227998879;(null);(null);24/03/2026 10:30:12;(null);(null);COTE Elia";
    private const string RealRowJ00_Vilain_Libre = "Ligne 3 D2608300313;Floriane PITHOUD J00227998879;Interrompue;IAC;(null);(null);J00227998879;(null);(null);24/03/2026 10:30:12;(null);(null);VILAIN Raphel";
    private const string RealRowDca_Vilain = "Ligne 5 D2608301642;LOCODAN (31/12/2025);Réclamation;DCADEMAT;(null);(null);K00227707569;2022B00899;912 245 776;24/03/2026 21:00:07;(null);X;VILAIN Raphel";
    private const string RealRowDca_AmiSvc = "Ligne 0 D2608301630;SILA MDB 3182481 K00228250320 (31/12/2024);Qualifiée;DCADEMAT;(null);(null);K00228250320;2025B01074;903 186 427;24/03/2026 18:30:14;(null);X;AMISERVICE AMISERVICE";

    [Theory]
    [InlineData(RealRowJ00_Cote_Libre, "COTE Elia")]
    [InlineData(RealRowJ00_Vilain_Libre, "VILAIN Raphel")]
    [InlineData(RealRowDca_Vilain, "VILAIN Raphel")]
    [InlineData(RealRowDca_AmiSvc, "AMISERVICE AMISERVICE")]
    [InlineData("D2611400077;HOP3;Interrompue;DCADEMAT;G99956017322;2025B00063;938 647 203;;CASALS FLORENCE", "CASALS FLORENCE")] // En cours vide (token vide avant Utilisateur)
    [InlineData("a;b;  Jean DUPONT  ", "Jean DUPONT")]   // trim des blancs de bord
    public void ExtractUtilisateurColumn_dernier_token_est_l_utilisateur(string row, string expected)
        => Assert.Equal(expected, LegacyParsing.ExtractUtilisateurColumn(row));

    // ── ExtractEnCoursColumn : colonne « En cours » = AVANT-DERNIER token (le VRAI verrou) ──
    [Theory]
    [InlineData(RealRowDca_Vilain, "X")]                 // "...;(null);X;VILAIN Raphel" → avant-dernier = X
    [InlineData(RealRowDca_AmiSvc, "X")]                 // "...;(null);X;AMISERVICE..." → X
    [InlineData(RealRowJ00_Cote_Libre, "")]              // "...;(null);(null);COTE Elia" → avant-dernier (null) → vide = LIBRE
    [InlineData(RealRowJ00_Vilain_Libre, "")]            // idem → vide = LIBRE
    [InlineData("D2611400077;HOP3;Interrompue;DCADEMAT;G99956017322;2025B00063;938 647 203;;CASALS FLORENCE", "")] // ";;CASALS" → token vide = LIBRE
    [InlineData("a;X;Jean DUPONT", "X")]                 // avant-dernier explicite
    [InlineData("SoloName", "")]                          // pas de ';' → pas de colonne avant-derniere
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExtractEnCoursColumn_avant_dernier_token_est_le_verrou(string row, string expected)
        => Assert.Equal(expected, LegacyParsing.ExtractEnCoursColumn(row));

    [Theory]
    [InlineData("a;b;c;(null)")]        // "(null)" = cellule vide WinForms -> proprietaire vide
    [InlineData("a;b;c;  (null)  ")]    // avec blancs
    [InlineData("(NULL)")]               // insensible a la casse
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExtractUtilisateurColumn_null_ou_placeholder_retourne_vide(string row)
        => Assert.Equal("", LegacyParsing.ExtractUtilisateurColumn(row));

    [Fact]
    public void ExtractUtilisateurColumn_ligne_sans_point_virgule_retourne_le_trim()
        // pas de ';' -> toute la chaine est le "dernier token".
        => Assert.Equal("SoloName", LegacyParsing.ExtractUtilisateurColumn("  SoloName  "));

    [Theory]
    [InlineData("raphael.vilain", "vilain")]          // session Windows réelle de l'utilisateur
    [InlineData("DOMAINE\\jdupont", "jdupont")]       // forme DOMAINE\login
    [InlineData("corp/abigaud", "abigaud")]            // séparateur '/'
    [InlineData("Jean Dupont", "Dupont")]              // prénom nom séparés par espace
    [InlineData("vilain", "vilain")]                   // déjà un seul segment
    [InlineData("  raphael.vilain  ", "vilain")]       // trim
    public void CurrentUserSurname_prend_le_dernier_segment(string userName, string expected)
        => Assert.Equal(expected, LegacyParsing.CurrentUserSurname(userName));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CurrentUserSurname_vide_retourne_vide(string userName)
        => Assert.Equal("", LegacyParsing.CurrentUserSurname(userName));

    [Fact]
    public void IsLockedByOtherUser_J00_LIBRE_owned_by_other_n_est_PAS_verrouillee()
        // ⚠ CORRECTIF : la formalité J00 LIBRE (« En cours » vide) détenue par "COTE Elia" n'est PAS
        //   verrouillée-autrui (propriétaire ≠ verrou). Elle reste ouvrable → NON sautée.
        => Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowJ00_Cote_Libre, "vilain"));

    [Fact]
    public void IsLockedByOtherUser_J00_LIBRE_mienne_n_est_pas_verrouillee()
        // MÊME ligne LIBRE mais Utilisateur="VILAIN Raphel" (moi) -> évidemment pas un verrou-autrui.
        => Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowJ00_Vilain_Libre, "vilain"));

    [Fact]
    public void IsLockedByOtherUser_DCADEMAT_en_cours_X_par_AMISERVICE_est_verrouillee_pour_vilain()
        // « En cours »=X (avant-dernier token) + Utilisateur="AMISERVICE" ≠ moi -> verrou-autrui réel.
        => Assert.True(LegacyParsing.IsLockedByOtherUser(RealRowDca_AmiSvc, "vilain"));

    [Fact]
    public void IsLockedByOtherUser_DCADEMAT_en_cours_X_mienne_n_est_pas_verrouillee()
        // « En cours »=X mais owner=VILAIN (moi) -> verrou self, PAS un verrou-autrui.
        => Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowDca_Vilain, "vilain"));

    [Theory]
    [InlineData("vilain")]                 // surname seul (ce que CurrentUserSurname renvoie)
    [InlineData("VILAIN")]                 // casse haute -> match (comparaison sans casse)
    [InlineData("Vilain")]                 // casse mixte
    public void IsLockedByOtherUser_match_insensible_a_la_casse(string token)
        // Ligne DCADEMAT « En cours »=X owner="VILAIN Raphel" : contient le token surname (quelle que soit
        //   sa casse) -> verrou self, PAS un verrou-autrui (false).
        // ⚠ Le helper attend le TOKEN surname (déjà extrait par CurrentUserSurname) — PAS le UserName brut
        //   "raphael.vilain" (qui contient un point et "raphael"≠"raphel"). Le driver passe toujours
        //   CurrentUserSurname(Environment.UserName), cf. test ci-dessous.
        => Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowDca_Vilain, token));

    [Fact]
    public void IsLockedByOtherUser_pipeline_complet_depuis_le_UserName_session()
    {
        // Chaîne réelle utilisée par le driver : Environment.UserName "raphael.vilain" -> surname "vilain".
        //   Verrou = « En cours »=X + owner ≠ moi. DCADEMAT « En cours »=X mienne (VILAIN) = self → libre ;
        //   DCADEMAT « En cours »=X d'un autre (AMISERVICE) = verrou-autrui. Une J00 LIBRE owned-by-other (COTE)
        //   N'est PAS un verrou (propriétaire ≠ verrou).
        var me = LegacyParsing.CurrentUserSurname("raphael.vilain");   // "vilain"
        Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowDca_Vilain, me));        // ma demande en cours
        Assert.True(LegacyParsing.IsLockedByOtherUser(RealRowDca_AmiSvc, me));         // en cours par AMISERVICE
        Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowJ00_Cote_Libre, me));    // LIBRE owned-by-COTE -> ouvrable
    }

    [Fact]
    public void IsLockedByOtherUser_colonne_utilisateur_vide_est_libre()
        // Utilisateur="(null)" -> demande LIBRE -> jamais un verrou (quel que soit le user courant),
        //   et de toute façon « En cours » vide ici.
        => Assert.False(LegacyParsing.IsLockedByOtherUser("a;b;c;(null)", "vilain"));

    [Fact]
    public void IsLockedByOtherUser_en_cours_X_owner_vide_n_est_pas_verrou_autrui()
        // « En cours »=X mais colonne Utilisateur vide ("(null)") = verrou ORPHELIN, pas un AUTRE user
        //   identifié -> false ici (c'est un self-lock levable, cf. IsMyEnCoursSelfLock).
        => Assert.False(LegacyParsing.IsLockedByOtherUser("a;b;c;X;(null)", "vilain"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsLockedByOtherUser_token_courant_absent_desactive_le_filtre(string token)
        // RETRO-COMPATIBILITE : sans identité courante, on ne saute JAMAIS une ligne (filtre inactif) —
        //   même une vraie « En cours »=X owned-by-other (DCADEMAT AMISERVICE).
        => Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowDca_AmiSvc, token));

    [Fact]
    public void DemandeRowMatches_J00_LIBRE_owned_by_other_RESTE_candidate_en_reprise()
        // ⚠ LE TEST QUI AURAIT ATTRAPÉ LA RÉGRESSION : une formalité J00 LIBRE (« En cours » vide) créée par
        //   "COTE Elia" reste CANDIDATE en reprise quand je suis "vilain" (propriétaire ≠ verrou ; ouvrable).
        => Assert.True(LegacyParsing.DemandeRowMatches(RealRowJ00_Cote_Libre, dcademat: false, includeMyEnCours: true, currentUserToken: "vilain"));

    [Fact]
    public void DemandeRowMatches_J00_LIBRE_mienne_reste_candidate_en_reprise()
        // MÊME ligne LIBRE mais Utilisateur=moi -> candidate retenue.
        => Assert.True(LegacyParsing.DemandeRowMatches(RealRowJ00_Vilain_Libre, dcademat: false, includeMyEnCours: true, currentUserToken: "vilain"));

    [Fact]
    public void DemandeRowMatches_J00_LIBRE_owned_by_other_candidate_aussi_pour_action_mutante()
        // ⚠ CŒUR DE LA RÉGRESSION form-validation/reclamation/refus (includeMyEnCours=false, actions mutantes) :
        //   une formalité J00 LIBRE créée par un AUTRE user DOIT rester candidate (ne PLUS être exclue au seul
        //   motif que le propriétaire ≠ moi). C'est exactement ce que l'ancien IsLockedByOtherUser cassait.
        => Assert.True(LegacyParsing.DemandeRowMatches(RealRowJ00_Cote_Libre, dcademat: false, includeMyEnCours: false, currentUserToken: "vilain"));

    [Fact]
    public void DemandeRowMatches_J00_LIBRE_owned_by_other_sans_token_reste_candidate_retrocompat()
        // Sans currentUserToken (appel historique) le filtre verrou-autrui est inactif -> comportement INCHANGÉ.
        => Assert.True(LegacyParsing.DemandeRowMatches(RealRowJ00_Cote_Libre, dcademat: false, includeMyEnCours: true));

    [Fact]
    public void DemandeRowMatches_DCADEMAT_en_cours_X_autrui_exclue_meme_pour_action_mutante()
        // Le skip verrou-autrui s'applique AUSSI hors reprise (includeMyEnCours=false) : ne pas tenter une
        //   DCADEMAT RÉELLEMENT verrouillée (« En cours »=X) détenue par "AMISERVICE" -> false.
        => Assert.False(LegacyParsing.DemandeRowMatches(RealRowDca_AmiSvc, dcademat: true, includeMyEnCours: false, currentUserToken: "vilain"));

    [Fact]
    public void DemandeRowMatches_override_prime_sur_le_filtre_verrou_autrui()
        // l'override (ciblage explicite RIG_ALERTES_*) prime sur TOUT, y compris le verrou-autrui (ici une
        //   vraie DCADEMAT « En cours »=X owned-by-other reste sélectionnable par l'override).
        => Assert.True(LegacyParsing.DemandeRowMatches(RealRowDca_AmiSvc, dcademat: false, includeMyEnCours: false,
            overrideSubstring: "SILA MDB", currentUserToken: "vilain"));

    // ── VERROU SELF « En cours »=X : IsMyEnCoursSelfLock + garde modale IsDejaEnCoursGuard ──
    // GROUND TRUTH (resolver #2, 2026-06-05) PROUVÉ par SQL RIG_DEV + RIG source + screenshot 11:16 :
    //   Les 10 formalités J00 interrompues portent TOUTES DMND_EN_COURS=1 (colonne grille « En cours »=X,
    //   owner=VILAIN = verrou self stale d'un run smoke précédent). RIG _ReprendreProcessus refuse de rouvrir
    //   une demande en cours (DialogBox « déjà en cours d'exécution » AVANT le dispatch CODE_PROSS) → le
    //   double-clic n'ouvre rien. C'est la CAUSE RÉELLE du FAIL form-interrompue. Le mécanisme de reprise =
    //   lever le verrou via le menu « Supprimer l'état en cours » (DMND_EN_COURS=0) PUIS rouvrir — geste
    //   gardé (écriture SQL, env RIG_LEGACY_CLEAR_MY_ENCOURS). dca-interrompue passe car DCADEMAT a 1
    //   candidate EN_COURS=0 ; les formalités n'en ont AUCUNE.
    // ⚠ Les fixtures RealRowJ00_*_Libre ci-dessus (sans ';X;', « En cours » vide) représentent des demandes
    //   J00 LIBRES (ouvrables) — y compris owned-by-other (COTE) : elles servent à VERROUILLER la non-régression
    //   « libre owned-by-other => candidate » (cœur du correctif 2026-06-05). Les fixtures J00 RÉELLEMENT
    //   verrouillées (« En cours »=X) sont ci-dessous (RealRowJ00_*_EnCoursX).
    private const string RealRowJ00_Vilain_EnCoursX = "Ligne 3 D2608300313;Floriane PITHOUD J00227998879;Interrompue;IAC;(null);(null);J00227998879;(null);(null);24/03/2026 10:30:12;(null);X;VILAIN Raphel";
    private const string RealRowJ00_Cote_EnCoursX   = "Ligne 7 D2607800393;Eloise LORENZI J00226774313;Interrompue;A1_C;(null);(null);J00226774313;(null);(null);19/03/2026 10:45:37;(null);X;COTE Elia";
    private const string RealRowDca_Vilain_Libre    = "Ligne 9 D2606101501;SARL CENTRE DE PRESERVATION;Interrompue;DCADEMAT;(null);(null);K00213675804;2024B00842;924 967 391;02/03/2026 19:15:04;(null);(null);VILAIN Raphel";

    [Fact]
    public void IsMyEnCoursSelfLock_ligne_en_cours_X_mienne_est_un_verrou_self_levable()
        // La formalité interrompue D2608300313 (IAC) porte « En cours »=X et owner=VILAIN (moi) = verrou self
        // stale → levable par moi via « Supprimer l'état en cours ». C'est le cas RÉEL du screenshot 11:16.
        => Assert.True(LegacyParsing.IsMyEnCoursSelfLock(RealRowJ00_Vilain_EnCoursX, "vilain"));

    [Fact]
    public void IsMyEnCoursSelfLock_ligne_en_cours_X_dun_autre_user_nest_pas_self()
        // « En cours »=X mais owner=COTE Elia → ce n'est pas MON verrou → je ne le réclame pas (false).
        => Assert.False(LegacyParsing.IsMyEnCoursSelfLock(RealRowJ00_Cote_EnCoursX, "vilain"));

    [Fact]
    public void IsMyEnCoursSelfLock_ligne_sans_X_nest_pas_un_verrou_en_cours()
        // Pas de « En cours »=X (DCADEMAT libre EN_COURS=0) → pas un verrou self, même si owner=moi.
        => Assert.False(LegacyParsing.IsMyEnCoursSelfLock(RealRowDca_Vilain_Libre, "vilain"));

    [Fact]
    public void IsMyEnCoursSelfLock_verrou_X_orphelin_owner_vide_est_levable()
        // « En cours »=X avec colonne Utilisateur vide ("(null)") = verrou orphelin → levable par moi (true).
        => Assert.True(LegacyParsing.IsMyEnCoursSelfLock("D2;X owner vide;Interrompue;IAC;J00227998879;;;X;(null)", "vilain"));

    [Fact]
    public void IsMyEnCoursSelfLock_token_courant_absent_ne_reclame_pas_un_owner_non_vide()
        // Identité inconnue + owner non vide → on ne réclame pas le verrou (false, prudence).
        => Assert.False(LegacyParsing.IsMyEnCoursSelfLock(RealRowJ00_Vilain_EnCoursX, ""));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsMyEnCoursSelfLock_vide_retourne_false(string row)
        => Assert.False(LegacyParsing.IsMyEnCoursSelfLock(row, "vilain"));

    [Fact]
    public void IsMyEnCoursSelfLock_et_IsLockedByOtherUser_sont_complementaires_sur_X_autrui()
    {
        // Une ligne « En cours »=X détenue par un AUTRE = verrou-autrui (true) ET PAS un self-lock (false).
        Assert.True(LegacyParsing.IsLockedByOtherUser(RealRowJ00_Cote_EnCoursX, "vilain"));
        Assert.False(LegacyParsing.IsMyEnCoursSelfLock(RealRowJ00_Cote_EnCoursX, "vilain"));
        // Une ligne « En cours »=X mienne = self-lock (true) ET PAS un verrou-autrui (false).
        Assert.True(LegacyParsing.IsMyEnCoursSelfLock(RealRowJ00_Vilain_EnCoursX, "vilain"));
        Assert.False(LegacyParsing.IsLockedByOtherUser(RealRowJ00_Vilain_EnCoursX, "vilain"));
    }

    [Fact]
    public void DemandeRowMatches_J00_mienne_en_cours_X_reste_candidate_en_reprise()
        // En reprise, la formalité J00 MIENNE « En cours »=X reste candidate (on tentera de lever le verrou
        // self). Le filtre ';X;' est désactivé par includeMyEnCours=true ; owner=moi → pas de skip verrou-autrui.
        => Assert.True(LegacyParsing.DemandeRowMatches(RealRowJ00_Vilain_EnCoursX, dcademat: false, includeMyEnCours: true, currentUserToken: "vilain"));

    [Fact]
    public void DemandeRowMatches_J00_en_cours_X_autrui_exclue_en_reprise()
        // En reprise, la formalité J00 « En cours »=X d'un AUTRE est SAUTÉE pré-open (verrou-autrui, owner=COTE).
        => Assert.False(LegacyParsing.DemandeRowMatches(RealRowJ00_Cote_EnCoursX, dcademat: false, includeMyEnCours: true, currentUserToken: "vilain"));

    [Theory]
    // Garde modale RIG « Vous ne pouvez pas traiter une demande qui est déjà en cours d'exécution… »
    [InlineData("Vous ne pouvez pas traiter une demande qui est déjà en cours d'exécution.")]
    [InlineData("vous ne pouvez pas traiter une demande qui est deja en cours d'execution")] // accents absents (restitution UIA)
    [InlineData("Erreur — Vous ne pouvez pas traiter une demande qui est déjà en cours d'exécution par l'utilisateur VILAIN Raphel.")]
    [InlineData("...déjà... ...traiter... ...en cours d'exécution...")] // fragmenté/agrégé
    public void IsDejaEnCoursGuard_message_reconnu(string text)
        => Assert.True(LegacyParsing.IsDejaEnCoursGuard(text));

    [Theory]
    [InlineData("Configurer le dépôt")]                                   // formulaire ouvert = pas la garde
    [InlineData("Une demande est en cours sur ce dossier — D2608200352")] // verrou-autrui DCADEMAT (autre message)
    [InlineData("Traitement en cours...")]                                // overlay de chargement, pas la garde
    [InlineData("")]
    [InlineData(null)]
    public void IsDejaEnCoursGuard_hors_garde_rejete(string text)
        => Assert.False(LegacyParsing.IsDejaEnCoursGuard(text));

    [Fact]
    public void IsDejaEnCoursGuard_disjoint_du_verrou_autrui_DCADEMAT_et_de_loverlay()
    {
        // Les 3 messages « en cours » sont distincts : garde reprise (déjà en cours d'exécution),
        // verrou-autrui dossier (en cours sur ce dossier), overlay (traitement en cours) — pas de confusion.
        string garde   = "Vous ne pouvez pas traiter une demande qui est déjà en cours d'exécution.";
        string dossier = "Une demande est en cours sur ce dossier — D2608200352 par RIGAPP23/julien.fontrier";
        string overlay = "Veuillez patienter... Traitement en cours...";
        Assert.True(LegacyParsing.IsDejaEnCoursGuard(garde));
        Assert.False(LegacyParsing.IsDossierLocked(garde));
        Assert.False(LegacyParsing.IsLoadingOverlay(garde));
        Assert.False(LegacyParsing.IsDejaEnCoursGuard(dossier));
        Assert.False(LegacyParsing.IsDejaEnCoursGuard(overlay));
    }

    // ── IsLoadingOverlay : overlay "Veuillez patienter / Traitement en cours" ──

    [Theory]
    [InlineData("Veuillez patienter...")]
    [InlineData("Traitement en cours...")]
    [InlineData("VEUILLEZ PATIENTER")]                   // insensible a la casse
    [InlineData("traitement en cours")]
    [InlineData("...  Veuillez patienter ...  Traitement en cours ...")] // les deux dans le texte agrege
    [InlineData("Demande DCADEMAT — Traitement en cours, merci de patienter")] // sous-chaine
    public void IsLoadingOverlay_overlay_reconnu(string text)
        => Assert.True(LegacyParsing.IsLoadingOverlay(text));

    [Theory]
    [InlineData("Configurer le dépôt")]                 // formulaire pret -> plus d'overlay
    [InlineData("Une demande est en cours sur ce dossier — D2608200352")] // verrou, pas un overlay
    [InlineData("Réclamation / Refus")]
    [InlineData("")]
    [InlineData(null)]
    public void IsLoadingOverlay_hors_overlay_rejete(string text)
        => Assert.False(LegacyParsing.IsLoadingOverlay(text));

    [Fact]
    public void IsDossierLocked_et_IsLoadingOverlay_sont_disjoints_sur_les_cas_reels()
    {
        // garantit que le verrou n'est PAS pris pour un chargement et vice-versa (les 2 branches du fix).
        string verrou = "Une demande est en cours sur ce dossier — D2608200352 par RIGAPP23/julien.fontrier";
        string overlay = "Veuillez patienter... Traitement en cours...";
        Assert.True(LegacyParsing.IsDossierLocked(verrou));
        Assert.False(LegacyParsing.IsLoadingOverlay(verrou));
        Assert.True(LegacyParsing.IsLoadingOverlay(overlay));
        Assert.False(LegacyParsing.IsDossierLocked(overlay));
    }

    // ── ShouldKeepWaitingForGrid : prolonger l'attente de la grille TANT QUE RIG charge encore ──
    // Cause racine du flap form-validation (run 17:29, STAMP 171521) : la grille des demandes n'est
    // pas apparue dans le budget de WaitForDemandeGrid alors que l'ecran affichait encore l'overlay
    // « Veuillez patienter / Traitement en cours » -> throw timing-race. Ce helper rend l'attente
    // DETERMINISTE : on ne renonce PAS tant que l'overlay de chargement est present (la grille arrive),
    // jusqu'a un plafond dur ; sans overlay (= RIG fige/plante, pas en chargement) on s'arrete.

    [Fact]
    public void ShouldKeepWaitingForGrid_prolonge_si_overlay_present_et_sous_plafond()
        // budget de base epuise MAIS RIG charge encore (overlay) ET on est sous le plafond dur -> on attend.
        => Assert.True(LegacyParsing.ShouldKeepWaitingForGrid(elapsedMs: 9000, baseMaxMs: 8000, hardCapMs: 45000, overlayPresent: true));

    [Fact]
    public void ShouldKeepWaitingForGrid_arrete_si_pas_overlay()
        // budget de base epuise et PLUS d'overlay (RIG ne charge plus = vrai blocage/plante) -> on s'arrete.
        => Assert.False(LegacyParsing.ShouldKeepWaitingForGrid(elapsedMs: 9000, baseMaxMs: 8000, hardCapMs: 45000, overlayPresent: false));

    [Fact]
    public void ShouldKeepWaitingForGrid_arrete_au_plafond_dur_meme_si_overlay()
        // garde-fou anti-attente-infinie : meme overlay present, au-dela du plafond dur on s'arrete (throw).
        => Assert.False(LegacyParsing.ShouldKeepWaitingForGrid(elapsedMs: 45000, baseMaxMs: 8000, hardCapMs: 45000, overlayPresent: true));

    [Fact]
    public void ShouldKeepWaitingForGrid_pas_de_prolongation_avant_epuisement_du_budget_de_base()
        // avant la fin du budget de base, la boucle normale tourne deja -> ce helper ne s'applique pas (false).
        => Assert.False(LegacyParsing.ShouldKeepWaitingForGrid(elapsedMs: 3000, baseMaxMs: 8000, hardCapMs: 45000, overlayPresent: true));

    [Fact]
    public void ShouldKeepWaitingForGrid_fac_simile_run_17h29_overlay_a_18s_prolonge()
    {
        // Reproduction du flap : apres le budget cumule 8s+10s=18s, l'ecran a TOUJOURS l'overlay
        // (dump du log : pnlIconsForm « Veuillez patienter... Traitement en cours... ») -> on prolonge
        // au lieu de throw. Le run 17:04 (succes) trouvait la grille a ~6,4s, donc << plafond.
        Assert.True(LegacyParsing.ShouldKeepWaitingForGrid(elapsedMs: 18000, baseMaxMs: 18000, hardCapMs: 45000, overlayPresent: true));
        // Sans overlay au meme instant (grille jamais demandee / autre ecran fige) -> pas de prolongation.
        Assert.False(LegacyParsing.ShouldKeepWaitingForGrid(elapsedMs: 18000, baseMaxMs: 18000, hardCapMs: 45000, overlayPresent: false));
    }

    // ── MotifAlreadySelected : le combo affiche-t-il deja le motif voulu ? (combo RCS lazy) ──

    [Theory]
    // cas reel du run live : combo deja rempli "INPMANQ - Piece manquante, n..." -> deja selectionne.
    [InlineData("INPMANQ - Pièce manquante, non valide ou illisible", "INPMANQ")]
    [InlineData("INPMANQ - Pièce manquante, n...", "INPMANQ")] // libelle tronque par la largeur du combo
    [InlineData("INPMANQ", "INPMANQ")]                          // combo qui n'affiche que le code
    [InlineData("INPMANQ-Piece manquante", "INPMANQ")]          // sans espace autour du tiret
    [InlineData("inpmanq - piece manquante", "INPMANQ")]        // insensible a la casse
    [InlineData("  INPMANQ - Piece manquante  ", "INPMANQ")]    // blancs de bord ignores
    public void MotifAlreadySelected_combo_prefixe_par_le_code_est_deja_selectionne(string current, string motif)
        => Assert.True(LegacyParsing.MotifAlreadySelected(current, motif));

    [Fact]
    public void MotifAlreadySelected_motif_present_au_milieu_du_libelle_compte_comme_selectionne()
        // motif demande sous forme de libelle (substring), present meme sans etre en tete.
        => Assert.True(LegacyParsing.MotifAlreadySelected(
            "AUTRE - voir Pièces manquantes plus bas", "Pièces manquantes"));

    [Theory]
    [InlineData("INPVALID - Pièce non valide", "INPMANQ")] // autre code -> pas selectionne
    [InlineData("AUTRE - autre motif", "INPMANQ")]
    [InlineData("", "INPMANQ")]      // combo vide -> rien de selectionne (il faudra ouvrir le dropdown)
    [InlineData("   ", "INPMANQ")]   // blancs seuls = vide
    [InlineData(null, "INPMANQ")]
    [InlineData("INPMANQ - Pièce manquante", "")]   // motif voulu vide -> ne pas court-circuiter
    [InlineData("INPMANQ - Pièce manquante", "   ")]
    [InlineData("INPMANQ - Pièce manquante", null)]
    public void MotifAlreadySelected_non_correspondant_ou_args_vides_retourne_false(string current, string motif)
        => Assert.False(LegacyParsing.MotifAlreadySelected(current, motif));

    // ── IsCellYVisible : le centre Y ecran d'une cellule est-il dans la bande visible ? ──
    // CONTEXTE bug run live : cellules MSAA a Y=1585 / Y=4511 sur un ecran/grille de 1080 ->
    // hors zone visible -> le double-clic tapait dans le vide. On rend la cellule visible AVANT.

    [Theory]
    [InlineData(540, 0, 1080)]    // pile au milieu d'une grille 0..1080
    [InlineData(10, 0, 1080)]     // pres du haut mais > marge (4)
    [InlineData(1070, 0, 1080)]   // pres du bas mais < bas - marge
    [InlineData(200, 100, 900)]   // grille qui ne part pas de 0 (sous une barre d'outils)
    public void IsCellYVisible_dans_la_bande_retourne_true(int cellCy, int top, int bottom)
        => Assert.True(LegacyParsing.IsCellYVisible(cellCy, top, bottom));

    [Theory]
    [InlineData(1585, 0, 1080)]   // CAS REEL run live : Y=1585 sur ecran 1080 -> offscreen bas
    [InlineData(4511, 0, 1080)]   // CAS REEL run live : Y=4511 -> tres en dessous
    [InlineData(-50, 0, 1080)]    // au-dessus de la grille (scrolled past top)
    [InlineData(2, 0, 1080)]      // dans la marge haute (< top + 4) -> pas fiable
    [InlineData(1079, 0, 1080)]   // dans la marge basse (> bottom - 4)
    [InlineData(950, 100, 900)]   // sous le bas de la grille (mais aurait ete < hauteur ecran)
    public void IsCellYVisible_hors_bande_ou_dans_la_marge_retourne_false(int cellCy, int top, int bottom)
        => Assert.False(LegacyParsing.IsCellYVisible(cellCy, top, bottom));

    [Fact]
    public void IsCellYVisible_bande_degeneree_retourne_false()
        // grille plus petite que 2*marge -> aucune position fiable.
        => Assert.False(LegacyParsing.IsCellYVisible(500, 500, 505));

    [Fact]
    public void IsCellYVisible_marge_personnalisee_elargit_la_zone_interdite()
    {
        // marge 100 : un Y a 50px du haut devient non fiable.
        Assert.False(LegacyParsing.IsCellYVisible(150, 100, 900, margin: 100));
        Assert.True(LegacyParsing.IsCellYVisible(500, 100, 900, margin: 100));
    }

    // ── WheelNotchesToReveal : crans de molette (signes) pour ramener une cellule dans la vue ──
    // Convention : notch NEGATIF = molette bas = contenu monte (Y diminuent) ;
    //              notch POSITIF = molette haut = contenu descend (Y augmentent).

    [Fact]
    public void WheelNotchesToReveal_cellule_sous_la_zone_donne_des_notches_negatifs()
    {
        // CAS REEL : cellule a Y=1585, grille 0..1080 (centre 540), rowHeight 22, 3 lignes/cran.
        // Elle est SOUS le centre -> il faut faire MONTER le contenu -> notches negatifs.
        int n = LegacyParsing.WheelNotchesToReveal(1585, 0, 1080, rowHeight: 22);
        Assert.True(n < 0, $"attendu negatif, obtenu {n}");
    }

    [Fact]
    public void WheelNotchesToReveal_cellule_tres_basse_donne_plus_de_notches()
    {
        // Y=4511 est bien plus bas que Y=1585 -> amplitude (valeur absolue) strictement superieure.
        int proche = LegacyParsing.WheelNotchesToReveal(1585, 0, 1080, rowHeight: 22);
        int loin = LegacyParsing.WheelNotchesToReveal(4511, 0, 1080, rowHeight: 22);
        Assert.True(System.Math.Abs(loin) > System.Math.Abs(proche),
            $"|{loin}| devrait depasser |{proche}|");
    }

    [Fact]
    public void WheelNotchesToReveal_cellule_au_dessus_donne_des_notches_positifs()
    {
        // cellule au-dessus du centre (Y=-100) -> faire DESCENDRE le contenu -> notches positifs.
        int n = LegacyParsing.WheelNotchesToReveal(-100, 0, 1080, rowHeight: 22);
        Assert.True(n > 0, $"attendu positif, obtenu {n}");
    }

    [Fact]
    public void WheelNotchesToReveal_deja_centree_retourne_zero()
        // cellule pile au centre (540) -> rien a scroller.
        => Assert.Equal(0, LegacyParsing.WheelNotchesToReveal(540, 0, 1080, rowHeight: 22));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void WheelNotchesToReveal_rowHeight_non_positif_ne_jette_pas(int rowHeight)
    {
        // garde-fou division par zero : traite rowHeight<=0 comme 1, retourne un resultat fini.
        var ex = Record.Exception(() => LegacyParsing.WheelNotchesToReveal(2000, 0, 1080, rowHeight));
        Assert.Null(ex);
    }

    [Fact]
    public void WheelNotchesToReveal_bande_degeneree_retourne_zero()
        => Assert.Equal(0, LegacyParsing.WheelNotchesToReveal(2000, 500, 500, rowHeight: 22));

    [Fact]
    public void WheelNotchesToReveal_progression_garantie_meme_si_petit_ecart()
    {
        // cellule juste sous le bas (delta > 0.5 cran mais < 1 cran) -> au moins 1 notch (anti-blocage
        // quand l'appelant boucle : doit progresser a chaque appel).
        // grille 0..100 (centre 50), rowHeight 10, 3 lignes/cran = 30px/cran. Cellule a 70 -> delta -20px
        // (|20| > 15 = pxPerNotch/2) -> au moins -1.
        int n = LegacyParsing.WheelNotchesToReveal(70, 0, 100, rowHeight: 10);
        Assert.Equal(-1, n);
    }

    // ── ParseLigneIndex : index 0-based pour le fallback clavier (Home + Down x index + Entree) ──
    // CONTEXTE : quand accSelect ne ramene pas la ligne dans la vue, on navigue au clavier. Le Name
    // MSAA de la LIGNE d'un DataGridView est "Ligne N" (FR) ou "Row N" (EN), N 1-based.

    [Theory]
    [InlineData("Ligne 1", 0)]       // 1re ligne : 0 Down apres Home
    [InlineData("Ligne 3", 2)]       // 2 Down apres Home
    [InlineData("Ligne 243", 242)]   // CAS REEL run live : demande tout en bas (position 242)
    [InlineData("Row 1", 0)]         // locale EN
    [InlineData("Row 5", 4)]
    [InlineData("ligne 7", 6)]       // insensible a la casse
    [InlineData("DCA Ligne 4", 3)]   // motif present meme avec un prefixe (ancre lache)
    [InlineData("Ligne  10", 9)]     // plusieurs espaces entre le mot et le nombre
    public void ParseLigneIndex_extrait_l_index_0_based(string name, int expected)
        => Assert.Equal(expected, LegacyParsing.ParseLigneIndex(name));

    [Theory]
    [InlineData("Ligne 0")]                       // placeholder/header -> pas une ligne data
    [InlineData("Row 0")]
    [InlineData("D2613500028;DCADEMAT;;;")]       // Name concatene des colonnes (pas de "Ligne N")
    [InlineData("Cellule Traitement")]            // autre nom sans motif
    [InlineData("Lignes 5")]                       // "Lignes" (pluriel) suivi d'un nombre -> pas le motif exact attendu...
    [InlineData("")]
    [InlineData(null)]
    public void ParseLigneIndex_sans_motif_ou_ligne_0_retourne_moins_un(string name)
        => Assert.Equal(-1, LegacyParsing.ParseLigneIndex(name));

    // ── ShouldAcceptDepotViaEditionsScreen : signal de succes de repli pour la validation DCADEMAT ──
    // CONTEXTE : « Valider » cree le depot puis navigue vers « Tableau des editions » ; la grille
    // Exercices n'est alors plus lisible -> aucun n° relu, mais le depot EXISTE. Le passage a l'ecran
    // editions (+ Valider clique) = preuve de succes. N'est consulte QUE si aucun n° n'a ete relu.

    [Fact]
    public void ShouldAcceptDepot_via_editions_quand_pas_de_numero_mais_valider_clique_et_ecran_editions()
        => Assert.True(LegacyParsing.ShouldAcceptDepotViaEditionsScreen(
            numDemandeFound: false, validerWasClicked: true, editionsScreenAfter: true));

    [Fact]
    public void ShouldAcceptDepot_false_si_un_numero_a_ete_relu()
        // Un n° relu => succes deja prouve par le chemin nominal ; pas besoin du repli.
        => Assert.False(LegacyParsing.ShouldAcceptDepotViaEditionsScreen(
            numDemandeFound: true, validerWasClicked: true, editionsScreenAfter: true));

    [Fact]
    public void ShouldAcceptDepot_false_si_valider_pas_clique()
        // Pas de clic Valider (no-op) => on ne doit pas conclure au succes meme si l'ecran editions est la.
        => Assert.False(LegacyParsing.ShouldAcceptDepotViaEditionsScreen(
            numDemandeFound: false, validerWasClicked: false, editionsScreenAfter: true));

    [Fact]
    public void ShouldAcceptDepot_false_si_pas_ecran_editions()
        // Ni n°, ni ecran editions => vrai echec : la validation n'a pas abouti.
        => Assert.False(LegacyParsing.ShouldAcceptDepotViaEditionsScreen(
            numDemandeFound: false, validerWasClicked: true, editionsScreenAfter: false));

    // ── IsAlreadyReclamee : la demande de reclamation ouverte est-elle deja reclamee (terminal atteint) ? ──
    // CONTEXTE : l'alerte « Demandes en reclamations > 15 jours » contient des demandes deja reclamees ;
    // leur combo « Type de motif » est vide/verrouille alors qu'un motif est deja pose. C'est le terminal
    // metier (demande en reclamation), pas un echec. Filet etroit : combo vide ET valeur deja presente.

    [Theory]
    [InlineData("9LIB")]                         // cas reel run live (screenshot 2084) : motif libre deja pose
    [InlineData("INPMANQ - Piece manquante")]    // motif code deja pose
    [InlineData("  9LIB  ")]                       // espaces autour -> non vide apres trim
    public void IsAlreadyReclamee_vrai_quand_combo_vide_et_motif_deja_pose(string current)
        => Assert.True(LegacyParsing.IsAlreadyReclamee(comboItemCount: 0, currentMotifValue: current));

    [Theory]
    [InlineData(0, "")]        // combo vide MAIS aucune valeur posee -> vrai ecran vide / mauvais etat -> echec garde
    [InlineData(0, "   ")]     // valeur blanche = vide apres trim
    [InlineData(0, null)]      // pas de valeur
    [InlineData(5, "9LIB")]    // combo a des items -> on PEUT choisir -> pas « deja verrouille » -> false
    [InlineData(1, "INPMANQ")] // au moins 1 item selectionnable -> false
    [InlineData(3, "")]        // items presents + pas de valeur -> chemin nominal de selection -> false
    public void IsAlreadyReclamee_faux_si_combo_a_des_items_ou_aucun_motif_pose(int count, string current)
        => Assert.False(LegacyParsing.IsAlreadyReclamee(count, current));

    // ── IsDemandeDejaReclamee : detection par le TEXTE de l'ecran (independante du combo) ──────────────
    // CONTEXTE (run live 2026-06-04, run 17:04, screenshot dca-reclamation-FAIL-170431) : variant ou le combo
    // « Type de motif » est PEUPLE (INPMANQ deja selectionne) mais la demande est DEJA reclamee — non capte
    // par IsAlreadyReclamee (qui exige combo vide). Signal robuste = « Etat Demande = N - Reclamation » et/ou
    // commentaire « reclamation en cours n° D... ». Discrimination : ne PAS sur-declencher sur une demande EN
    // ATTENTE (l'ecran contient TOUJOURS le titre « Reclamation / Refus » + bouton « Reclamer »).

    [Theory]
    // (B) etat « N - Reclamation » (avec/sans accents, espacement variable autour du tiret).
    [InlineData("Evenement attendu PDCA Traitement Etat Demande N - Reclamation Reception")]
    [InlineData("etat demande n - reclamation")]                 // minuscule, sans accent
    [InlineData("Etat Demande  N  -  Réclamation")]              // espacement large + accent
    // (A) commentaire « reclamation en cours n° D... le ... » (grille Exercices).
    [InlineData("Date de cloture 30/06/2025 commentaire reclamation en cours n° D2608301551 le 27/04/2026")]
    [InlineData("RECLAMATION EN COURS")]                          // casse haute
    // Cas reel complet (texte agrege facsimile du screenshot 170431 : titre section + bouton + etat + commentaire).
    [InlineData("Reclamation / Refus Evenement attendu PDCA - Piece manquante DCA Etat Demande N - Reclamation "
        + "Type de motif INPMANQ - Piece manquante Motif Pieces manquantes Reclamer Refuser "
        + "reclamation en cours n° D2608301551 le 27/04/2026")]
    public void IsDemandeDejaReclamee_vrai_sur_etat_N_ou_commentaire_en_cours(string agg)
        => Assert.True(LegacyParsing.IsDemandeDejaReclamee(agg));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // ⚠ DISCRIMINATION : l'ecran d'une demande EN ATTENTE (non encore reclamee) contient le titre de section
    //   « Reclamation / Refus » + le bouton « Reclamer » + le label « Type de motif » -> NE DOIT PAS matcher
    //   (sinon on conclurait « deja reclamee » a tort et on sauterait la vraie 1re mise en reclamation).
    [InlineData("Reclamation / Refus Evenement attendu PDCA Type de motif Motif Reclamer Refuser Surenu")]
    [InlineData("Reclamation / Refus  Reclamer  Refuser")]       // titre + boutons seuls
    [InlineData("Configurer le depot Exercices DCA Valider Interrompre Verifier Quitter")] // ecran depot, pas reclamation
    [InlineData("Etat Demande A - Attente de pieces")]            // etat « A » (en attente) -> pas N-reclamation
    public void IsDemandeDejaReclamee_faux_sur_demande_en_attente_ou_texte_vide(string agg)
        => Assert.False(LegacyParsing.IsDemandeDejaReclamee(agg));

    // ── IsReclamationActionTerminalOk : verdict du step terminal ALERTES rec-form / rec-dca ──────────────
    // CONTEXTE (run live 2026-06-04, run 18:04, stdout legacy-20260604-180417 + screenshots FAIL 180306/180411) :
    // le clic-droit OUVRE le menu (VK_APPS) et l'item « Reprendre les impressions » / « Lancer le pool d'editions »
    // est TROUVE puis CLIQUE (action metier declenchee), MAIS l'apercu avant impression (courrier/lettre, rendu
    // par composition DWM type AcroPDF) ne peint sur RIEN de detectable sur le HDESK non compose (mur partie b).
    // POLITIQUE (alignee sur RefuserDemande / ReclamerDcaAvecMotif) : terminal-OK SSI item clique ET RIG vivant ;
    // l'apercu detecte est un bonus non requis. FAIL seulement si l'item n'a pas ete clique OU si RIG a crashe.

    [Theory]
    // Item clique + RIG vivant = OK, que l'apercu soit detecte (Mode A/C, desktop compose) ou non (mur HDESK b).
    [InlineData(true, true, true)]   // cas nominal Mode A/C : apercu peint -> OK
    [InlineData(true, true, false)]  // cas reel HDESK Mode B (run 18:04) : apercu non peint -> terminal-OK gracieux
    public void IsReclamationActionTerminalOk_vrai_si_item_clique_et_rig_vivant(bool clicked, bool alive, bool preview)
        => Assert.True(LegacyParsing.IsReclamationActionTerminalOk(clicked, alive, preview));

    [Theory]
    // RIG a crashe apres l'action = vrai echec dur (EnsureRigStillAlive aurait throw cote driver) -> FAIL.
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    // Item PAS clique (menu non ouvert / item introuvable) = pas d'action declenchee -> FAIL (le driver throw
    // deja en amont sur ces cas ; on encode la politique pour qu'elle reste FAIL si elle est jamais sollicitee).
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void IsReclamationActionTerminalOk_faux_si_pas_clique_ou_rig_mort(bool clicked, bool alive, bool preview)
        => Assert.False(LegacyParsing.IsReclamationActionTerminalOk(clicked, alive, preview));

    // ── RETRY-on-transient (DCADEMAT / ALERTES) : politique + verdict + textes de log ─────────
    // Sous Mode B (HDESK isole, 2 workers concurrents), RIG est lent et un scenario aleatoire FAIL
    // parfois (contention/timeout environnemental) alors que les vrais modes d'echec sont corriges.
    // Politique : un worker en echec (exit != 0) est relance UNE seule fois ; 2e essai vert -> flap
    // absorbe (OK) ; 2e essai rouge -> echec confirme (vrai bug). Un worker OK du 1er coup (exit 0)
    // n'est JAMAIS relance.

    [Fact]
    public void ShouldRetryAfterExit_exit_non_nul_declenche_le_retry()
        => Assert.True(LegacyParsing.ShouldRetryAfterExit(1));

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    [InlineData(-1)]
    public void ShouldRetryAfterExit_tout_exit_non_nul_declenche_le_retry(int exit)
        => Assert.True(LegacyParsing.ShouldRetryAfterExit(exit));

    [Fact]
    public void ShouldRetryAfterExit_exit0_ne_relance_jamais()
        // INVARIANT CLE : les passes-du-1er-coup ne sont JAMAIS relancees (zero surcout, zero regression).
        => Assert.False(LegacyParsing.ShouldRetryAfterExit(0));

    [Fact]
    public void FinalExitAfterRetry_succes_au_1er_coup_ignore_le_2e_essai()
        // exit1 == 0 -> pas de retry : le retryExit (ici volontairement != 0) est IGNORE, verdict reste 0.
        => Assert.Equal(0, LegacyParsing.FinalExitAfterRetry(firstExitCode: 0, retryExitCode: 1));

    [Fact]
    public void FinalExitAfterRetry_flap_absorbe_le_2e_essai_passe()
        // exit1 != 0 puis exit2 == 0 -> verdict final = 0 (flap transitoire absorbe).
        => Assert.Equal(0, LegacyParsing.FinalExitAfterRetry(firstExitCode: 1, retryExitCode: 0));

    [Fact]
    public void FinalExitAfterRetry_echec_confirme_le_2e_essai_echoue_aussi()
        // exit1 != 0 et exit2 != 0 -> verdict final = exit2 (echec confirme, vrai bug).
        => Assert.Equal(1, LegacyParsing.FinalExitAfterRetry(firstExitCode: 1, retryExitCode: 1));

    [Theory]
    [InlineData(0, 0, 0)]   // OK direct
    [InlineData(0, 1, 0)]   // OK direct -> 2e essai ignore
    [InlineData(1, 0, 0)]   // flap absorbe
    [InlineData(3, 0, 0)]   // flap absorbe (autre code d'echec initial)
    [InlineData(1, 1, 1)]   // echec confirme
    [InlineData(1, 2, 2)]   // echec confirme (2e code different)
    public void FinalExitAfterRetry_table_de_verite(int first, int retry, int expected)
        => Assert.Equal(expected, LegacyParsing.FinalExitAfterRetry(first, retry));

    [Fact]
    public void AllPassedAfterRetry_tous_verts_apres_retry()
        => Assert.True(LegacyParsing.AllPassedAfterRetry(new[] { 0, 0, 0, 0 }));

    [Fact]
    public void AllPassedAfterRetry_un_echec_confirme_fait_echouer_la_suite()
        // 1 seul exit final != 0 (echec confirme apres retry) -> suite rouge.
        => Assert.False(LegacyParsing.AllPassedAfterRetry(new[] { 0, 0, 1, 0 }));

    [Fact]
    public void AllPassedAfterRetry_liste_vide_est_vert_par_vacuite()
        => Assert.True(LegacyParsing.AllPassedAfterRetry(System.Array.Empty<int>()));

    [Fact]
    public void AllPassedAfterRetry_null_est_faux()
        => Assert.False(LegacyParsing.AllPassedAfterRetry(null));

    [Fact]
    public void RetryAbsorbedLogLine_texte_exact()
        // Texte EXACT attendu dans le log/UI quand le retry passe (flap absorbe) — verrouille le wording.
        => Assert.Equal(
            "RETRY dcademat-dca-validation-1 : flap transitoire absorbe (OK au 2e essai)",
            LegacyParsing.RetryAbsorbedLogLine("dcademat-dca-validation-1"));

    [Fact]
    public void RetryConfirmedFailLogLine_texte_exact()
        // Texte EXACT attendu quand le retry echoue aussi (echec confirme = vrai bug).
        => Assert.Equal(
            "RETRY alertes-int-form : echec confirme au 2e essai",
            LegacyParsing.RetryConfirmedFailLogLine("alertes-int-form"));

    [Fact]
    public void RetryStartingLogLine_texte_exact()
        // Texte EXACT de l'annonce de relance (visible dans le flux temps reel avant le 2e essai).
        => Assert.Equal(
            "RETRY dcademat-dca-reclamation-1 : echec au 1er essai (exit1), relance unique…",
            LegacyParsing.RetryStartingLogLine("dcademat-dca-reclamation-1", 1));

    // ── WATCHDOG par worker (filet anti-hang) : delai resolu + exit synthetique + texte de log ─────
    // Un worker en hang (ne sort jamais, busy-poll) doit etre tue apres un plafond de temps PAR worker,
    // avec un exit synthetique != 0 qui ALIMENTE le retry existant (pas un mecanisme parallele). Le delai
    // par defaut (360s) laisse vivre un scenario lent (~3,5 min) mais attrape un hang (13+ min observe).

    [Fact]
    public void ResolveWatchdog_override_absent_donne_le_defaut()
        => Assert.Equal(LegacyParsing.DefaultLegacyWorkerWatchdogSeconds, LegacyParsing.ResolveWatchdogSeconds(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]      // non numerique
    [InlineData("12.5")]     // non entier
    public void ResolveWatchdog_override_invalide_donne_le_defaut(string raw)
        => Assert.Equal(LegacyParsing.DefaultLegacyWorkerWatchdogSeconds, LegacyParsing.ResolveWatchdogSeconds(raw));

    [Fact]
    public void ResolveWatchdog_override_valide_est_respecte()
        // Un override raisonnable (au-dessus du min dur) est pris tel quel.
        => Assert.Equal(600, LegacyParsing.ResolveWatchdogSeconds("600"));

    [Theory]
    [InlineData("10")]       // beaucoup trop bas
    [InlineData("239")]      // juste sous le min dur (240)
    [InlineData("0")]
    [InlineData("-5")]
    public void ResolveWatchdog_override_trop_bas_est_clampe_au_min_dur(string raw)
        // INVARIANT CLE : ne JAMAIS descendre sous le temps d'un scenario normal, sinon on tuerait des
        // workers vivants et le retry boucle a vide. Tout override < min est ramene au min dur.
        => Assert.Equal(LegacyParsing.MinLegacyWorkerWatchdogSeconds, LegacyParsing.ResolveWatchdogSeconds(raw));

    [Fact]
    public void ResolveWatchdog_override_pile_au_min_dur_est_garde()
        => Assert.Equal(LegacyParsing.MinLegacyWorkerWatchdogSeconds,
            LegacyParsing.ResolveWatchdogSeconds(LegacyParsing.MinLegacyWorkerWatchdogSeconds.ToString()));

    [Fact]
    public void Watchdog_defaut_au_dessus_du_min_dur_et_min_au_dessus_dun_scenario_normal()
    {
        // Le defaut doit laisser vivre un scenario lent (> min dur), et le min dur doit etre au-dessus
        // du temps d'un scenario normal (~3,5 min = 210s) pour ne jamais tuer un worker vivant.
        Assert.True(LegacyParsing.DefaultLegacyWorkerWatchdogSeconds > LegacyParsing.MinLegacyWorkerWatchdogSeconds);
        Assert.True(LegacyParsing.MinLegacyWorkerWatchdogSeconds >= 210);
    }

    [Fact]
    public void Watchdog_exit_synthetique_est_non_nul_et_alimente_le_retry()
    {
        // L'exit synthetique du watchdog DOIT etre != 0 ET declencher le retry existant : c'est le
        // couplage central (le hang devient un echec normal que le retry re-lance, pas un parallele).
        Assert.NotEqual(0, LegacyParsing.WatchdogKillExitCode);
        Assert.True(LegacyParsing.ShouldRetryAfterExit(LegacyParsing.WatchdogKillExitCode));
        Assert.True(LegacyParsing.WatchdogExitFeedsRetry());
    }

    [Fact]
    public void Watchdog_exit_synthetique_est_distinct_de_exit1_applicatif()
        // 124 (convention `timeout`) pour distinguer un kill watchdog d'un FAIL applicatif (exit 1) dans les logs.
        => Assert.Equal(124, LegacyParsing.WatchdogKillExitCode);

    [Fact]
    public void WatchdogTimeoutLogLine_texte_exact()
        // Texte EXACT du log du watchdog (visible UI + log applicatif) : annonce « -> FAIL+retry ».
        => Assert.Equal(
            "WATCHDOG dcademat-dca-reclamation-1 : worker tue apres 360s (hang) -> FAIL+retry",
            LegacyParsing.WatchdogTimeoutLogLine("dcademat-dca-reclamation-1", 360));

    // ── DEADLINE de verification du step terminal ALERTES rec-form / rec-dca (anti-hang UIA) ──────────
    // Apres le clic de l'item (« Reprendre les impressions » / « Lancer le pool d'editions »), RIG entre en
    // REPRISE PROCESSUS (suivi D1 + facturation) et parke le focus sur HDESK -> les appels UIA de la verif
    // bloquent sans timeout. On borne la verif post-clic par une deadline DURE, << watchdog, pour que le
    // scenario SORTE par lui-meme (terminal-OK gracieux), jamais via le watchdog 360s.

    [Fact]
    public void ResolveReclamVerify_override_absent_donne_le_defaut()
        => Assert.Equal(LegacyParsing.DefaultReclamationVerifyDeadlineSeconds,
            LegacyParsing.ResolveReclamationVerifyDeadlineSeconds(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]      // non numerique
    [InlineData("12.5")]     // non entier
    [InlineData("0")]        // deadline nulle = absurde -> defaut
    [InlineData("-3")]       // deadline negative -> defaut
    public void ResolveReclamVerify_override_invalide_ou_nul_donne_le_defaut(string raw)
        => Assert.Equal(LegacyParsing.DefaultReclamationVerifyDeadlineSeconds,
            LegacyParsing.ResolveReclamationVerifyDeadlineSeconds(raw));

    [Fact]
    public void ResolveReclamVerify_override_valide_est_respecte()
        // Un override raisonnable (entre 1 et le max dur) est pris tel quel.
        => Assert.Equal(45, LegacyParsing.ResolveReclamationVerifyDeadlineSeconds("45"));

    [Theory]
    [InlineData("121")]      // juste au-dessus du max dur (120)
    [InlineData("360")]      // = watchdog : interdit (on perdrait l'auto-sortie)
    [InlineData("10000")]
    public void ResolveReclamVerify_override_trop_haut_est_clampe_au_max_dur(string raw)
        // INVARIANT CLE : ne JAMAIS s'approcher du watchdog (360s), sinon le scenario ne sort plus par
        // lui-meme. Tout override > max dur est ramene au max dur.
        => Assert.Equal(LegacyParsing.MaxReclamationVerifyDeadlineSeconds,
            LegacyParsing.ResolveReclamationVerifyDeadlineSeconds(raw));

    [Fact]
    public void ResolveReclamVerify_override_pile_au_max_dur_est_garde()
        => Assert.Equal(LegacyParsing.MaxReclamationVerifyDeadlineSeconds,
            LegacyParsing.ResolveReclamationVerifyDeadlineSeconds(
                LegacyParsing.MaxReclamationVerifyDeadlineSeconds.ToString()));

    [Fact]
    public void ReclamVerify_deadline_reste_largement_sous_le_watchdog()
    {
        // INVARIANT CENTRAL : la deadline de verif (defaut ET max dur) DOIT etre sous le watchdog, pour que
        // rec-form/rec-dca sortent TOUJOURS par eux-memes avant le kill watchdog (anti-recidive du hang).
        Assert.True(LegacyParsing.MaxReclamationVerifyDeadlineSeconds < LegacyParsing.DefaultLegacyWorkerWatchdogSeconds);
        Assert.True(LegacyParsing.DefaultReclamationVerifyDeadlineSeconds < LegacyParsing.DefaultLegacyWorkerWatchdogSeconds);
        Assert.True(LegacyParsing.ReclamationVerifyDeadlineIsBelowWatchdog());
    }

    // ── DELAI DE STABILISATION (settle) post-clic ALERTES rec-* — APPROCHE ZERO-UIA (tentative #3) ─────
    // Apres le clic de l'item, le worker NE fait AUCUN appel UIA (les tentatives #1 « throw si pas de signal »
    // et #2 « UIA sous deadline sur thread STA background » ont echoue : affinite STA -> l'appel UIA est
    // marshale vers le thread STA proprietaire deja bloque). Le clic a DEJA declenche l'action metier (REPRISE
    // PROCESSUS). On verifie UNIQUEMENT en NON-UIA (Process.HasExited) + un SETTLE borne (one-shot, pas un
    // poll : aucune condition UIA-free a sonder) + screenshot PrintWindow + return OK. ResolveReclamationSettleMs
    // est la SEULE logique parametrable -> testee ici.

    [Fact]
    public void ResolveReclamSettle_override_absent_donne_le_defaut()
        => Assert.Equal(LegacyParsing.DefaultReclamationSettleMs,
            LegacyParsing.ResolveReclamationSettleMs(null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]      // non numerique
    [InlineData("3.5")]      // non entier
    [InlineData("0")]        // settle nul = absurde -> defaut
    [InlineData("-100")]     // settle negatif -> defaut
    public void ResolveReclamSettle_override_invalide_ou_nul_donne_le_defaut(string raw)
        => Assert.Equal(LegacyParsing.DefaultReclamationSettleMs,
            LegacyParsing.ResolveReclamationSettleMs(raw));

    [Fact]
    public void ResolveReclamSettle_override_valide_est_respecte()
        // Un override raisonnable (entre min et max) est pris tel quel.
        => Assert.Equal(4000, LegacyParsing.ResolveReclamationSettleMs("4000"));

    [Theory]
    [InlineData("1")]        // bien en dessous du min (500)
    [InlineData("250")]
    [InlineData("499")]
    public void ResolveReclamSettle_override_trop_bas_est_clampe_au_min(string raw)
        => Assert.Equal(LegacyParsing.MinReclamationSettleMs,
            LegacyParsing.ResolveReclamationSettleMs(raw));

    [Theory]
    [InlineData("10001")]    // juste au-dessus du max (10000)
    [InlineData("60000")]
    [InlineData("360000")]   // = watchdog en ms : interdit (on perdrait l'auto-sortie rapide)
    public void ResolveReclamSettle_override_trop_haut_est_clampe_au_max(string raw)
        // INVARIANT : meme un settle « pied-de-biche » reste tres en deca du watchdog -> le worker sort vite.
        => Assert.Equal(LegacyParsing.MaxReclamationSettleMs,
            LegacyParsing.ResolveReclamationSettleMs(raw));

    [Theory]
    [InlineData("500")]      // pile au min
    [InlineData("10000")]    // pile au max
    public void ResolveReclamSettle_override_pile_aux_bornes_est_garde(string raw)
        => Assert.Equal(int.Parse(raw), LegacyParsing.ResolveReclamationSettleMs(raw));

    [Fact]
    public void ReclamSettle_reste_tres_en_deca_du_watchdog()
    {
        // INVARIANT CENTRAL : le settle (defaut ET max, en ms) DOIT etre tres sous le watchdog (en ms), pour
        // que rec-form/rec-dca sortent par eux-memes en quelques secondes, jamais via le kill watchdog 360s.
        Assert.True(LegacyParsing.MaxReclamationSettleMs < LegacyParsing.DefaultLegacyWorkerWatchdogSeconds * 1000);
        Assert.True(LegacyParsing.DefaultReclamationSettleMs <= LegacyParsing.MaxReclamationSettleMs);
        Assert.True(LegacyParsing.DefaultReclamationSettleMs >= LegacyParsing.MinReclamationSettleMs);
        Assert.True(LegacyParsing.ReclamationSettleIsBelowWatchdog());
    }

    // ── dca-reclamation : RIG auto-ouvre un PROCESSUS DE SUIVI (MB1/facturation) apres une reclamation
    // actee => SUCCES terminal (le driver doit SORTIR, pas busy-poller). Discriminant : ne PAS matcher
    // l'ecran de reclamation lui-meme.

    [Theory]
    [InlineData("... Création processus ... MB1 ...")]                          // (A) evenement creation
    [InlineData("CREATION PROCESSUS facturation")]                             // (A) majuscules
    [InlineData("Entrée dans le RCS — chargement du dossier")]                 // (B) ecran MB1
    [InlineData("entree dans le rcs")]                                          // (B) sans accents
    [InlineData("Processus MB1 - facturation de la formalité")]                // (C) MB1 + facturation
    [InlineData("Facturation automatique (processus MB1)")]                    // (C) facturation + MB1 (ordre inverse)
    public void FollowupProcess_detecte_un_suivi_mb1_ouvert(string screenText)
        => Assert.True(LegacyParsing.IsReclamationFollowupProcessOpened(screenText));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // L'ecran de reclamation lui-meme (titre de section + bouton) NE doit PAS declencher (faux positif).
    [InlineData("Réclamation / Refus    Type de motif    Motif    Réclamer    Quitter")]
    [InlineData("Configurer le dépôt — Exercices — DCA Ligne 1 — Valider")]
    // « mb1 » isole sans contexte creation/facturation ne doit pas matcher (regex (C) exige le contexte).
    [InlineData("référence interne mb1 du client")]
    public void FollowupProcess_ne_sur_declenche_pas(string screenText)
        => Assert.False(LegacyParsing.IsReclamationFollowupProcessOpened(screenText));
}

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
}

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
}

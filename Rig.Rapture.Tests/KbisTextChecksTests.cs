using Rig.Wpf.Kbis.SmokeRunner;
using Xunit;

namespace Rig.Rapture.Tests;

/// <summary>
/// Tests unitaires de la logique PURE de vérification du K-bis (KbisTextChecks).
/// Rapides (&lt;1s, aucun run RIG). Couvrent les bugs RÉELS de 2026-05-29 :
///   - RigAffichageDoc (viewer interne RIG) ignoré → K-bis non détecté ;
///   - SIREN avec \b qui ne matche pas sur le texte collé par PdfPig ("numéro982 574 717").
/// Échantillon = extrait réel du K-bis du dossier 2024B00001 (SIREN 982 574 717).
/// </summary>
public class KbisTextChecksTests
{
    // Extrait représentatif du texte réellement produit par PdfPig sur le K-bis (mots collés
    // volontairement comme PdfPig les concatène — c'est le piège qui cassait le regex SIREN).
    private const string KbisSample =
        "Greffe du Tribunal de Commerce de . N° de gestion 2024B00001R.C.S.. - 29/05/2026 page 1/2" +
        "Extrait KbisEXTRAIT D'IMMATRICULATION PRINCIPALE AU REGISTRE DU COMMERCE ET DES SOCIETES" +
        "Immatriculation au RCS, numéro982 574 717 R.C.S. .Date d'immatriculation02/01/2024" +
        "Dénomination ou raison socialeOFPS Rhône-AlpesForme juridiqueSociété par actions simplifiée" +
        "Numéro d’identification Européen - EUIDFR3801.982574717Adresse du siège13 Rue Mandrin" +
        "Le Greffier FIN DE L'EXTRAIT";

    private const string RigAffichageDocCmdline =
        "\"C:\\RIG\\exe\\RigAffichageDoc.exe\" \"greffe=9995;TypeParam=NomFichier;Param=c:\\tmp\\rig\\raphael.vilain\\DD0F7128-4DB8-4F2E-BE45-8BF69708B284.pdf\"";

    // ── IsMeaningfulKbisProc ────────────────────────────────────────────────

    [Theory]
    [InlineData("RigAffichageDoc")] // ⚠ régression 2026-05-29 : était ignoré → K-bis non détecté
    [InlineData("Acrobat")]
    [InlineData("AcroRd32")]
    [InlineData("msedge")]
    [InlineData("FoxitPDFReader")]
    [InlineData("KBisXML2PDF")]
    public void IsMeaningfulKbisProc_viewers_et_generateurs_sont_reconnus(string proc)
        => Assert.True(KbisTextChecks.IsMeaningfulKbisProc(proc));

    [Theory]
    [InlineData("dllhost")]
    [InlineData("conhost")]
    [InlineData("svchost")]
    [InlineData("RuntimeBroker")]
    [InlineData("RigClientAccueil")]
    [InlineData("")]
    [InlineData(null)]
    public void IsMeaningfulKbisProc_hotes_generiques_sont_rejetes(string proc)
        => Assert.False(KbisTextChecks.IsMeaningfulKbisProc(proc));

    // ── ExtractPdfPathFromCmdline ───────────────────────────────────────────

    [Fact]
    public void ExtractPdfPathFromCmdline_recupere_le_chemin_pdf_de_RigAffichageDoc()
        => Assert.Equal(
            @"c:\tmp\rig\raphael.vilain\DD0F7128-4DB8-4F2E-BE45-8BF69708B284.pdf",
            KbisTextChecks.ExtractPdfPathFromCmdline(RigAffichageDocCmdline));

    [Fact]
    public void ExtractPdfPathFromCmdline_pas_de_pdf_retourne_null()
        => Assert.Null(KbisTextChecks.ExtractPdfPathFromCmdline("\"C:\\app\\viewer.exe\" --no-doc"));

    // ── FindSiren (le bug \b) ───────────────────────────────────────────────

    [Fact]
    public void FindSiren_siren_espace_colle_au_mot_precedent_est_trouve()
        // "numéro982 574 717" : \b échouait (o↔9 = pas de frontière). Lookarounds anti-chiffres OK.
        => Assert.Equal("982 574 717", KbisTextChecks.FindSiren("Immatriculation au RCS, numéro982 574 717 R.C.S."));

    [Fact]
    public void FindSiren_siren_concatene_dans_EUID_est_trouve()
        => Assert.Equal("982574717", KbisTextChecks.FindSiren("EUIDFR3801.982574717Adresse"));

    [Fact]
    public void FindSiren_dans_l_echantillon_complet()
        => Assert.Equal("982 574 717", KbisTextChecks.FindSiren(KbisSample));

    [Theory]
    [InlineData("Date 31/12/2024 clôture")] // pas de run de 9 chiffres
    [InlineData("aucun chiffre ici")]
    [InlineData("")]
    [InlineData(null)]
    public void FindSiren_pas_de_siren_retourne_null(string text)
        => Assert.Null(KbisTextChecks.FindSiren(text));

    // ── ContainsNumGestion ──────────────────────────────────────────────────

    [Fact]
    public void ContainsNumGestion_le_bon_dossier_est_present()
        => Assert.True(KbisTextChecks.ContainsNumGestion(KbisSample, "2024B00001"));

    [Fact]
    public void ContainsNumGestion_avec_espaces_dans_la_demande_reste_robuste()
        => Assert.True(KbisTextChecks.ContainsNumGestion(KbisSample, " 2024 B00001 "));

    [Fact]
    public void ContainsNumGestion_mauvais_dossier_absent()
        => Assert.False(KbisTextChecks.ContainsNumGestion(KbisSample, "9999X99999"));

    // ── FindKbisMarkers / IsComplete ────────────────────────────────────────

    [Fact]
    public void FindKbisMarkers_echantillon_a_plusieurs_marqueurs()
        => Assert.True(KbisTextChecks.FindKbisMarkers(KbisSample).Count >= 2);

    [Fact]
    public void FindKbisMarkers_texte_quelconque_aucun_marqueur()
        => Assert.Empty(KbisTextChecks.FindKbisMarkers("ceci n'est pas un document juridique"));

    [Fact]
    public void IsComplete_echantillon_complet_est_vrai()
        => Assert.True(KbisTextChecks.IsComplete(KbisSample));

    [Fact]
    public void IsComplete_pdf_tronque_est_faux()
        => Assert.False(KbisTextChecks.IsComplete("Greffe ... Extrait Kbis ... (coupé avant la fin)"));

    // ── Alertes RCS : IsMeaningfulDocDematProc ──────────────────────────────

    [Theory]
    [InlineData("PROC_DOC_DEMAT_EXE")] // viewer pièces formalités
    [InlineData("RigAffichageDoc")]    // DCADEMAT ouvre un PDF interne
    [InlineData("Acrobat")]
    public void IsMeaningfulDocDematProc_viewers_reconnus(string proc)
        => Assert.True(KbisTextChecks.IsMeaningfulDocDematProc(proc));

    [Theory]
    [InlineData("dllhost")]
    [InlineData("RigClientAccueil")]
    [InlineData("")]
    [InlineData(null)]
    public void IsMeaningfulDocDematProc_hotes_generiques_rejetes(string proc)
        => Assert.False(KbisTextChecks.IsMeaningfulDocDematProc(proc));

    // ── Alertes RCS : MatchesDemandeType (J00 formalité vs DCADEMAT) ─────────

    [Fact]
    public void MatchesDemandeType_formalite_J00()
        => Assert.True(KbisTextChecks.MatchesDemandeType("D2613500028 | Formalité | En cours | J00127770311 | 982574717", wantDcademat: false));

    [Fact]
    public void MatchesDemandeType_dcademat()
        => Assert.True(KbisTextChecks.MatchesDemandeType("D2613500099 | DCADEMAT | En cours | J00120004858 | 123456789", wantDcademat: true));

    [Fact]
    public void MatchesDemandeType_dcademat_n_est_pas_une_formalite()
        // une ligne DCADEMAT ne doit PAS matcher la demande de type formalité même si elle a un J00
        => Assert.False(KbisTextChecks.MatchesDemandeType("D2613500099 | DCADEMAT | J00120004858", wantDcademat: false));

    [Fact]
    public void MatchesDemandeType_formalite_sans_J00_ne_matche_pas()
        => Assert.False(KbisTextChecks.MatchesDemandeType("D2613500028 | Formalité | sans liaison", wantDcademat: false));

    [Theory]
    [InlineData("", false)]
    [InlineData(null, true)]
    public void MatchesDemandeType_vide_retourne_false(string row, bool wantDca)
        => Assert.False(KbisTextChecks.MatchesDemandeType(row, wantDca));

    // ── DCADEMAT "Configurer le dépôt" : n° dépôt / facture / demande ────────
    // Échantillon = texte agrégé d'une ligne Exercices après Valider (case DCA cochée).
    private const string ExerciceRowSample =
        "DCA;01/01/2025;31/12/2025;DCA B2026/000052;26-000389;D2611200138;Déposé";

    [Fact]
    public void FindNumDemande_extrait_le_numero_prefixe_D()
        => Assert.Equal("D2611200138", KbisTextChecks.FindNumDemande(ExerciceRowSample));

    [Fact]
    public void FindNumDemande_colle_au_libelle_reste_trouve()
        => Assert.Equal("D2611200138", KbisTextChecks.FindNumDemande("n° de demandeD2611200138 Déposé"));

    [Theory]
    [InlineData("DCA;2025;DCA B2026/000052;26-000389;(pas encore validé)")] // pas de n° demande
    [InlineData("aucun numero ici")]
    [InlineData("")]
    [InlineData(null)]
    public void FindNumDemande_absent_retourne_null(string text)
        => Assert.Null(KbisTextChecks.FindNumDemande(text));

    [Fact]
    public void FindNumDepot_extrait_le_numero_de_depot()
        => Assert.Equal("DCA B2026/000052", KbisTextChecks.FindNumDepot(ExerciceRowSample));

    [Fact]
    public void FindNumDepot_tolere_espace_colle()
        => Assert.Equal("DCAB2026/000052", KbisTextChecks.FindNumDepot("dépôtDCAB2026/000052facture"));

    [Fact]
    public void FindNumFacture_extrait_le_numero_de_facture()
        => Assert.Equal("26-000389", KbisTextChecks.FindNumFacture(ExerciceRowSample));

    [Theory]
    [InlineData("01/01/2025;31/12/2025")] // dates, pas une facture AA-NNNNNN
    [InlineData("")]
    [InlineData(null)]
    public void FindNumFacture_absent_retourne_null(string text)
        => Assert.Null(KbisTextChecks.FindNumFacture(text));

    [Fact]
    public void HasDepotSuccess_ligne_validee_est_un_succes()
        => Assert.True(KbisTextChecks.HasDepotSuccess(ExerciceRowSample));

    [Theory]
    [InlineData("DCA;01/01/2025;31/12/2025;;;;")] // ligne pas encore validée (colonnes n° vides)
    [InlineData("")]
    [InlineData(null)]
    public void HasDepotSuccess_ligne_non_validee_est_faux(string text)
        => Assert.False(KbisTextChecks.HasDepotSuccess(text));
}

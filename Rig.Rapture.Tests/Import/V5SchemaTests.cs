using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Tests pour les schémas V5 (PC) et V4 (Contentieux) reçus de Pierre-Édouard
/// le 2026-05-15. Couvre les nouveaux champs introduits :
///
///   Niveau composition (présent dans les 2 schémas, "V5 composition") :
///     - composition_libelle
///     - president_id (int?)
///     - juges_ids (int[])
///     - greffier_id (int?)
///
///   Niveau affaire (présent uniquement dans V5 PC) :
///     - composition_specifique (override composition pour l'affaire)
///     - jc_avis (RichText : avis du juge commissaire)
///     - intervenants_rapture { jc, mj, aj, cj, cep } slots avec rig_ids[]
///     - contenu_decision_cloture (code court, ex "CIA")
///
/// Contrat important : sur Contentieux V4, les champs niveau affaire n'existent
/// pas → le parser DOIT les laisser à null sans throw.
/// </summary>
public class V5SchemaTests
{
    private static readonly string PcV5Path = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "rapture-smoke-pc-v5-aud28644-9995.json");

    private static readonly string ContentieuxV4Path = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "rapture-contentieux-v4.json");

    private static RaptureImportDto ParsePcV5()
    {
        File.Exists(PcV5Path).Should().BeTrue($"fixture absente : {PcV5Path}");
        return new RaptureImportParser().ParseFile(PcV5Path);
    }

    private static RaptureImportDto ParseContentieuxV4()
    {
        File.Exists(ContentieuxV4Path).Should().BeTrue($"fixture absente : {ContentieuxV4Path}");
        return new RaptureImportParser().ParseFile(ContentieuxV4Path);
    }

    // ─────────────────────────────────────────────────────────────────
    // V5 PC — header
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PcV5_header_fields_match_sample()
    {
        var dto = ParsePcV5();

        dto.CodeGreffe.Should().Be("9995");
        dto.DateAudience.Should().Be("2026-03-11");
        dto.HeureAudience.Should().Be("14:00");
        dto.Chambre.Should().Be("PC : clôtures de LJ");
        dto.Section.Should().BeNull("la fixture met section: null");
        dto.TypeAudience.Should().Be("procédures collectives");
    }

    [Fact]
    public void PcV5_has_44_affaires_all_cloture()
    {
        var dto = ParsePcV5();

        dto.Affaires.Should().HaveCount(44);
        dto.Affaires.Should().OnlyContain(a => a.TypeDecision == "cloture",
            "audience PC V5 = clôtures uniquement, pas d'ouverture_pc ni poursuite_po");
    }

    // ─────────────────────────────────────────────────────────────────
    // V5 — composition (juges_ids + greffier_id + president_id résolus)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PcV5_composition_has_v5_ids_resolved()
    {
        var dto = ParsePcV5();

        dto.Composition.Should().NotBeNull();
        dto.Composition.President.Should().Be("M. LESBROS Michel");
        dto.Composition.Greffier.Should().Be("Mme BOCCHIA Paola");

        // ── V5 fields ──
        dto.Composition.CompositionLibelle.Should().Be("M. LESBROS Michel");
        dto.Composition.PresidentId.Should().Be(3087);
        dto.Composition.JugesIds.Should().NotBeNull().And.BeEmpty(
            "juges array est [] (président seul → juges_ids[] aussi)");
        dto.Composition.GreffierId.Should().Be(17);
    }

    // ─────────────────────────────────────────────────────────────────
    // V5 — affaire 01_001 : tous les nouveaux champs remplis
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PcV5_affaire_01_001_has_composition_specifique()
    {
        var dto = ParsePcV5();

        var a = dto.Affaires.FirstOrDefault(x => x.NumeroAppel == "01_001");
        a.Should().NotBeNull();
        a!.CompositionSpecifique.Should().Be("M. LESBROS Michel",
            "override composition au niveau affaire");
    }

    [Fact]
    public void PcV5_affaire_01_001_has_jc_avis()
    {
        var dto = ParsePcV5();

        var a = dto.Affaires.First(x => x.NumeroAppel == "01_001");
        a.JcAvis.Should().NotBeNull();
        a.JcAvis.Valeur.Should().Be("absent, sans avis.");
        a.JcAvis.ValeurTexte.Should().Be("absent, sans avis.");
        a.JcAvis.Type.Should().Be("absent, sans avis.",
            "le champ type sert de discriminateur (V5)");
    }

    [Fact]
    public void PcV5_affaire_01_001_has_intervenants_rapture_jc_with_rig_ids()
    {
        var dto = ParsePcV5();

        var a = dto.Affaires.First(x => x.NumeroAppel == "01_001");
        a.IntervenantsRapture.Should().NotBeNull();
        a.IntervenantsRapture.Jc.Should().NotBeNull();
        a.IntervenantsRapture.Jc.Nom.Should().Be("Mme DEGASPERI Raphaëlle, M. GONON Bernard");
        a.IntervenantsRapture.Jc.RigIds.Should().BeEquivalentTo(new int?[] { 3046, 3009 });
        a.IntervenantsRapture.Jc.RaptureId.Should().BeNull();
    }

    [Fact]
    public void PcV5_affaire_01_001_has_intervenants_rapture_mj()
    {
        var dto = ParsePcV5();

        var a = dto.Affaires.First(x => x.NumeroAppel == "01_001");
        a.IntervenantsRapture.Mj.Should().NotBeNull();
        a.IntervenantsRapture.Mj.Nom.Should().Be("SELARL MJ SYNERGIE représentée par Me DESPRAT");
        a.IntervenantsRapture.Mj.RigIds.Should().BeEquivalentTo(new int?[] { 542 });
    }

    [Fact]
    public void PcV5_affaire_01_001_has_empty_aj_cj_cep_slots()
    {
        var dto = ParsePcV5();

        var a = dto.Affaires.First(x => x.NumeroAppel == "01_001");

        // Sur une clôture LJ on a JC + MJ, les autres slots sont vides (présents mais nom="" et rig_ids=[])
        a.IntervenantsRapture.Aj.Should().NotBeNull();
        a.IntervenantsRapture.Aj.Nom.Should().BeEmpty();
        a.IntervenantsRapture.Aj.RigIds.Should().BeEmpty();

        a.IntervenantsRapture.Cj.Should().NotBeNull();
        a.IntervenantsRapture.Cj.Nom.Should().BeEmpty();
        a.IntervenantsRapture.Cj.RigIds.Should().BeEmpty();

        a.IntervenantsRapture.Cep.Should().NotBeNull();
        a.IntervenantsRapture.Cep.Nom.Should().BeEmpty();
        a.IntervenantsRapture.Cep.RigIds.Should().BeEmpty();
    }

    [Fact]
    public void PcV5_affaire_01_001_has_contenu_decision_cloture_code()
    {
        var dto = ParsePcV5();

        var a = dto.Affaires.First(x => x.NumeroAppel == "01_001");
        a.ContenuDecisionCloture.Should().Be("CIA",
            "code court de décision de clôture (V5) — ici CIA = clôture pour insuffisance d'actif");
    }

    // ─────────────────────────────────────────────────────────────────
    // V5 PC — invariants à l'échelle de la fixture
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PcV5_rig_ids_tolerate_null_entries_when_rapture_did_not_resolve()
    {
        // Garde-fou anti-régression : sur cette fixture, certaines slots
        // intervenants_rapture ont rig_ids contenant des null (Rapture a vu
        // le nom mais n'a pas pu résoudre l'ID RIG). La déclaration DTO doit
        // donc être List<int?> et pas List<int>, sinon SerializationException
        // « ValueType System.Int32 ne peut pas être Null ».
        var dto = ParsePcV5();

        var slotsWithNulls = dto.Affaires
            .Where(a => a.IntervenantsRapture != null)
            .SelectMany(a => new[]
            {
                a.IntervenantsRapture.Jc,
                a.IntervenantsRapture.Mj,
                a.IntervenantsRapture.Aj,
                a.IntervenantsRapture.Cj,
                a.IntervenantsRapture.Cep,
            })
            .Where(s => s != null && s.RigIds != null && s.RigIds.Any(id => id == null))
            .ToList();

        slotsWithNulls.Should().NotBeEmpty(
            "la fixture PC V5 contient au moins une slot intervenants_rapture avec un rig_id null");
    }

    [Fact]
    public void PcV5_all_affaires_have_intervenants_rapture_block()
    {
        var dto = ParsePcV5();

        dto.Affaires.Should().OnlyContain(a => a.IntervenantsRapture != null,
            "le bloc intervenants_rapture est présent sur les 44 affaires PC V5");
    }

    [Fact]
    public void PcV5_all_affaires_have_contenu_decision_cloture_not_null()
    {
        var dto = ParsePcV5();

        // Toutes les 44 affaires PC ont contenu_decision_cloture renseigné
        // (peut être "CIA", "CEP", "CCH"...). Aucune ne doit être null.
        dto.Affaires.Should().OnlyContain(a => a.ContenuDecisionCloture != null);
    }

    // ─────────────────────────────────────────────────────────────────
    // V4 Contentieux — header + composition V5
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentieuxV4_header_fields_match_sample()
    {
        var dto = ParseContentieuxV4();

        dto.CodeGreffe.Should().Be("9995");
        dto.DateAudience.Should().Be("2026-05-22");
        dto.HeureAudience.Should().Be("14:30");
        dto.Chambre.Should().Be("Audience interactive");
        dto.Section.Should().Be("audience interactive");
        dto.TypeAudience.Should().Be("contentieux général");
    }

    [Fact]
    public void ContentieuxV4_has_10_affaires_all_poursuite_po()
    {
        var dto = ParseContentieuxV4();

        dto.Affaires.Should().HaveCount(10);
        dto.Affaires.Should().OnlyContain(a => a.TypeDecision == "poursuite_po",
            "audience contentieux = renvois/maintien (poursuite_po)");
    }

    [Fact]
    public void ContentieuxV4_composition_has_v5_fields_with_partial_ids()
    {
        var dto = ParseContentieuxV4();

        dto.Composition.President.Should().Be("Mme LOMBARD Florence");
        dto.Composition.Juges.Should().BeEquivalentTo(new[] { "M. LESBROS Michel", "M. RUBAT Gilles" });
        dto.Composition.Greffier.Should().Be("Mme ROCHE Marjorie");

        // ── V5 composition fields, partiellement résolus côté Rapture ──
        dto.Composition.CompositionLibelle.Should().BeEmpty();
        dto.Composition.PresidentId.Should().BeNull(
            "Rapture n'a pas résolu le president_id sur cet export");
        dto.Composition.JugesIds.Should().NotBeNull().And.BeEmpty(
            "juges présents par nom mais pas résolus en IDs RIG");
        dto.Composition.GreffierId.Should().Be(28,
            "seul le greffier a été résolu en ID");
    }

    // ─────────────────────────────────────────────────────────────────
    // V4 Contentieux — les champs niveau affaire V5 sont absents (null)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentieuxV4_affaire_level_v5_fields_are_null()
    {
        // Le schéma V4 Contentieux ne porte PAS les nouveaux champs niveau affaire
        // (composition_specifique / jc_avis / intervenants_rapture / contenu_decision_cloture).
        // Le parser doit donc les laisser à null sans throw.
        var dto = ParseContentieuxV4();

        dto.Affaires.Should().OnlyContain(a => a.CompositionSpecifique == null);
        dto.Affaires.Should().OnlyContain(a => a.JcAvis == null);
        dto.Affaires.Should().OnlyContain(a => a.IntervenantsRapture == null);
        dto.Affaires.Should().OnlyContain(a => a.ContenuDecisionCloture == null);
    }

    [Fact]
    public void ContentieuxV4_affaire_01_001_has_rich_text_fields()
    {
        // Garde-fou sur la régression : les champs RichText existants
        // (avis_mp, contenu_decision, note_audience_rapture) doivent toujours
        // se parser correctement après l'ajout des champs V5.
        var dto = ParseContentieuxV4();

        var a = dto.Affaires.First(x => x.NumeroAppel == "01_001");
        a.AvisMP.Should().NotBeNull();
        a.AvisMP.ValeurTexte.Should().Be("Test avis MP");
        a.ContenuDecision.Should().NotBeNull();
        a.ContenuDecision.ValeurTexte.Should().Be("Test contenu de la décision");
        a.NoteAudienceRapture.Should().NotBeNull();
        a.NoteAudienceRapture.ValeurTexte.Should().Be("Test note d'audience");
    }
}

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Tests de désérialisation pure du JSON Rapture. Pas de dépendance BDD.
/// Source du contrat : <c>Fixtures/rapture-sample-72-affaires.json</c> (copie
/// versionnée de Test.json fourni par Pierre-Édouard POURADIER DUTEIL,
/// greffe Grenoble 9995, audience 2026-05-15).
/// </summary>
public class ParserTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "rapture-sample-72-affaires.json");

    private static RaptureImportDto ParseFixture()
    {
        File.Exists(FixturePath).Should().BeTrue(
            $"fixture absente : {FixturePath} — vérifie que CopyToOutputDirectory marche dans le csproj");
        return new RaptureImportParser().ParseFile(FixturePath);
    }

    [Fact]
    public void Header_audience_fields_match_sample()
    {
        var dto = ParseFixture();

        dto.CodeGreffe.Should().Be("9995");
        dto.DateAudience.Should().Be("2026-05-15");
        dto.HeureAudience.Should().Be("09:00");
    }

    [Fact]
    public void Affaires_count_is_72()
    {
        var dto = ParseFixture();

        dto.Affaires.Should().NotBeNull();
        dto.Affaires.Should().HaveCount(72);
    }

    [Fact]
    public void Exactly_5_affaires_have_isProcedureCollective_flag()
    {
        var dto = ParseFixture();

        dto.Affaires.Count(a => a.IsProcedureCollective).Should().Be(5);
    }

    [Fact]
    public void Exactly_3_affaires_have_type_decision_ouverture_pc()
    {
        var dto = ParseFixture();

        dto.Affaires.Count(a => a.TypeDecision == "ouverture_pc").Should().Be(3);
    }

    [Fact]
    public void Exactly_2_affaires_have_decisionPC_block_filled()
    {
        // 3 sont en "ouverture_pc" mais seulement 2 ont le bloc decision_pc rempli
        // (la 3ème a le flag mais bloc vide → DecisionPC == null après désérialisation).
        var dto = ParseFixture();

        dto.Affaires.Count(a => a.DecisionPC != null).Should().Be(2);
    }

    [Fact]
    public void Affaire_01_006_decisionPC_redressement_judiciaire_with_mj_danguy()
    {
        var dto = ParseFixture();

        var affaire = dto.Affaires.FirstOrDefault(a => a.NumeroAppel == "01_006");
        affaire.Should().NotBeNull("l'affaire 01_006 'B&K RENOVATION' est listée dans la fixture");
        affaire!.IsProcedureCollective.Should().BeTrue();
        affaire.TypeDecision.Should().Be("ouverture_pc");
        affaire.DecisionPC.Should().NotBeNull();
        affaire.DecisionPC!.Type.Should().Be("Redressement judiciaire");
        affaire.DecisionPC.IntervenantsDesignes.Should().NotBeNull();
        affaire.DecisionPC.IntervenantsDesignes!.Mj.Should().Be("Me DANGUY Marie");
        affaire.DecisionPC.DateFinPO.Should().Be("12/11/2026");
    }

    [Fact]
    public void Affaire_01_001_has_all_rapture_text_fields()
    {
        var dto = ParseFixture();

        var affaire = dto.Affaires.FirstOrDefault(a => a.NumeroAppel == "01_001");
        affaire.Should().NotBeNull("l'affaire 01_001 a tous les champs Rapture remplis");
        affaire!.MinisterePublic.Should().Be("Guillaume GEORGES");
        affaire.AvisMP.Should().NotBeNull();
        affaire.AvisMP!.ValeurTexte.Should().Be("test avis MP");
        affaire.ContenuDecision.Should().NotBeNull();
        affaire.ContenuDecision!.ValeurTexte.Should().Be("test contenu de la décision");
    }

    [Fact]
    public void Parser_strips_utf8_bom_when_present()
    {
        // DataContractJsonSerializer ne tolère pas un BOM en tête de stream.
        // Le parser Rapture doit le strip silencieusement. On préfixe ici notre
        // fixture avec un BOM artificiel pour le tester quelle que soit l'encodage
        // de la fixture sur disque (qui n'a pas forcément de BOM).
        var fixtureBytes = File.ReadAllBytes(FixturePath);
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
        var withBom = new byte[bomBytes.Length + fixtureBytes.Length];
        Buffer.BlockCopy(bomBytes, 0, withBom, 0, bomBytes.Length);
        Buffer.BlockCopy(fixtureBytes, 0, withBom, bomBytes.Length, fixtureBytes.Length);

        var dto = new RaptureImportParser().ParseBytes(withBom);

        dto.Should().NotBeNull();
        dto.CodeGreffe.Should().Be("9995");
    }

    [Fact]
    public void Parser_works_on_fixture_without_bom()
    {
        // La fixture rapture-sample-72-affaires.json n'a pas de BOM. Le parser
        // doit aussi marcher dans ce cas (offset=0 sur la branche bytes).
        var bytes = File.ReadAllBytes(FixturePath);
        (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF).Should().BeFalse(
            "la fixture committée n'a pas de BOM (test complémentaire au Parser_strips_utf8_bom_when_present)");

        var dto = new RaptureImportParser().ParseBytes(bytes);
        dto.CodeGreffe.Should().Be("9995");
    }

    [Fact]
    public void Parser_throws_RaptureImportParseException_on_malformed_json()
    {
        var act = () => new RaptureImportParser().ParseString("{ not valid json ::: }");
        act.Should().Throw<RaptureImportParseException>();
    }
}

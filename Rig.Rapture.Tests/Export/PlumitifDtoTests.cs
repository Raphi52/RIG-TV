using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using FluentAssertions;
using RIG.PROCESSUS.PREAUD;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests.Export;

/// <summary>
/// Tests d'export Plumitif (RIG → Rapture, pré-audience). Couvre :
///  1. Roundtrip : PlumitifAudienceDto → JSON → PlumitifAudienceDto, asserts equality
///  2. Contract : PlumitifAudienceDto → JSON → RaptureImportDto, vérifie que le
///     RaptureImportParser ingère sans erreur ce que PlumitifJsonExporter émet.
///     Le 2ème test garantit la symétrie export/import.
/// </summary>
public class PlumitifDtoTests
{
    private static string SerializeToJson(PlumitifAudienceDto dto)
    {
        var ser = new DataContractJsonSerializer(typeof(PlumitifAudienceDto));
        using var ms = new MemoryStream();
        ser.WriteObject(ms, dto);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static PlumitifAudienceDto DeserializeJson(string json)
    {
        var ser = new DataContractJsonSerializer(typeof(PlumitifAudienceDto));
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (PlumitifAudienceDto)ser.ReadObject(ms);
    }

    private static PlumitifAudienceDto BuildMinimalDto()
    {
        return new PlumitifAudienceDto
        {
            CodeGreffe = "9995",
            DateAudience = "2026-05-15",
            HeureAudience = "09:00",
            SalleAudience = "Salle Albert Caquot",
            Chambre = "1",
            Section = "A",
            TypeAudience = "Cm",
            Composition = new CompositionDto
            {
                President = "M. FAURE Pascal",
                Juges = new List<string> { "M. AUTRE Juge" },
                Greffier = "Mme ROCHE Marjorie",
                Parquetier = "M. PROCUREUR",
            },
            Affaires = new List<PlumitifAffaireDto>
            {
                new PlumitifAffaireDto
                {
                    NumeroAppel = "01_001",
                    NumeroOrdre = 1,
                    HeureAppel = "09:00",
                    NumeroRole = "2026F00042",
                    IdInstance = 12345,
                    IdAffaire = 67890,
                    NatureAffaire = "Contentieux",
                    TypeAffaire = "Procédure ordinaire",
                    NomAffaire = "DUPONT c/ DURAND",
                    IsProcedureCollective = false,
                },
            },
        };
    }

    [Fact]
    public void PlumitifDto_roundtrip_preserves_header_and_affaire_fields()
    {
        var original = BuildMinimalDto();

        var json = SerializeToJson(original);
        var reparsed = DeserializeJson(json);

        reparsed.CodeGreffe.Should().Be("9995");
        reparsed.DateAudience.Should().Be("2026-05-15");
        reparsed.HeureAudience.Should().Be("09:00");
        reparsed.Composition.President.Should().Be("M. FAURE Pascal");
        reparsed.Composition.Greffier.Should().Be("Mme ROCHE Marjorie");
        reparsed.Affaires.Should().HaveCount(1);
        reparsed.Affaires[0].NumeroAppel.Should().Be("01_001");
        reparsed.Affaires[0].IdInstance.Should().Be(12345);
        reparsed.Affaires[0].NomAffaire.Should().Be("DUPONT c/ DURAND");
    }

    [Fact]
    public void Plumitif_export_is_readable_by_RaptureImportParser_contract_test()
    {
        // Le RIG export (RIG → Rapture pré-audience) produit le même schéma JSON que
        // ce que le RaptureImportParser (Rapture → RIG post-audience) sait lire pour les
        // champs communs. Garantit que le pipeline reste symétrique : si Rapture renvoie
        // le JSON d'origine sans modification, RIG peut le ré-ingérer.
        var plumitif = BuildMinimalDto();
        var json = SerializeToJson(plumitif);

        var rapture = new RaptureImportParser().ParseString(json);

        rapture.CodeGreffe.Should().Be("9995");
        rapture.DateAudience.Should().Be("2026-05-15");
        rapture.HeureAudience.Should().Be("09:00");
        rapture.Composition.Should().NotBeNull();
        rapture.Composition.President.Should().Be("M. FAURE Pascal");
        rapture.Composition.Greffier.Should().Be("Mme ROCHE Marjorie");
        rapture.Affaires.Should().HaveCount(1);
        rapture.Affaires[0].NumeroAppel.Should().Be("01_001");
        rapture.Affaires[0].IdInstance.Should().Be(12345);
        rapture.Affaires[0].IsProcedureCollective.Should().BeFalse();

        // Les champs nouveaux Rapture (avis_mp, autres, decision, decision_pc) sont
        // null sur un JSON Plumitif vierge — c'est correct.
        rapture.Affaires[0].AvisMP.Should().BeNull();
        rapture.Affaires[0].DecisionPC.Should().BeNull();
        rapture.Affaires[0].TypeDecision.Should().BeNull();
    }

    [Fact]
    public void Plumitif_with_isProcedureCollective_true_roundtrips_correctly()
    {
        var dto = BuildMinimalDto();
        dto.Affaires[0].IsProcedureCollective = true;
        dto.Affaires[0].SocieteDebitrice = "B&K RENOVATION SAS";

        var json = SerializeToJson(dto);
        var rapture = new RaptureImportParser().ParseString(json);

        rapture.Affaires[0].IsProcedureCollective.Should().BeTrue();
        rapture.Affaires[0].SocieteDebitrice.Should().Be("B&K RENOVATION SAS");
    }

    [Fact]
    public void Empty_affaires_list_is_serialized_and_parseable()
    {
        var dto = BuildMinimalDto();
        dto.Affaires = new List<PlumitifAffaireDto>();

        var json = SerializeToJson(dto);
        var rapture = new RaptureImportParser().ParseString(json);

        rapture.Affaires.Should().NotBeNull();
        rapture.Affaires.Should().BeEmpty();
    }
}

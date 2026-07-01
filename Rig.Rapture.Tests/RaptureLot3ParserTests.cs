using System;
using FluentAssertions;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// Tests de logique pure du parser EDI lot3 Rapture (DTO + DataContractJsonSerializer).
    /// Verrouille le CONTRAT de désérialisation câblé cette session :
    ///   - lecture du pivot id_audience (inc ②, arbitrage n°3) ;
    ///   - les 3 type_decision (poursuite_po / ouverture_pc / cloture) ;
    ///   - decision_pc + intervenants_rapture V5 (rapture_id = Int32, fait de schéma inc ①).
    /// Source-linké depuis wt-edilot3 (aucune dép RIG runtime) — données 100% FICTIVES (RGPD).
    /// </summary>
    public class RaptureLot3ParserTests
    {
        // JSON conforme au schéma unifié lot3, données fictives. Ordre des membres = ordre DataMember.
        private const string SampleJson = @"{
  ""id_audience"": 700123,
  ""codeGreffe"": ""TEST"",
  ""dateAudience"": ""2026-02-20"",
  ""heureAudience"": ""09:00"",
  ""salleAudience"": ""Salle 1"",
  ""chambre"": ""1ere chambre"",
  ""section"": ""Section commerciale"",
  ""typeAudience"": ""Audience PC"",
  ""composition"": { ""president"": ""Mme PRESIDENTE FICTIVE"", ""juges"": [""M. JUGE UN FICTIF""], ""greffier"": ""M. GREFFIER FICTIF"", ""president_id"": 101, ""juges_ids"": [201], ""greffier_id"": 301 },
  ""delibere"": ""2026-03-20"",
  ""composition_validee"": ""true"",
  ""audience_date_libre"": ""20 fevrier 2026"",
  ""schema_version"": ""5.0"",
  ""exported_by"": ""Rapture (jeu de test fictif)"",
  ""exported_at"": ""2026-02-20T11:30:00"",
  ""affaires"": [
    {
      ""numeroRole"": ""26RJ001"",
      ""idInstance"": 1001,
      ""isProcedureCollective"": true,
      ""noteAudience"": ""Note fictive 1"",
      ""type_decision"": ""poursuite_po"",
      ""decision"": { ""valeur"": ""PPO"", ""valeur_texte"": ""Poursuite PO"", ""label"": ""Poursuite PO"" }
    },
    {
      ""numeroRole"": ""26RJ002"",
      ""idInstance"": 1002,
      ""isProcedureCollective"": true,
      ""noteAudience"": ""Note fictive 2"",
      ""type_decision"": ""ouverture_pc"",
      ""decision"": { ""valeur"": ""RJ"", ""valeur_texte"": ""Ouverture RJ"", ""label"": ""Ouverture RJ"" },
      ""decision_pc"": {
        ""type"": ""1"",
        ""intervenants_designes"": { ""jc"": ""Mme JUGE UN FICTIF"", ""mj"": ""Me MANDATAIRE FICTIF"", ""aj"": """", ""cj"": """" },
        ""date_cessation"": ""2026-01-15"",
        ""date_fin_po"": ""2026-08-20"",
        ""case_patrimoine"": ""N""
      },
      ""intervenants_rapture"": {
        ""jc"": { ""nom"": ""Mme JUGE UN FICTIF"", ""rig_ids"": [201], ""rapture_id"": 1 },
        ""mj"": { ""nom"": ""Me MANDATAIRE FICTIF"", ""rig_ids"": [501], ""rapture_id"": 2 }
      }
    },
    {
      ""numeroRole"": ""24LJ010"",
      ""idInstance"": 1003,
      ""isProcedureCollective"": true,
      ""noteAudience"": ""Note fictive 3"",
      ""type_decision"": ""cloture"",
      ""decision"": { ""valeur"": ""CIA"", ""valeur_texte"": ""Cloture insuffisance actif"", ""label"": ""CIA"" },
      ""contenu_decision_cloture"": ""Cloture pour insuffisance d'actif (fictif).""
    }
  ]
}";

        private static RaptureImportDto Parse()
        {
            return new RaptureImportParser().ParseString(SampleJson);
        }

        [Fact]
        public void ParseString_lit_le_pivot_id_audience()
        {
            // inc ② : le DTO doit désérialiser id_audience (avant, le champ n'existait pas → 0).
            Parse().IdAudience.Should().Be(700123);
        }

        [Fact]
        public void ParseString_lit_entete_et_les_trois_affaires()
        {
            RaptureImportDto dto = Parse();
            dto.CodeGreffe.Should().Be("TEST");
            dto.DateAudience.Should().Be("2026-02-20");
            dto.Composition.Should().NotBeNull();
            dto.Composition.President.Should().Be("Mme PRESIDENTE FICTIVE");
            dto.Affaires.Should().NotBeNull();
            dto.Affaires.Count.Should().Be(3);
        }

        [Theory]
        [InlineData(0, "poursuite_po")]
        [InlineData(1, "ouverture_pc")]
        [InlineData(2, "cloture")]
        public void ParseString_reconnait_les_trois_types_de_decision(int index, string attendu)
        {
            Parse().Affaires[index].TypeDecision.Should().Be(attendu);
        }

        [Fact]
        public void ParseString_lit_decision_pc_et_intervenants_rapture_avec_rapture_id_entier()
        {
            RaptureAffaireDto aff = Parse().Affaires[1]; // ouverture_pc
            aff.DecisionPC.Should().NotBeNull();
            aff.DecisionPC.Type.Should().Be("1");
            aff.DecisionPC.IntervenantsDesignes.Jc.Should().Be("Mme JUGE UN FICTIF");
            aff.IntervenantsRapture.Should().NotBeNull();
            // Fait de schéma (inc ①) : rapture_id est un Int32, pas une chaîne.
            aff.IntervenantsRapture.Jc.RaptureId.Should().Be(1);
            aff.IntervenantsRapture.Mj.RaptureId.Should().Be(2);
        }

        [Fact]
        public void ParseString_buffer_vide_leve_ParseException()
        {
            RaptureImportParser parser = new RaptureImportParser();
            Assert.Throws<RaptureImportParseException>(() => parser.ParseString(""));
        }

        [Fact]
        public void ParseString_json_malforme_leve_ParseException()
        {
            RaptureImportParser parser = new RaptureImportParser();
            Assert.Throws<RaptureImportParseException>(() => parser.ParseString("{ ceci n'est pas du json"));
        }
    }
}

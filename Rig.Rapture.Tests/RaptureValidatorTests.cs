using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests {
	/// <summary>
	/// Couverture xUnit de RaptureImportValidator (P1, 2026-07-01) — logique PURE de pré-validation,
	/// SANS base de données. Stratégie offline :
	///   - CodeGreffe = "" ⇒ la vérif FK <c>Instance.GetInstance</c> (ValidateAffaire:142) est SKIPPÉE
	///     (elle n'a lieu que si codeGreffe non vide) ⇒ aucun accès DB.
	///   - Aucune composition / Decision.Valeur / DecisionPC.Type / intervenants renseignés ⇒ aucun
	///     appel <c>_mapper.Resolve*</c> (qui exigerait <c>Load()</c> = DB, sinon InvalidOperationException).
	/// Le mapper est donc construit mais jamais Load()é : inerte. On assarte des items PRÉCIS (field+severity),
	/// pas l'absence globale d'erreurs (le CodeGreffe="" ajoute volontairement une erreur header à ignorer).
	/// </summary>
	public class RaptureValidatorTests {

		static RaptureValidatorTests() {
			RigAssemblyResolver.Ensure();
		}

		private static RaptureImportValidator NewValidator() {
			// Mapper non-Load()é (cf. remarque de classe) — codeGreffe non vide juste pour satisfaire le ctor.
			return new RaptureImportValidator(new RaptureImportMapper("9995"));
		}

		private static bool HasItem(IEnumerable<RaptureValidationItem> items, string field, RaptureValidationSeverity sev) {
			foreach (var it in items) {
				if (it.Field == field && it.Severity == sev) return true;
			}
			return false;
		}

		private static bool HasField(IEnumerable<RaptureValidationItem> items, string field) {
			foreach (var it in items) {
				if (it.Field == field) return true;
			}
			return false;
		}

		// ---------- Garde-fous ctor / arguments ----------

		[Fact]
		public void Ctor_NullMapper_Throws() {
			Assert.Throws<ArgumentNullException>(() => new RaptureImportValidator(null));
		}

		[Fact]
		public void Validate_NullDto_Throws() {
			Assert.Throws<ArgumentNullException>(() => NewValidator().Validate(null));
		}

		// ---------- Header ----------

		[Fact]
		public void Header_MissingDateAudience_ReportsError() {
			var dto = new RaptureImportDto { CodeGreffe = "", DateAudience = null, Affaires = new List<RaptureAffaireDto>() };
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Header, "dateAudience", RaptureValidationSeverity.Error));
		}

		[Fact]
		public void Header_UnparsableDateAudience_ReportsError() {
			var dto = new RaptureImportDto { CodeGreffe = "", DateAudience = "pas une date", Affaires = new List<RaptureAffaireDto>() };
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Header, "dateAudience", RaptureValidationSeverity.Error));
		}

		[Theory]
		[InlineData("12/11/2026")]
		[InlineData("2026-11-12")]
		[InlineData("2026-11-12T09:30:00")]
		public void Header_ValidDateAudience_NoDateError(string date) {
			var dto = new RaptureImportDto { CodeGreffe = "", DateAudience = date, Affaires = new List<RaptureAffaireDto>() };
			var report = NewValidator().Validate(dto);
			Assert.False(HasField(report.Header, "dateAudience"));
		}

		[Fact]
		public void Affaires_Null_ReportsMissing() {
			var dto = new RaptureImportDto { CodeGreffe = "", DateAudience = "12/11/2026", Affaires = null };
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Header, "affaires", RaptureValidationSeverity.Error));
		}

		// ---------- Affaire ----------

		[Fact]
		public void Affaire_NullBlock_ReportsError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> { null }
			};
			var report = NewValidator().Validate(dto);
			Assert.Single(report.Affaires);
			Assert.True(HasItem(report.Affaires[0].Items, "affaire", RaptureValidationSeverity.Error));
		}

		[Fact]
		public void Affaire_MissingIdInstance_ReportsError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> { new RaptureAffaireDto { IdInstance = null } }
			};
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Affaires[0].Items, "idInstance", RaptureValidationSeverity.Error));
		}

		[Fact]
		public void Affaire_IdInstanceNegative_ReportsError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> { new RaptureAffaireDto { IdInstance = -1 } }
			};
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Affaires[0].Items, "idInstance", RaptureValidationSeverity.Error));
		}

		// ---------- type_decision ----------

		[Fact]
		public void TypeDecision_OuverturePc_WithoutDecisionPc_ReportsError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> {
					new RaptureAffaireDto { IdInstance = 5, TypeDecision = "ouverture_pc", DecisionPC = null }
				}
			};
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Affaires[0].Items, "decision_pc", RaptureValidationSeverity.Error));
		}

		[Fact]
		public void TypeDecision_Unknown_ReportsWarning() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> {
					new RaptureAffaireDto { IdInstance = 5, TypeDecision = "valeur_bidon" }
				}
			};
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Affaires[0].Items, "type_decision", RaptureValidationSeverity.Warning));
		}

		[Theory]
		[InlineData("poursuite_po")]
		[InlineData("ouverture_pc")]
		[InlineData("cloture")]
		public void TypeDecision_Known_NoTypeDecisionWarning(string td) {
			// DecisionPC fourni pour ouverture_pc (sinon erreur decision_pc, hors sujet ici) ; Type="" pour
			// éviter ResolveTypePC (mapper non Load()é). On vérifie juste l'ABSENCE de warning type_decision.
			var aff = new RaptureAffaireDto { IdInstance = 5, TypeDecision = td };
			if (td == "ouverture_pc") aff.DecisionPC = new RaptureDecisionPCDto { Type = "" };
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> { aff }
			};
			var report = NewValidator().Validate(dto);
			Assert.False(HasField(report.Affaires[0].Items, "type_decision"));
		}

		// ---------- decision_pc : dates ----------

		[Fact]
		public void DecisionPc_UnparsableDate_ReportsError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> {
					new RaptureAffaireDto {
						IdInstance = 5,
						DecisionPC = new RaptureDecisionPCDto { Type = "", DateCessation = "test date de cessation" }
					}
				}
			};
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Affaires[0].Items, "decision_pc.date_cessation", RaptureValidationSeverity.Error));
		}

		[Fact]
		public void DecisionPc_ValidDates_NoDateError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> {
					new RaptureAffaireDto {
						IdInstance = 5,
						DecisionPC = new RaptureDecisionPCDto {
							Type = "", DateCessation = "12/11/2026", DateFinPO = "2026-11-12", DateRenvoi = "01/01/2027"
						}
					}
				}
			};
			var report = NewValidator().Validate(dto);
			Assert.False(HasField(report.Affaires[0].Items, "decision_pc.date_cessation"));
			Assert.False(HasField(report.Affaires[0].Items, "decision_pc.date_fin_po"));
			Assert.False(HasField(report.Affaires[0].Items, "decision_pc.date_renvoi"));
		}

		[Fact]
		public void DecisionPc_MissingType_ReportsError() {
			var dto = new RaptureImportDto {
				CodeGreffe = "", DateAudience = "12/11/2026",
				Affaires = new List<RaptureAffaireDto> {
					new RaptureAffaireDto { IdInstance = 5, DecisionPC = new RaptureDecisionPCDto { Type = "" } }
				}
			};
			var report = NewValidator().Validate(dto);
			Assert.True(HasItem(report.Affaires[0].Items, "decision_pc.type", RaptureValidationSeverity.Error));
		}
	}
}

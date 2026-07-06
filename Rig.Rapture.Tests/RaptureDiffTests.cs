using System;
using System.Collections.Generic;
using RIG.METIER.JUDICIAIRE;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests {
	/// <summary>
	/// Couverture xUnit de RaptureImportDiff (audit rapture-smoke-coverage P1.5, 2026-07-06) —
	/// routing du moteur de diff SANS base de données. Stratégie offline :
	///   - AudienceCabinet construit hors DB (ctor codeGreffe, fallback objet non initialisé) :
	///     ses collections lazy (AppelAffaires) sont vides ou throw → les accès de Compute sont
	///     tous défensifs (IndexAppelsParInstance/SafeAudienceId en try/catch) → on exerce
	///     RÉELLEMENT les chemins AffairesIgnorees / erreur de pré-validation.
	///   - dto.Composition = null ⇒ DiffComposition sort immédiatement ⇒ aucun accès métier profond.
	/// NB : le skew d'identité d'assembly documenté dans le csproj (l.93-97) concerne DematRapture,
	/// PAS RaptureImportDiff — vérifié par ce fichier même (il compile et tourne).
	/// </summary>
	public class RaptureDiffTests {

		static RaptureDiffTests() {
			RigAssemblyResolver.Ensure();
		}

		private static AudienceCabinet NewAudienceOffline() {
			// Le ctor n'accède JAMAIS à la DB : BASE_GREFFE_AUDIENCE_CABINET(codeGreffe) fait
			// _GetCodeGreffe + _Alloc (wrappers DBxxx vides en mémoire) — vérifié AUDIENCE_CABINET.cs:283-287.
			// AppelAffaires est lazy et son accès est défensif côté Compute (IndexAppelsParInstance try/catch).
			return new AudienceCabinet("9995");
		}

		private static RaptureAffaireDto Affaire(int idInstance, string numeroAppel) {
			return new RaptureAffaireDto { IdInstance = idInstance, NumeroAppel = numeroAppel };
		}

		// ---------- Garde-fous arguments ----------

		[Fact]
		public void Compute_NullDto_Throws() {
			Assert.Throws<ArgumentNullException>(
				() => new RaptureImportDiff().Compute(null, NewAudienceOffline(), null));
		}

		[Fact]
		public void Compute_NullAudience_Throws() {
			Assert.Throws<ArgumentNullException>(
				() => new RaptureImportDiff().Compute(new RaptureImportDto(), null, null));
		}

		// ---------- Routing des affaires ----------

		[Fact]
		public void Compute_AffairesNull_RendDiffVide() {
			var dto = new RaptureImportDto { CodeGreffe = "9995", Affaires = null };
			var diff = new RaptureImportDiff().Compute(dto, NewAudienceOffline(), null);
			Assert.Equal("9995", diff.CodeGreffe);
			Assert.Empty(diff.Affaires);
			Assert.Empty(diff.AffairesIgnorees);
		}

		[Fact]
		public void Compute_AffaireSansAppelAffaire_EstIgnoreeAvecRaison() {
			// Audience offline sans AppelAffaires → l'affaire ne peut pas être matchée :
			// elle DOIT finir en AffairesIgnorees (jamais diffée), avec la raison "introuvable".
			var dto = new RaptureImportDto {
				CodeGreffe = "9995",
				Affaires = new List<RaptureAffaireDto> { Affaire(12345, "24/00042") }
			};
			var diff = new RaptureImportDiff().Compute(dto, NewAudienceOffline(), null);
			Assert.Empty(diff.Affaires);
			var ignoree = Assert.Single(diff.AffairesIgnorees);
			Assert.Equal(0, ignoree.Index);
			Assert.Equal(12345, ignoree.IdInstance);
			Assert.Contains("introuvable", ignoree.RaisonIgnoree);
		}

		[Fact]
		public void Compute_AffaireEnErreurDePrevalidation_EstIgnoreeSansEtreDiffee() {
			// Une affaire flaggée Error par la pré-validation ne doit JAMAIS être diffée,
			// même si son idInstance existait : raison dédiée "Pré-validation en erreur".
			var dto = new RaptureImportDto {
				CodeGreffe = "9995",
				Affaires = new List<RaptureAffaireDto> { Affaire(777, "24/00007") }
			};
			var report = new RaptureValidationReport();
			var av = new RaptureAffaireValidation { Index = 0, IdInstance = 777 };
			av.Items.Add(RaptureValidationItem.Error("idInstance", "erreur de test"));
			report.Affaires.Add(av);

			var diff = new RaptureImportDiff().Compute(dto, NewAudienceOffline(), report);
			Assert.Empty(diff.Affaires);
			var ignoree = Assert.Single(diff.AffairesIgnorees);
			Assert.Equal("Pré-validation en erreur", ignoree.RaisonIgnoree);
		}

		[Fact]
		public void Compute_ErreurPrevalidation_NeContamineQueSonIndex() {
			// L'erreur porte l'Index 1 : l'affaire 0 (saine) suit le chemin normal
			// (ici "introuvable" faute d'AppelAffaire), l'affaire 1 le chemin pré-validation.
			var dto = new RaptureImportDto {
				CodeGreffe = "9995",
				Affaires = new List<RaptureAffaireDto> { Affaire(100, "24/00100"), Affaire(200, "24/00200") }
			};
			var report = new RaptureValidationReport();
			var av = new RaptureAffaireValidation { Index = 1, IdInstance = 200 };
			av.Items.Add(RaptureValidationItem.Error("idInstance", "erreur de test"));
			report.Affaires.Add(av);

			var diff = new RaptureImportDiff().Compute(dto, NewAudienceOffline(), report);
			Assert.Equal(2, diff.AffairesIgnorees.Count);
			Assert.Contains("introuvable", diff.AffairesIgnorees[0].RaisonIgnoree);
			Assert.Equal("Pré-validation en erreur", diff.AffairesIgnorees[1].RaisonIgnoree);
		}

		[Fact]
		public void Compute_AffaireNull_NApparaitDansAucuneListe() {
			var dto = new RaptureImportDto {
				CodeGreffe = "9995",
				Affaires = new List<RaptureAffaireDto> { null, Affaire(300, "24/00300") }
			};
			var diff = new RaptureImportDiff().Compute(dto, NewAudienceOffline(), null);
			// L'entrée null n'apparaît ni en Affaires ni en AffairesIgnorees ;
			// la vraie affaire (Index 1) suit son chemin normal.
			var ignoree = Assert.Single(diff.AffairesIgnorees);
			Assert.Equal(1, ignoree.Index);
		}

		[Fact]
		public void Compute_AffaireSansIdInstance_EstIgnoree() {
			// Branche court-circuit `a.IdInstance != null &&` (RaptureImportDiff.cs:67) :
			// une affaire sans idInstance ne peut pas matcher → ignorée, jamais diffée.
			var dto = new RaptureImportDto {
				CodeGreffe = "9995",
				Affaires = new List<RaptureAffaireDto> {
					new RaptureAffaireDto { IdInstance = null, NumeroAppel = "24/00400" }
				}
			};
			var diff = new RaptureImportDiff().Compute(dto, NewAudienceOffline(), null);
			Assert.Empty(diff.Affaires);
			var ignoree = Assert.Single(diff.AffairesIgnorees);
			Assert.Null(ignoree.IdInstance);
			Assert.Contains("introuvable", ignoree.RaisonIgnoree);
		}
	}
}

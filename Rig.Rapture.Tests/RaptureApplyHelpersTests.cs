using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests {
	/// <summary>
	/// Couverture xUnit des HELPERS PURS de RaptureImportApplyService (P1, 2026-07-01).
	/// La méthode Apply() (ouvre SqlRigConnectionWriteOnly + écrit en base) reste HORS xUnit — couverte
	/// par les smoke tests --rapture-selfdrive/--apply. Ici on teste uniquement les utilitaires sans effet
	/// de bord ni DB : ParseDate, SqlStr/SqlStrMax, SqlIntNullable, FindAffaireDto. Tous private static →
	/// invoqués par réflexion (le code prod n'est PAS modifié pour exposer InternalsVisibleTo).
	/// </summary>
	public class RaptureApplyHelpersTests {

		static RaptureApplyHelpersTests() {
			RigAssemblyResolver.Ensure();
		}

		private static object InvokeStatic(string name, params object[] args) {
			var mi = typeof(RaptureImportApplyService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
			Assert.NotNull(mi); // garde-fou : renommage prod → test rouge explicite
			return mi.Invoke(null, args);
		}

		// ---------- ParseDate ----------

		[Theory]
		[InlineData("12/11/2026", 2026, 11, 12)] // dd/MM/yyyy
		[InlineData("2026-11-12", 2026, 11, 12)] // yyyy-MM-dd
		[InlineData("1/2/2026", 2026, 2, 1)]     // d/M/yyyy
		public void ParseDate_Valid_ReturnsDate(string raw, int y, int m, int d) {
			object r = InvokeStatic("ParseDate", raw);
			Assert.NotNull(r);
			Assert.Equal(new DateTime(y, m, d), (DateTime)r);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("test date de cessation")]
		[InlineData("32/13/2026")]
		public void ParseDate_InvalidOrEmpty_ReturnsNull(string raw) {
			Assert.Null(InvokeStatic("ParseDate", raw));
		}

		// ---------- SqlStr / SqlStrMax ----------

		[Fact]
		public void SqlStr_Null_ReturnsNullLiteral() {
			Assert.Equal("NULL", (string)InvokeStatic("SqlStr", new object[] { null }));
		}

		[Fact]
		public void SqlStr_Simple_QuotedUnicode() {
			Assert.Equal("N'abc'", (string)InvokeStatic("SqlStr", "abc"));
		}

		[Fact]
		public void SqlStr_ApostropheEscaped() {
			// Anti-injection basique : ' doublé.
			Assert.Equal("N'd''Arc'", (string)InvokeStatic("SqlStr", "d'Arc"));
		}

		[Fact]
		public void SqlStrMax_DelegueASqlStr() {
			Assert.Equal("N'x'", (string)InvokeStatic("SqlStrMax", "x"));
			Assert.Equal("NULL", (string)InvokeStatic("SqlStrMax", new object[] { null }));
		}

		// ---------- SqlIntNullable ----------

		[Fact]
		public void SqlIntNullable_Null_ReturnsNullLiteral() {
			Assert.Equal("NULL", (string)InvokeStatic("SqlIntNullable", new object[] { (int?)null }));
		}

		[Fact]
		public void SqlIntNullable_Value_ReturnsNumber() {
			Assert.Equal("42", (string)InvokeStatic("SqlIntNullable", new object[] { (int?)42 }));
		}

		// ---------- FindAffaireDto ----------

		[Fact]
		public void FindAffaireDto_ValidIndex_ReturnsAffaire() {
			var a0 = new RaptureAffaireDto { NumeroAppel = "A0" };
			var a1 = new RaptureAffaireDto { NumeroAppel = "A1" };
			var dto = new RaptureImportDto { Affaires = new List<RaptureAffaireDto> { a0, a1 } };
			Assert.Same(a1, (RaptureAffaireDto)InvokeStatic("FindAffaireDto", dto, 1));
		}

		[Theory]
		[InlineData(-1)]
		[InlineData(2)]
		[InlineData(99)]
		public void FindAffaireDto_OutOfRange_ReturnsNull(int index) {
			var dto = new RaptureImportDto { Affaires = new List<RaptureAffaireDto> { new RaptureAffaireDto() } };
			Assert.Null(InvokeStatic("FindAffaireDto", dto, index));
		}

		[Fact]
		public void FindAffaireDto_NullDtoOrList_ReturnsNull() {
			Assert.Null(InvokeStatic("FindAffaireDto", new object[] { null, 0 }));
			Assert.Null(InvokeStatic("FindAffaireDto", new RaptureImportDto { Affaires = null }, 0));
		}

		// ---------- Gardes anti-régression P2 (judge cycle 1) : SENS des mappings d'écriture ----------
		// Verrouillent la sémantique métier des écritures PARTIE (une inversion silencieuse Présent↔NC
		// serait invisible du build et de la batterie dry-run — c'est exactement le défaut canary planté).

		[Theory]
		[InlineData("Présent", "EP")] // partie présente → EP (présent en personne)
		[InlineData("Absent", "NC")]  // partie absente → NC (non comparant)
		public void MapPresenceCode_SensVerrouille(string newValue, string expected) {
			Assert.Equal(expected, RaptureImportApplyService.MapPresenceCode(newValue));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("Present")]  // sans accent = contrat FormatBool non respecté → refus d'écrire
		[InlineData("présent")]  // casse différente → refus
		[InlineData("true")]
		public void MapPresenceCode_ValeurInattendue_ReturnsNull(string newValue) {
			Assert.Null(RaptureImportApplyService.MapPresenceCode(newValue));
		}

		[Theory]
		[InlineData("demandeurs[X].avocatPlaidant", true)]
		[InlineData("defendeurs[Y SARL].avocatPostulant", false)]
		[InlineData("intervenants[Z].avocatPlaidant", true)]
		public void AvocatPathKind_PathsValides(string path, bool expectedPlaidant) {
			Assert.Equal(expectedPlaidant, RaptureImportApplyService.AvocatPathKind(path));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("demandeurs[X].present")]
		[InlineData("avocatPlaidantX")]
		public void AvocatPathKind_PathsInconnus_ReturnsNull(string path) {
			Assert.Null(RaptureImportApplyService.AvocatPathKind(path));
		}

		// ---------- Branches Skip sans DB : l'apply refuse proprement un diff présence sans cible ----------

		private static RaptureApplyReport InvokeApplyPartiePresence(FieldDiff fd) {
			var svc = new RaptureImportApplyService("9995", "xunit", "test.json", "1", "t", "t");
			var report = new RaptureApplyReport();
			var ad = new RaptureAffaireDiff { NumeroAppel = "T1" };
			var mi = typeof(RaptureImportApplyService).GetMethod("ApplyPartiePresence",
				BindingFlags.NonPublic | BindingFlags.Instance);
			Assert.NotNull(mi);
			// appaf/cnx null : les branches Skip testées sortent AVANT tout accès DB/appaf.
			mi.Invoke(svc, new object[] { fd, null, ad, null, report });
			return report;
		}

		[Fact]
		public void ApplyPartiePresence_SansIdPartie_Skip_SansException() {
			var report = InvokeApplyPartiePresence(new FieldDiff { Path = "d[X].present", NewValue = "Présent", IdPartie = null });
			Assert.Single(report.Skipped);
			Assert.Empty(report.Applied);
			Assert.Empty(report.Errors);
		}

		[Fact]
		public void ApplyPartiePresence_ValeurInattendue_Skip_SansException() {
			var report = InvokeApplyPartiePresence(new FieldDiff { Path = "d[X].present", NewValue = "peut-être", IdPartie = 123 });
			Assert.Single(report.Skipped);
			Assert.Empty(report.Applied);
		}
	}
}

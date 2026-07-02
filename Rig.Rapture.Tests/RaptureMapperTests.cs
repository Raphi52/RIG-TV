using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests {
	/// <summary>
	/// Couverture xUnit de RaptureImportMapper (P1, 2026-07-01) — logique de NORMALISATION et LOOKUP,
	/// SANS base de données (aucun Load(), donc aucun SELECT). On teste :
	///   - NormalizeName / NormalizeCode (internal static, accessibles car source linkée) : civilités,
	///     accents, espaces, ponctuation, casse.
	///   - LookupName / LookupString (private static) via réflexion, avec un index construit à la main
	///     → prouve le match « normalize-then-lookup » (ex. "Maître X" matche l'index bâti sur "Me X").
	///   - Resolution&lt;T&gt; (struct public) : statuts Empty/Found/NotFound.
	/// </summary>
	public class RaptureMapperTests {

		static RaptureMapperTests() {
			RigAssemblyResolver.Ensure();
		}

		// ---------- NormalizeName ----------

		[Theory]
		[InlineData("Me DANGUY Marie", "danguy marie")]        // civilité Me + lowercase
		[InlineData("Maître DANGUY Marie", "danguy marie")]    // Maître → même clé que Me (matching robuste)
		[InlineData("Maitre DANGUY Marie", "danguy marie")]    // sans accent
		[InlineData("M. FAURE Pascal", "faure pascal")]        // civilité M.
		[InlineData("Mme SIVERA Brigitte", "sivera brigitte")] // civilité Mme
		[InlineData("Mlle DUPONT Claire", "dupont claire")]    // civilité Mlle
		[InlineData("FAURÉ Pascal", "faure pascal")]           // accents supprimés
		[InlineData("  FAURE   Pascal  ", "faure pascal")]     // espaces collapsés + trim
		[InlineData("FAURE Pascal", "faure pascal")]           // déjà normalisé
		public void NormalizeName_Cases(string raw, string expected) {
			Assert.Equal(expected, RaptureImportMapper.NormalizeName(raw));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void NormalizeName_NullOrEmpty_ReturnsNull(string raw) {
			Assert.Null(RaptureImportMapper.NormalizeName(raw));
		}

		[Fact]
		public void NormalizeName_CiviliteEtAccent_ConvergentVersMemeCle() {
			// Le cœur du matching tolérant : deux graphies du même magistrat → clé identique.
			Assert.Equal(RaptureImportMapper.NormalizeName("Me DANGUY Marie"),
			             RaptureImportMapper.NormalizeName("Maître DANGUY Marie"));
		}

		// ---------- NormalizeCode ----------

		[Theory]
		[InlineData("R.J.", "rj")]                               // ponctuation jetée
		[InlineData("Redressement Judiciaire", "redressement judiciaire")]
		[InlineData("  LJ  ", "lj")]                             // trim
		[InlineData("Liquidation  judiciaire", "liquidation judiciaire")] // double espace collapsé
		[InlineData("Prévention", "prevention")]                 // accent
		[InlineData("Type-PC / EI", "typepc ei")]               // tirets & slash jetés, espace conservé
		public void NormalizeCode_Cases(string raw, string expected) {
			Assert.Equal(expected, RaptureImportMapper.NormalizeCode(raw));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void NormalizeCode_NullOrEmpty_ReturnsNull(string raw) {
			Assert.Null(RaptureImportMapper.NormalizeCode(raw));
		}

		// ---------- LookupName (private static) via réflexion ----------

		private static Resolution<int?> InvokeLookupName(Dictionary<string, int> index, string nom) {
			var mi = typeof(RaptureImportMapper).GetMethod("LookupName", BindingFlags.NonPublic | BindingFlags.Static);
			Assert.NotNull(mi); // garde-fou : signature renommée → test rouge explicite
			return (Resolution<int?>)mi.Invoke(null, new object[] { index, nom, "Juge" });
		}

		[Fact]
		public void LookupName_CiviliteVariante_Matche() {
			// Index bâti sur "Me DANGUY Marie" ; lookup avec "Maître DANGUY Marie" → Found (même clé normalisée).
			var index = new Dictionary<string, int> { { RaptureImportMapper.NormalizeName("Me DANGUY Marie"), 42 } };
			var r = InvokeLookupName(index, "Maître DANGUY Marie");
			Assert.True(r.IsFound);
			Assert.Equal(42, r.Value);
		}

		[Fact]
		public void LookupName_Inconnu_NotFound() {
			var index = new Dictionary<string, int> { { RaptureImportMapper.NormalizeName("Me DANGUY Marie"), 42 } };
			var r = InvokeLookupName(index, "M. INCONNU Personne");
			Assert.True(r.IsNotFound);
			Assert.Equal("M. INCONNU Personne", r.RawInput);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void LookupName_Vide_Empty(string nom) {
			var r = InvokeLookupName(new Dictionary<string, int>(), nom);
			Assert.True(r.IsEmpty);
		}

		// ---------- Resolution<T> ----------

		[Fact]
		public void Resolution_Empty_HasEmptyStatus() {
			var r = Resolution<int?>.Empty();
			Assert.True(r.IsEmpty);
			Assert.False(r.IsFound);
			Assert.False(r.IsNotFound);
		}

		[Fact]
		public void Resolution_Found_CarriesValue() {
			var r = Resolution<int?>.Found(7, "M. X");
			Assert.True(r.IsFound);
			Assert.Equal(7, r.Value);
			Assert.Equal("M. X", r.RawInput);
		}

		[Fact]
		public void Resolution_NotFound_CarriesRawAndLabel() {
			var r = Resolution<string>.NotFound("code inconnu", "CodePlumitif");
			Assert.True(r.IsNotFound);
			Assert.Equal("code inconnu", r.RawInput);
			Assert.Equal("CodePlumitif", r.RefLabel);
		}
	}
}

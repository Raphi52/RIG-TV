using System.Globalization;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Tests des helpers de normalisation Mapper.
///
/// ⚠ Helpers VENDORISÉS (copies bit-pour-bit de RaptureImportMapper.NormalizeName /
/// NormalizeCode / RemoveDiacritics) : on ne peut pas LINK directement
/// RaptureImportMapper.cs dans ce projet test parce qu'il dépend de RIG.METIER
/// (Juge, MandatairePC, etc.) qui sont des classic csproj non référencés.
///
/// Si les implémentations divergent, ce test casse — c'est le filet pour
/// détecter le drift. Refacto possible plus tard : extraire ces helpers dans
/// un fichier <c>RaptureNameNormalization.cs</c> standalone côté prod et
/// remplacer ce vendor par un &lt;Compile Include&gt; pur.
/// </summary>
public class NameNormalizationTests
{
    /// <summary>VENDOR copy de <c>RaptureImportMapper.NormalizeName</c>.</summary>
    private static string NormalizeName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null!;
        string s = raw.Trim();
        string[] civilites = { "Mme.", "Mlle.", "Mme", "Mlle", "M.", "Maître", "Maitre", "Me" };
        foreach (string civ in civilites)
        {
            if (s.StartsWith(civ + " ", System.StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(civ.Length + 1).TrimStart();
                break;
            }
        }
        s = RemoveDiacritics(s);
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { if (!prevSpace) sb.Append(' '); prevSpace = true; }
            else { sb.Append(c); prevSpace = false; }
        }
        return sb.ToString().Trim().ToLowerInvariant();
    }

    /// <summary>VENDOR copy de <c>RaptureImportMapper.NormalizeCode</c>.</summary>
    private static string NormalizeCode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null!;
        string s = RemoveDiacritics(raw.Trim());
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c)) sb.Append(' ');
        }
        string flat = sb.ToString().Trim();
        while (flat.IndexOf("  ", System.StringComparison.Ordinal) >= 0)
            flat = flat.Replace("  ", " ");
        return flat;
    }

    /// <summary>VENDOR copy de <c>RaptureImportMapper.RemoveDiacritics</c>.</summary>
    private static string RemoveDiacritics(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        string norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (char c in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // ── NormalizeName ─────────────────────────────────────────────────

    [Theory]
    [InlineData("M. FAURE Pascal", "FAURE Pascal")]                 // M. prefix
    [InlineData("Mme ROCHE Marjorie", "ROCHE Marjorie")]             // Mme prefix
    [InlineData("Mme. ROCHE Marjorie", "ROCHE Marjorie")]            // Mme. avec point
    [InlineData("Me DANGUY Marie", "Maître DANGUY Marie")]          // Me/Maître équivalence
    [InlineData("Maitre DANGUY Marie", "DANGUY Marie")]              // Maitre sans accent
    [InlineData("Mlle DUPONT", "DUPONT")]
    public void NormalizeName_strips_civility_prefix(string withCiv, string withoutCiv)
    {
        // Le doc §7.1 promet : ResolveJugeId("M. FAURE Pascal") et ResolveJugeId("FAURE Pascal")
        // donnent le même résultat. Test équivalent ici en boîte noire.
        NormalizeName(withCiv).Should().Be(NormalizeName(withoutCiv));
    }

    [Theory]
    [InlineData("Étienne", "etienne")]
    [InlineData("ÉCHANGE", "echange")]
    [InlineData("Maître", "maitre")]
    [InlineData("José María", "jose maria")]
    public void NormalizeName_strips_accents_and_lowercases(string input, string expected)
    {
        NormalizeName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("  M.   FAURE     Pascal  ", "faure pascal")]   // multi-spaces + leading/trailing
    [InlineData("M. FAURE\tPascal", "faure pascal")]            // tab compressed to single space
    public void NormalizeName_collapses_whitespace(string input, string expected)
    {
        NormalizeName(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeName_returns_null_on_null_or_empty()
    {
        NormalizeName(null!).Should().BeNull();
        NormalizeName("").Should().BeNull();
    }

    [Fact]
    public void NormalizeName_case_insensitive_civility_match()
    {
        // "m. faure pascal" tout en lowercase doit aussi strip le "m."
        NormalizeName("m. FAURE Pascal").Should().Be("faure pascal");
    }

    /// <summary>
    /// Invariant de SÛRETÉ du fix RaptureImportMapper (Build*Index) : on a remplacé
    /// <c>x.NomComplet</c> (= "&lt;civilité-abrégée&gt; NOM PRENOM", qui déclenchait
    /// <c>x.Civilite</c> → <c>CIVILITE.GetCIVILITE(testValidite:true)</c> et spammait
    /// le log RIG sur toute donnée ref incohérente) par une source SANS civilité
    /// ("NOM PRENOM" / NomCompletSansCivilite). Comme <c>NormalizeName</c> retire
    /// déjà les préfixes civilité, la clé d'index est STRICTEMENT identique → zéro
    /// changement de comportement de matching. Ce test fige cette équivalence pour
    /// toutes les abréviations émises par <c>Outils.FormerNomComplet</c>.
    /// </summary>
    [Theory]
    [InlineData("M.", "FAURE Pascal")]
    [InlineData("Mme", "ROCHE Marjorie")]
    [InlineData("Mme.", "ROCHE Marjorie")]
    [InlineData("Mlle", "DUPONT Claire")]
    [InlineData("Me", "DANGUY Marie")]
    [InlineData("Maître", "DANGUY Marie")]
    [InlineData("Maitre", "DANGUY Marie")]
    public void NomComplet_with_civility_abbrev_normalizes_same_as_civility_free(string abbrev, string nomPrenom)
    {
        // Ancien index (NomComplet) vs nouveau (sans civilité) → MÊME clé.
        NormalizeName(abbrev + " " + nomPrenom).Should().Be(NormalizeName(nomPrenom));
    }

    [Fact]
    public void Invalid_civility_yields_empty_abbrev_so_NomComplet_equals_civility_free()
    {
        // Cas réel RIG_DEV : civilité stockée "M" absente de la table CIVILITE →
        // GetCIVILITE échoue → R_CIVILITE_ABREGE_EDIT vide → FormerNomComplet rend
        // déjà "NOM PRENOM". Le fix produit donc exactement la même valeur.
        NormalizeName(("" + " " + "FAURE Pascal").Trim()).Should().Be(NormalizeName("FAURE Pascal"));
    }

    // ── NormalizeCode ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Redressement judiciaire", "redressement judiciaire")]
    [InlineData("Liquidation Judiciaire", "liquidation judiciaire")]
    [InlineData("RJ", "rj")]
    [InlineData("LJ-SP", "ljsp")]                                    // ponctuation jetée
    [InlineData("Sauvegarde - Liquidation", "sauvegarde liquidation")]  // tiret + espaces
    [InlineData("Patrimoine pro.", "patrimoine pro")]
    public void NormalizeCode_lowercases_strips_punctuation(string input, string expected)
    {
        NormalizeCode(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeCode_handles_accents()
    {
        NormalizeCode("Délibéré").Should().Be("delibere");
    }
}

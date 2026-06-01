using FluentAssertions;
using RIG.PROCESSUS.RETAUD.RAPTURE_IMPORT;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Tests pour <see cref="RaptureAudienceSanitizer"/> : map les LABELS humains
/// exportés par Rapture vers les CODES courts RIG (varchar(5)/(10)).
///
/// Couvre les cas réels observés sur les fixtures Pierre-Édouard :
///   - "PC : clôtures de LJ"      → "PC"   (split sur " : ")
///   - "procédures collectives"   → "PC"   (mapping libellé→code)
///   - "contentieux général"      → "CX"
///   - "Audience interactive"     → "Audie" (fallback truncate, pas idéal mais
///                                            évite "String or binary data would
///                                            be truncated" côté SQL Server)
///
/// Sans ce sanitizer, le `INSERT INTO AUDIENCE_CABINET` crash car AUDNC_CHAMBRE
/// = varchar(5) et AUDNC_TYPE_AUD_CAB = varchar(5) (cf log RIG legacy 16:27:10).
/// </summary>
public class AudienceSanitizerTests
{
    // ── SanitizeChambre ──────────────────────────────────────────────────

    [Theory]
    [InlineData("PC : clôtures de LJ", "PC")]
    [InlineData("PC : Examen périodes d'observation", "PC")]
    [InlineData("CX - AU - Audience de mise en état", "CX")]
    [InlineData("INT - Audience interactive", "INT")]
    [InlineData("REF - Référés", "REF")]
    [InlineData("DCP /Déclaration cessation paiement", "DCP")]
    [InlineData("ABC (description longue)", "ABC")]
    public void SanitizeChambre_splits_on_known_separators(string raw, string expected)
    {
        RaptureAudienceSanitizer.SanitizeChambre(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("Audience interactive", "Audie", "pas de séparateur → truncate brut à 5 chars")]
    [InlineData("PC", "PC", "déjà court : passe tel quel")]
    [InlineData("CLOT", "CLOT", "code court existant : passthrough")]
    public void SanitizeChambre_falls_back_to_truncate_when_no_separator(string raw, string expected, string because)
    {
        RaptureAudienceSanitizer.SanitizeChambre(raw).Should().Be(expected, because);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SanitizeChambre_handles_null_or_blank(string raw, string expected)
    {
        RaptureAudienceSanitizer.SanitizeChambre(raw).Should().Be(expected);
    }

    [Fact]
    public void SanitizeChambre_never_returns_more_than_5_chars()
    {
        // Garde-fou anti-régression : le résultat doit toujours fit dans AUDNC_CHAMBRE varchar(5)
        var samples = new[]
        {
            "Audience absolument géante",
            "Procédures collectives spéciales",
            "ABCDEFGHIJKLMNOP",
            "ABCDE",
            "ABCDEF",
        };
        foreach (var s in samples)
        {
            RaptureAudienceSanitizer.SanitizeChambre(s).Length.Should().BeLessThanOrEqualTo(5,
                $"chambre sanitisée pour '{s}' doit fit en varchar(5)");
        }
    }

    // ── SanitizeTypeAudCab ──────────────────────────────────────────────

    [Theory]
    [InlineData("procédures collectives", "PC")]
    [InlineData("Procédures Collectives", "PC", Skip = null)]  // case-insensitive
    [InlineData("contentieux général", "CX")]
    [InlineData("contentieux", "CX")]
    [InlineData("référés", "REF")]
    [InlineData("référé", "REF")]
    public void SanitizeTypeAudCab_maps_known_labels(string raw, string expected)
    {
        RaptureAudienceSanitizer.SanitizeTypeAudCab(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("Procédures Collectives", "PC")]
    [InlineData("PROCÉDURES COLLECTIVES", "PC")]
    [InlineData("Procedures collectives", "PC")]  // sans accent
    [InlineData("CONTENTIEUX général", "CX")]
    [InlineData("Refere", "REF")]                 // sans accent
    public void SanitizeTypeAudCab_is_case_and_accent_tolerant(string raw, string expected)
    {
        RaptureAudienceSanitizer.SanitizeTypeAudCab(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "CX")]
    [InlineData("", "CX")]
    public void SanitizeTypeAudCab_defaults_to_CX(string raw, string expected)
    {
        RaptureAudienceSanitizer.SanitizeTypeAudCab(raw).Should().Be(expected,
            "fallback contentieux est le cas legacy le plus fréquent");
    }

    [Fact]
    public void SanitizeTypeAudCab_never_returns_more_than_5_chars()
    {
        var samples = new[]
        {
            "unknown_type_extra_long",
            "AUTRE",
            "AB",
            "procédures collectives spéciales",
        };
        foreach (var s in samples)
        {
            RaptureAudienceSanitizer.SanitizeTypeAudCab(s).Length.Should().BeLessThanOrEqualTo(5,
                $"type sanitisé pour '{s}' doit fit en varchar(5)");
        }
    }

    // ── Truncate ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PC", 5, "PC")]
    [InlineData("ABCDE", 5, "ABCDE")]
    [InlineData("ABCDEF", 5, "ABCDE")]
    [InlineData("", 5, "")]
    [InlineData(null, 5, "")]
    public void Truncate_works(string raw, int max, string expected)
    {
        RaptureAudienceSanitizer.Truncate(raw, max).Should().Be(expected);
    }
}

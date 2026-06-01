using System;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Tests du parseur de date du Validator (RaptureImportValidator.TryParseDate).
///
/// ⚠ Vendor : copie bit-pour-bit du <c>TryParseDate</c> + array <c>DateFormats</c>
/// pour les mêmes raisons que NameNormalizationTests (dep RIG.METIER côté prod).
///
/// Cible le contrat §7.1 du doc :
///   « L'affaire 01_006 ne doit avoir aucune erreur sur les dates (toutes
///    parseables au format dd/MM/yyyy) »
/// </summary>
public class ValidatorDateTests
{
    // VENDOR copy des DateFormats acceptés (RaptureImportValidator.cs:38)
    private static readonly string[] DateFormats =
    {
        "dd/MM/yyyy",
        "d/M/yyyy",
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.fffZ",
    };

    /// <summary>VENDOR copy de <c>RaptureImportValidator.TryParseDate</c>.</summary>
    private static bool TryParseDate(string raw, out DateTime parsed)
    {
        if (string.IsNullOrEmpty(raw)) { parsed = default; return false; }
        if (DateTime.TryParseExact(raw.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed))
            return true;
        return DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
            || DateTime.TryParse(raw.Trim(), CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out parsed);
    }

    [Theory]
    [InlineData("12/11/2026", 2026, 11, 12)]   // Format Rapture observé (cas DateFinPO sample)
    [InlineData("12/07/2026", 2026, 7, 12)]    // Cas DatePoursuiteExceptionnelleActivite
    [InlineData("1/1/2027", 2027, 1, 1)]       // Format court d/M/yyyy
    [InlineData("2026-05-15", 2026, 5, 15)]    // ISO court
    [InlineData("2026-05-15T09:00:00", 2026, 5, 15)] // ISO long
    [InlineData("2026-05-15T09:00:00Z", 2026, 5, 15)] // ISO UTC
    public void TryParseDate_accepts_supported_formats(string raw, int year, int month, int day)
    {
        TryParseDate(raw, out var dt).Should().BeTrue($"format '{raw}' devrait être reconnu");
        dt.Year.Should().Be(year);
        dt.Month.Should().Be(month);
        dt.Day.Should().Be(day);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test date de cessation")]  // Cas dégradé observé dans le sample
    [InlineData("not a date")]
    public void TryParseDate_returns_false_on_invalid(string raw)
    {
        TryParseDate(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseDate_strips_leading_trailing_whitespace()
    {
        TryParseDate("  12/11/2026  ", out var dt).Should().BeTrue();
        dt.Year.Should().Be(2026);
    }

    [Fact]
    public void TryParseDate_affaire_01_006_dates_all_parse_at_dd_MM_yyyy()
    {
        // Validation directe du contrat §7.1 du doc : pour l'affaire 01_006, les
        // dates "12/11/2026" et "12/07/2026" doivent toutes parser sans erreur.
        TryParseDate("12/11/2026", out var fin).Should().BeTrue();
        TryParseDate("12/07/2026", out var po).Should().BeTrue();
        fin.Date.Should().Be(new DateTime(2026, 11, 12));
        po.Date.Should().Be(new DateTime(2026, 7, 12));
    }
}

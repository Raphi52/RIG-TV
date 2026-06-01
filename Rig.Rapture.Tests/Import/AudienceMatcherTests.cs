using System;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Tests du parser de date dans RaptureAudienceMatcher (logique pure, pas de DB).
/// Le Find() complet nécessite RIG.METIER + DB, donc pas testable directement ici
/// (ProjectReference trop lourde).
/// </summary>
public class AudienceMatcherTests
{
    /// <summary>VENDOR copy de <c>RaptureAudienceMatcher.TryParseDate</c>.</summary>
    private static bool TryParseDate(string raw, out DateTime parsed)
    {
        if (string.IsNullOrEmpty(raw)) { parsed = default; return false; }
        string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsed)
            || DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed);
    }

    [Theory]
    [InlineData("2026-05-15", 2026, 5, 15)]    // ISO format (Rapture JSON dateAudience)
    [InlineData("15/05/2026", 2026, 5, 15)]    // dd/MM/yyyy
    [InlineData("5/5/2026", 2026, 5, 5)]       // d/M/yyyy
    [InlineData("2026-12-31", 2026, 12, 31)]
    public void TryParseDate_accepts_supported_formats(string raw, int year, int month, int day)
    {
        TryParseDate(raw, out var dt).Should().BeTrue($"format '{raw}'");
        dt.Year.Should().Be(year);
        dt.Month.Should().Be(month);
        dt.Day.Should().Be(day);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("2026/13/45")]
    public void TryParseDate_rejects_invalid(string raw)
    {
        TryParseDate(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseDate_strips_whitespace()
    {
        TryParseDate("  2026-05-15  ", out var dt).Should().BeTrue();
        dt.Date.Should().Be(new DateTime(2026, 5, 15));
    }
}

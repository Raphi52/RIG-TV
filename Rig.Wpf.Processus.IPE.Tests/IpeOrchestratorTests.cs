using System;
using FluentAssertions;
using Rig.Wpf.Processus.Ipe;
using Xunit;

namespace Rig.Wpf.Processus.Ipe.Tests;

/// <summary>
/// Reproduit la logique <c>PROC_IPE.PrefixFacturationChangement2016</c> du legacy :
/// préfixe vide avant 2016-05-01, "16-" entre 2016-05-01 et 2018-05-01,
/// "18-" à partir de 2018-05-01.
/// </summary>
public class IpeOrchestratorTests
{
    [Theory]
    [InlineData("2014-01-15", "")]
    [InlineData("2016-04-30", "")]
    [InlineData("2016-05-01", "16-")]
    [InlineData("2017-12-31", "16-")]
    [InlineData("2018-04-30", "16-")]
    [InlineData("2018-05-01", "18-")]
    [InlineData("2024-06-15", "18-")]
    public void PrefixFacturation_FromDateSaisineGreffe(string dateIso, string expected)
    {
        var date = DateTime.Parse(dateIso, System.Globalization.CultureInfo.InvariantCulture);
        var sut = new IpeOrchestrator();

        sut.GetPrefixFacturation(date).Should().Be(expected);
    }

    [Fact]
    public void PrefixFacturation_OnNullDate_ReturnsEmpty()
    {
        new IpeOrchestrator().GetPrefixFacturation(null).Should().Be("");
    }
}

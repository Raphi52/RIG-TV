using FluentAssertions;
using Rig.Wpf.Processus.Mandataire;
using Rig.Wpf.Processus.Mandataire.Etapes;
using Rig.Wpf.RigMetier.Dtos;
using Xunit;

namespace Rig.Wpf.Processus.Mandataire.Tests;

public class MandataireMapperTests
{
    [Fact]
    public void Load_PopulatesEtape_FromDto()
    {
        var sut = new MandataireMapper();
        var dto = new MandataireDto(42, "MME", "Dupont", "DUP", "12345", false);
        var etape = new MandataireSaisieEtapeViewModel();

        sut.Load(dto, etape);

        etape.Civilite.Should().Be("MME");
        etape.Nom.Should().Be("Dupont");
        etape.Abrege.Should().Be("DUP");
        etape.NumeroCnbf.Should().Be("12345");
        etape.EstActif.Should().BeFalse();
    }

    [Fact]
    public void Save_PopulatesDto_FromEtape_KeepingId()
    {
        var sut = new MandataireMapper();
        var etape = new MandataireSaisieEtapeViewModel
        {
            Civilite = "M",
            Nom = "Martin",
            Abrege = "MAR",
            NumeroCnbf = "678",
            EstActif = true
        };
        var dto = new MandataireDto(7, "OLD", "Ancien", null, null, false);

        sut.Save(etape, ref dto);

        dto.Id.Should().Be(7);
        dto.Civilite.Should().Be("M");
        dto.Nom.Should().Be("Martin");
        dto.Abrege.Should().Be("MAR");
        dto.NumeroCnbf.Should().Be("678");
        dto.EstActif.Should().BeTrue();
    }

    [Fact]
    public void Save_OnNewEtape_ProducesDto_WithIdZero()
    {
        var sut = new MandataireMapper();
        var etape = new MandataireSaisieEtapeViewModel
        {
            Civilite = "PM",
            Nom = "Cabinet"
        };

        var dto = sut.SaveAsNew(etape);

        dto.Id.Should().Be(0);
        dto.Civilite.Should().Be("PM");
        dto.Nom.Should().Be("Cabinet");
    }
}

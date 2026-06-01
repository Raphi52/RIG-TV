using FluentAssertions;
using Rig.Wpf.Processus.Mandataire.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.Mandataire.Tests.Etapes;

public class MandataireSaisieEtapeViewModelTests
{
    [Fact]
    public void NewInstance_IsInvalid_WhenNomEmpty()
    {
        var sut = new MandataireSaisieEtapeViewModel();
        sut.IsValid.Should().BeFalse();
    }

    [Fact]
    public void NewInstance_OffersStandardCivilites()
    {
        var sut = new MandataireSaisieEtapeViewModel();
        sut.CivilitesDisponibles.Should().Contain(new[] { "M", "MME", "PM", "ND" });
    }

    [Fact]
    public void NewInstance_DefaultsToActif()
    {
        var sut = new MandataireSaisieEtapeViewModel();
        sut.EstActif.Should().BeTrue();
    }

    [Fact]
    public void IsValid_True_WhenCiviliteAndNomFilled()
    {
        var sut = new MandataireSaisieEtapeViewModel
        {
            Civilite = "M",
            Nom = "Dupont"
        };
        sut.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_False_WhenCiviliteMissing()
    {
        var sut = new MandataireSaisieEtapeViewModel { Nom = "Dupont" };
        sut.IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_False_WhenNomBlank()
    {
        var sut = new MandataireSaisieEtapeViewModel
        {
            Civilite = "M",
            Nom = "   "
        };
        sut.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SettingFields_RaisesIsValidChanged_OnTransitions()
    {
        var sut = new MandataireSaisieEtapeViewModel();
        var raised = 0;
        sut.IsValidChanged += (_, _) => raised++;

        sut.Nom = "Dupont";    // toujours invalide (pas de civilité)
        sut.Civilite = "M";    // → valide (transition false→true)
        sut.Nom = "Durand";    // toujours valide (pas de transition)
        sut.Civilite = "";     // → invalide (transition true→false)

        raised.Should().Be(2);
    }
}

using System;
using FluentAssertions;
using Rig.Wpf.Processus.Ipe;
using Rig.Wpf.Processus.Ipe.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.Ipe.Tests;

public class IpeProcessusViewModelTests
{
    private static IpeProcessusViewModel CreateSut()
        => new(new IpeOrchestrator());

    [Fact]
    public void NewInstance_HasCode_IPE()
    {
        CreateSut().Code.Should().Be("IPE");
    }

    [Fact]
    public void NewInstance_HasThreeEtapes_InOrder()
    {
        var sut = CreateSut();

        sut.Etapes.Should().HaveCount(3);
        sut.Etapes[0].Should().BeOfType<IpeDemandeEtapeViewModel>();
        sut.Etapes[1].Should().BeOfType<IpeFactureEtapeViewModel>();
        sut.Etapes[2].Should().BeOfType<IpeRecapEtapeViewModel>();
    }

    [Fact]
    public void GoNext_BlockedOnFirstEtape_WhenInvalid()
    {
        var sut = CreateSut();

        sut.GoNextCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void GoNext_AdvancesToFacture_WhenDemandeFilled()
    {
        var sut = CreateSut();
        var demande = (IpeDemandeEtapeViewModel)sut.Etapes[0];
        demande.TypeIp = TypeIp.Standard;
        demande.DateSaisineGreffe = new DateTime(2024, 6, 15);

        sut.GoNextCommand.Execute(null);

        sut.CurrentEtape.Should().BeOfType<IpeFactureEtapeViewModel>();
    }

    [Fact]
    public void Navigation_AcrossAll3Etapes_Works()
    {
        var sut = CreateSut();
        var demande = (IpeDemandeEtapeViewModel)sut.Etapes[0];
        var facture = (IpeFactureEtapeViewModel)sut.Etapes[1];
        demande.TypeIp = TypeIp.TribunalDigital;
        demande.DateSaisineGreffe = new DateTime(2024, 1, 1);
        facture.Montant = 250m;

        sut.GoNextCommand.Execute(null);
        sut.GoNextCommand.Execute(null);

        sut.CurrentEtape.Should().BeOfType<IpeRecapEtapeViewModel>();
        sut.GoNextCommand.CanExecute(null).Should().BeFalse(
            "il n'y a pas d'étape suivante après le récap");
        sut.GoPreviousCommand.Execute(null);
        sut.CurrentEtape.Should().BeOfType<IpeFactureEtapeViewModel>();
    }

    [Fact]
    public void SaveCommand_Disabled_UntilDemandeAndFactureValid()
    {
        var sut = CreateSut();
        sut.SaveCommand.CanExecute(null).Should().BeFalse();

        var demande = (IpeDemandeEtapeViewModel)sut.Etapes[0];
        demande.TypeIp = TypeIp.Standard;
        demande.DateSaisineGreffe = new DateTime(2024, 6, 15);
        sut.SaveCommand.CanExecute(null).Should().BeFalse(
            "Facture toujours invalide (montant 0)");

        var facture = (IpeFactureEtapeViewModel)sut.Etapes[1];
        facture.Montant = 100m;
        sut.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void SaveCommand_SetsHasBeenSaved()
    {
        var sut = CreateSut();
        var demande = (IpeDemandeEtapeViewModel)sut.Etapes[0];
        demande.TypeIp = TypeIp.Standard;
        demande.DateSaisineGreffe = new DateTime(2024, 6, 15);
        var facture = (IpeFactureEtapeViewModel)sut.Etapes[1];
        facture.Montant = 100m;

        sut.SaveCommand.Execute(null);

        sut.HasBeenSaved.Should().BeTrue();
    }

    [Fact]
    public void Recap_ProjectionsReflectEtapesPrecedentes()
    {
        var sut = CreateSut();
        var demande = (IpeDemandeEtapeViewModel)sut.Etapes[0];
        var facture = (IpeFactureEtapeViewModel)sut.Etapes[1];
        var recap = (IpeRecapEtapeViewModel)sut.Etapes[2];

        demande.TypeIp = TypeIp.TribunalDigital;
        demande.DateSaisineGreffe = new DateTime(2024, 6, 15);
        facture.Montant = 247.5m;

        recap.TypeIpLibelle.Should().Be("Tribunal digital");
        recap.DateSaisineLibelle.Should().Be("15/06/2024");
        recap.MontantLibelle.Should().Contain("247");
        recap.PrefixFacturation.Should().Be("18-");
    }
}

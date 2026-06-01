using FluentAssertions;
using Moq;
using Rig.Wpf.Processus.Mandataire;
using Rig.Wpf.Processus.Mandataire.Etapes;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.InMemory;
using Rig.Wpf.RigMetier.Repositories;
using Xunit;

namespace Rig.Wpf.Processus.Mandataire.Tests;

public class MandataireProcessusViewModelTests
{
    private static MandataireProcessusViewModel CreateSut(IMandataireRepository? repo = null)
        => new(repo ?? new InMemoryMandataireRepository(), new MandataireMapper());

    [Fact]
    public void NewInstance_HasCode_MANDATAIRE()
    {
        CreateSut().Code.Should().Be("MANDATAIRE");
    }

    private static Rig.Wpf.Processus.Mandataire.Etapes.MandataireSaisieEtapeViewModel SaisieEtape(MandataireProcessusViewModel sut)
        => (Rig.Wpf.Processus.Mandataire.Etapes.MandataireSaisieEtapeViewModel)sut.Etapes[1];

    [Fact]
    public void NewInstance_HasRechercheAndSaisieEtapes()
    {
        var sut = CreateSut();
        sut.Etapes.Should().HaveCount(2);
        sut.Etapes[0].Should().BeOfType<Rig.Wpf.Processus.Mandataire.Etapes.MandataireRechercheEtapeViewModel>();
        sut.Etapes[1].Should().BeOfType<Rig.Wpf.Processus.Mandataire.Etapes.MandataireSaisieEtapeViewModel>();
    }

    [Fact]
    public void SaveCommand_Disabled_WhenEtapeInvalid()
    {
        CreateSut().SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SaveCommand_Enabled_WhenEtapeValid()
    {
        var sut = CreateSut();
        var etape = SaisieEtape(sut);
        etape.Civilite = "M";
        etape.Nom = "Dupont";

        sut.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void SaveCommand_OnNewMandataire_PersistsViaRepository_AndAssignsId()
    {
        var repo = new InMemoryMandataireRepository();
        var sut = CreateSut(repo);
        var etape = SaisieEtape(sut);
        etape.Civilite = "M";
        etape.Nom = "Dupont";
        etape.Abrege = "DUP";

        sut.SaveCommand.Execute(null);

        sut.IdMandataire.Should().BeGreaterThan(0);
        var saved = repo.GetById(sut.IdMandataire);
        saved.Should().NotBeNull();
        saved!.Nom.Should().Be("Dupont");
        saved.Abrege.Should().Be("DUP");
    }

    [Fact]
    public void SaveCommand_OnExistingMandataire_KeepsId_AndUpdates()
    {
        var repo = new InMemoryMandataireRepository();
        var initialId = repo.Save(new MandataireDto(0, "M", "Initial", null, null, true));
        var sut = CreateSut(repo);

        sut.LoadCommand.Execute(initialId);
        var etape = SaisieEtape(sut);
        etape.Nom = "Modifié";
        sut.SaveCommand.Execute(null);

        sut.IdMandataire.Should().Be(initialId);
        repo.GetById(initialId)!.Nom.Should().Be("Modifié");
    }

    [Fact]
    public void LoadCommand_OnUnknownId_LeavesEtapeEmpty()
    {
        var repo = new Mock<IMandataireRepository>();
        repo.Setup(r => r.GetById(It.IsAny<int>())).Returns((MandataireDto?)null);
        repo.Setup(r => r.GetActifs()).Returns(System.Array.Empty<MandataireDto>());
        repo.Setup(r => r.GetTous()).Returns(System.Array.Empty<MandataireDto>());
        var sut = CreateSut(repo.Object);

        sut.LoadCommand.Execute(99999);

        var etape = SaisieEtape(sut);
        etape.Nom.Should().BeNullOrEmpty();
        sut.IdMandataire.Should().Be(0);
    }
}

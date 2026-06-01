using FluentAssertions;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.InMemory;
using Xunit;

namespace Rig.Wpf.RigMetier.Tests;

public class InMemoryMandataireRepositoryTests
{
    [Fact]
    public void Save_NewMandataire_AssignsId()
    {
        var sut = new InMemoryMandataireRepository();
        var dto = new MandataireDto(0, "M", "Dupont", "DUP", null, true);

        var id = sut.Save(dto);

        id.Should().BeGreaterThan(0);
        sut.GetById(id).Should().NotBeNull()
            .And.Subject.As<MandataireDto>().Nom.Should().Be("Dupont");
    }

    [Fact]
    public void Save_ExistingMandataire_KeepsId_AndUpdates()
    {
        var sut = new InMemoryMandataireRepository();
        var id = sut.Save(new MandataireDto(0, "M", "Dupont", null, null, true));
        sut.Save(new MandataireDto(id, "MME", "Dupont", "DUP", null, false));

        var loaded = sut.GetById(id);

        loaded.Should().NotBeNull();
        loaded!.Civilite.Should().Be("MME");
        loaded.Abrege.Should().Be("DUP");
        loaded.EstActif.Should().BeFalse();
    }

    [Fact]
    public void GetActifs_ExcludesInactives()
    {
        var sut = new InMemoryMandataireRepository();
        sut.Save(new MandataireDto(0, "M", "A", null, null, true));
        sut.Save(new MandataireDto(0, "M", "B", null, null, false));
        sut.Save(new MandataireDto(0, "M", "C", null, null, true));

        sut.GetActifs().Should().HaveCount(2);
        sut.GetTous().Should().HaveCount(3);
    }

    [Fact]
    public void GetById_OnUnknownId_ReturnsNull()
    {
        var sut = new InMemoryMandataireRepository();
        sut.GetById(99999).Should().BeNull();
    }
}

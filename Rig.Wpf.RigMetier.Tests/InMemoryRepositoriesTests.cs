using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.RigMetier.DependencyInjection;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.InMemory;
using Rig.Wpf.RigMetier.Repositories;
using Xunit;

namespace Rig.Wpf.RigMetier.Tests;

public class InMemoryRepositoriesTests
{
    [Fact]
    public void Utilisateur_GetCourant_ReturnsAnonByDefault()
    {
        var sut = new InMemoryUtilisateurRepository();

        sut.GetCourant().Should().NotBeNull()
            .And.Subject.Should().BeEquivalentTo(new { Code = "ANON", EstActif = true });
    }

    [Fact]
    public void Utilisateur_AddThenGetByCode_RoundTrips()
    {
        var sut = new InMemoryUtilisateurRepository();
        sut.Add(new UtilisateurDto("ALICE", "Martin", "Alice", "RCS", true));

        var alice = sut.GetByCode("ALICE");

        alice.Should().NotBeNull();
        alice!.Nom.Should().Be("Martin");
    }

    [Fact]
    public void Utilisateur_GetByService_FiltersByCode()
    {
        var sut = new InMemoryUtilisateurRepository();
        sut.Add(new UtilisateurDto("U1", "Nom1", "Pre1", "RCS", true));
        sut.Add(new UtilisateurDto("U2", "Nom2", "Pre2", "JUD", true));
        sut.Add(new UtilisateurDto("U3", "Nom3", "Pre3", "RCS", false));

        var rcs = sut.GetByService("RCS");

        rcs.Should().HaveCount(2);
    }

    [Fact]
    public void Greffe_GetActifs_FiltersInactives()
    {
        var sut = new InMemoryGreffeRepository();
        sut.Add(new GreffeDto("G001", "Paris", "Paris", true));
        sut.Add(new GreffeDto("G002", "Lyon", "Lyon", false));
        sut.Add(new GreffeDto("G003", "Lille", null, true));

        sut.GetActifs().Should().HaveCount(2);
        sut.GetTous().Should().HaveCount(3);
    }

    [Fact]
    public void Demande_Save_AssignsIncrementalId()
    {
        var sut = new InMemoryDemandeRepository();
        var d1 = new DemandeDto(0, "TESTNLH", "G001", DemandeEtat.Entree,
            System.DateTime.UtcNow, "ANON");
        var d2 = new DemandeDto(0, "TESTNLH", "G001", DemandeEtat.Entree,
            System.DateTime.UtcNow, "ANON");

        var id1 = sut.Save(d1);
        var id2 = sut.Save(d2);

        id1.Should().BeGreaterThan(0);
        id2.Should().Be(id1 + 1);
    }

    [Fact]
    public void Demande_GetEnCours_FiltersTerminees()
    {
        var sut = new InMemoryDemandeRepository();
        sut.Save(new DemandeDto(0, "P", "G", DemandeEtat.Saisie, System.DateTime.UtcNow, "U"));
        sut.Save(new DemandeDto(0, "P", "G", DemandeEtat.Terminee, System.DateTime.UtcNow, "U"));
        sut.Save(new DemandeDto(0, "P", "G", DemandeEtat.Rejetee, System.DateTime.UtcNow, "U"));

        sut.GetEnCours("G").Should().HaveCount(1);
        sut.GetByGreffe("G").Should().HaveCount(3);
    }

    [Fact]
    public void DI_AddRigMetierInMemory_RegistersAllRepositories()
    {
        var services = new ServiceCollection();
        services.AddRigMetierInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetService<IUtilisateurRepository>().Should().BeOfType<InMemoryUtilisateurRepository>();
        provider.GetService<IGreffeRepository>().Should().BeOfType<InMemoryGreffeRepository>();
        provider.GetService<IDemandeRepository>().Should().BeOfType<InMemoryDemandeRepository>();
    }

    [Fact]
    public void DI_InMemoryRepositories_AreSingletons()
    {
        var services = new ServiceCollection();
        services.AddRigMetierInMemory();
        var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IUtilisateurRepository>();
        var second = provider.GetRequiredService<IUtilisateurRepository>();
        first.Should().BeSameAs(second);
    }
}

using FluentAssertions;
using Rig.Wpf.RigMetier.Legacy.Repositories;
using Xunit;

namespace Rig.Wpf.RigMetier.Legacy.Tests;

[Trait("Category", "Integration")]
[Collection("Legacy")]
public class LegacyRepositoriesIntegrationTests
{
    private readonly LegacyRigMetierFixture _fixture;

    public LegacyRepositoriesIntegrationTests(LegacyRigMetierFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void GreffeRepository_GetActifs_ReturnsAtLeastOneGreffe()
    {
        Skip.IfNot(_fixture.IsAvailable,
            "RigMetier legacy non initialisable (env non provisionné ou code greffe invalide).");

        var sut = new LegacyGreffeRepository(_fixture.Bootstrap);

        var actifs = sut.GetActifs();

        actifs.Should().NotBeEmpty(
            "GetListGreffeRigExploitation doit retourner au moins le greffe local.");
    }

    [SkippableFact]
    public void GreffeRepository_GetByCode_ReturnsCurrentGreffe()
    {
        Skip.IfNot(_fixture.IsAvailable,
            "RigMetier legacy non initialisable.");

        var sut = new LegacyGreffeRepository(_fixture.Bootstrap);

        var current = sut.GetByCode(_fixture.Bootstrap.CodeGreffe!);

        current.Should().NotBeNull();
        current!.Code.Should().Be(_fixture.Bootstrap.CodeGreffe);
    }

    [SkippableFact]
    public void UtilisateurRepository_GetCourant_ReturnsLoggedInUser()
    {
        Skip.IfNot(_fixture.IsAvailable,
            "RigMetier legacy non initialisable.");

        var sut = new LegacyUtilisateurRepository(_fixture.Bootstrap);

        var courant = sut.GetCourant();

        // L'utilisateur courant Windows peut ne pas exister dans la table
        // UTILISATEUR du greffe testé : on accepte null mais pas d'exception.
        if (courant is not null)
        {
            courant.Code.Should().NotBeNullOrEmpty();
        }
    }
}

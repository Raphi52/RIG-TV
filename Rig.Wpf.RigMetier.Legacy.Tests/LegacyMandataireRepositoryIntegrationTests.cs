using System.Linq;
using FluentAssertions;
using Rig.Wpf.RigMetier.Legacy.Repositories;
using Xunit;

namespace Rig.Wpf.RigMetier.Legacy.Tests;

[Trait("Category", "Integration")]
[Collection("Legacy")]
public class LegacyMandataireRepositoryIntegrationTests
{
    private readonly LegacyRigMetierFixture _fixture;

    public LegacyMandataireRepositoryIntegrationTests(LegacyRigMetierFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void GetTous_OnRealDb_ReturnsListWithoutCrash()
    {
        Skip.IfNot(_fixture.IsAvailable, "RigMetier legacy non initialisable.");

        var sut = new LegacyMandataireRepository(_fixture.Bootstrap);

        var all = sut.GetTous();

        // Aucune assertion sur la quantité — un greffe vide est valide.
        // L'important : pas d'exception, retour cohérent (liste non null).
        all.Should().NotBeNull();
    }

    [SkippableFact]
    public void GetActifs_OnRealDb_OnlyContainsActiveMandatataires()
    {
        Skip.IfNot(_fixture.IsAvailable, "RigMetier legacy non initialisable.");

        var sut = new LegacyMandataireRepository(_fixture.Bootstrap);

        var actifs = sut.GetActifs();

        actifs.Should().OnlyContain(m => m.EstActif);
    }

    [SkippableFact]
    public void GetById_OnUnknownId_ReturnsNull_WithoutCrash()
    {
        Skip.IfNot(_fixture.IsAvailable, "RigMetier legacy non initialisable.");

        var sut = new LegacyMandataireRepository(_fixture.Bootstrap);

        var unknown = sut.GetById(99999999);

        // 99 999 999 est très improbablement un id valide ; on accepte
        // null mais surtout pas d'exception non gérée.
        unknown.Should().BeNull();
    }

    [SkippableFact]
    public void Save_NotImplemented_ThrowsClearException()
    {
        Skip.IfNot(_fixture.IsAvailable, "RigMetier legacy non initialisable.");

        var sut = new LegacyMandataireRepository(_fixture.Bootstrap);
        var dto = new Rig.Wpf.RigMetier.Dtos.MandataireDto(0, "M", "TEST", "TST", null, true);

        FluentActions.Invoking(() => sut.Save(dto))
            .Should().Throw<System.NotSupportedException>(
                "Save n'est pas branché pour l'instant — l'exception doit être claire");
    }
}

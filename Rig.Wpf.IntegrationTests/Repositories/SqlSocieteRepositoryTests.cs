using FluentAssertions;
using Rig.Wpf.RigMetier.Sql;
using Rig.Wpf.RigMetier.Sql.Repositories;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Repositories;

[Trait("Category", "Integration")]
[Collection("RigDevSql")] // pas de parallélisme — évite les promotions DTC entre tests
public class SqlSocieteRepositoryTests
{
    private static SqlSocieteRepository CreateSut()
        => new(new RigMetierSqlOptions { ConnectionString = RigDevConnection.ConnectionString });

    private static bool RigDevReachable()
    {
        using var c = RigDevConnection.TryOpen();
        return c is not null;
    }

    [SkippableFact]
    public void Search_ByPrefixOnDenomination_ReturnsResultsOrEmpty()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        var sut = CreateSut();
        var results = sut.Search("A", maxResults: 10);

        results.Should().NotBeNull();
        results.Count.Should().BeLessThanOrEqualTo(10);
        results.Should().OnlyContain(s => s.IdDossier > 0);
        results.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Denomination));
    }

    [SkippableFact]
    public void GetById_WithUnknownId_ReturnsNull()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        var sut = CreateSut();
        sut.GetById(int.MaxValue).Should().BeNull();
    }

    [SkippableFact]
    public void GetById_WithExistingId_ReturnsSociete()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        var sut = CreateSut();
        var first = sut.Search("A", maxResults: 1);
        if (first.Count == 0) return;

        var byId = sut.GetById(first[0].IdDossier);
        byId.Should().NotBeNull();
        byId!.IdDossier.Should().Be(first[0].IdDossier);
        byId.Denomination.Should().Be(first[0].Denomination);
    }
}

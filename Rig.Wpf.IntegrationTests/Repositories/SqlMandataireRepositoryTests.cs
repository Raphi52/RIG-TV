using System;
using System.Data.SqlClient;
using System.Linq;
using System.Transactions;
using FluentAssertions;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Sql;
using Rig.Wpf.RigMetier.Sql.Repositories;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Repositories;

/// <summary>
/// Tests d'intégration du <see cref="SqlMandataireRepository"/> contre la table
/// <c>dbo.MANDATAIRE</c> de RIG_DEV. Chaque test s'enrôle dans une
/// <see cref="TransactionScope"/> qui n'est jamais <c>Complete</c>'d → toute
/// modification est rollback à la sortie. Aucune ligne n'est persistée.
/// Skip automatique si RIG_DEV est inaccessible.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RigDevSql")] // pas de parallélisme — évite les promotions DTC entre tests
public class SqlMandataireRepositoryTests
{
    private static SqlMandataireRepository CreateSut()
        => new(new RigMetierSqlOptions { ConnectionString = RigDevConnection.ConnectionString });

    private static TransactionScope BeginRollbackScope()
        // Suppress puis RequiresNew = on isole proprement la transaction du test
        // du contexte ambiant (xUnit peut en injecter un dans certains runners).
        => new(TransactionScopeOption.RequiresNew, new TransactionOptions
        {
            IsolationLevel = IsolationLevel.ReadCommitted,
            Timeout = TimeSpan.FromSeconds(30),
        });

    private static bool RigDevReachable()
    {
        using var c = RigDevConnection.TryOpen();
        return c is not null;
    }

    [SkippableFact]
    public void GetTous_ReturnsAtLeastOne_OrEmpty_OnRigDev()
    {
        Skip.IfNot(RigDevReachable(), $"RIG_DEV inaccessible — skip.");

        var sut = CreateSut();

        var all = sut.GetTous();

        all.Should().NotBeNull();
        // Pas de borne basse stricte : RIG_DEV peut être à 0 sur un environnement neuf.
    }

    [SkippableFact]
    public void Save_NewMandataire_AssignsId_AndCanBeReadBack_ThenRollback()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        using var scope = BeginRollbackScope();
        var sut = CreateSut();
        var marker = $"AUTO_TEST_{Guid.NewGuid():N}".Substring(0, 16);
        var dto = new MandataireDto(
            Id: 0,
            Civilite: "M",
            Nom: "TestMandataire " + marker,
            Abrege: marker,
            NumeroCnbf: null,
            EstActif: true);

        var newId = sut.Save(dto);
        var read = sut.GetById(newId);

        newId.Should().BeGreaterThan(0);
        read.Should().NotBeNull();
        read!.Nom.Should().Be(dto.Nom);
        read.Abrege.Should().Be(dto.Abrege);
        read.Civilite.Should().Be("M");
        read.EstActif.Should().BeTrue();
        // Pas de scope.Complete() → ROLLBACK automatique au Dispose.
    }

    [SkippableFact]
    public void Save_UpdatesExistingMandataire_ThenRollback()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        using var scope = BeginRollbackScope();
        var sut = CreateSut();
        var initialMarker = $"INIT_{Guid.NewGuid():N}".Substring(0, 16);
        var inserted = sut.Save(new MandataireDto(0, "M", "Initial " + initialMarker, initialMarker, null, true));

        var modifiedMarker = $"MOD_{Guid.NewGuid():N}".Substring(0, 16);
        var updateId = sut.Save(new MandataireDto(inserted, "Mme", "Modifié " + modifiedMarker, modifiedMarker, "AB1234", false));
        var read = sut.GetById(updateId);

        updateId.Should().Be(inserted);
        read.Should().NotBeNull();
        read!.Civilite.Should().Be("Mme");
        read.Nom.Should().StartWith("Modifié ");
        read.EstActif.Should().BeFalse();
        read.NumeroCnbf.Should().Be("AB1234");
    }

    [SkippableFact]
    public void GetActifs_OnlyReturnsActiveMandataires_AfterRollbackInsertion()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        using var scope = BeginRollbackScope();
        var sut = CreateSut();
        var markerActif = $"ACT_{Guid.NewGuid():N}".Substring(0, 16);
        var markerInactif = $"INA_{Guid.NewGuid():N}".Substring(0, 16);
        var actifId = sut.Save(new MandataireDto(0, "M", "ActifTest " + markerActif, markerActif, null, true));
        var inactifId = sut.Save(new MandataireDto(0, "M", "InactifTest " + markerInactif, markerInactif, null, false));

        var actifs = sut.GetActifs();

        actifs.Should().Contain(m => m.Id == actifId);
        actifs.Should().NotContain(m => m.Id == inactifId);
    }

    [SkippableFact]
    public void Save_WithMissingCivilite_Throws()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        var sut = CreateSut();

        var act = () => sut.Save(new MandataireDto(0, "", "Nom", null, null, true));

        act.Should().Throw<ArgumentException>();
    }

    [SkippableFact]
    public void GetById_WithUnknownId_ReturnsNull()
    {
        Skip.IfNot(RigDevReachable(), "RIG_DEV inaccessible — skip.");

        var sut = CreateSut();

        sut.GetById(int.MaxValue).Should().BeNull();
    }
}

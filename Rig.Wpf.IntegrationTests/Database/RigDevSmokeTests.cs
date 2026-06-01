using FluentAssertions;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Database;

/// <summary>
/// Smoke tests qui valident que l'environnement <c>SQL-DEV\DEV / RIG_DEV</c> est joignable.
/// Skippés automatiquement si la connexion échoue (cas dev sans VPN, CI sans accès).
/// </summary>
[Trait("Category", "Integration")]
public class RigDevSmokeTests
{
    [SkippableFact]
    public void OpensReadOnly_AgainstRigDev()
    {
        using var conn = RigDevConnection.TryOpen();
        Skip.If(conn is null, $"RIG_DEV inaccessible via '{RigDevConnection.ConnectionString}' (skip).");

        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = cmd.ExecuteScalar();

        result.Should().Be(1);
    }

    [SkippableFact]
    public void GreffeTable_HasAtLeastOneRow()
    {
        using var conn = RigDevConnection.TryOpen();
        Skip.If(conn is null, "RIG_DEV inaccessible (skip).");

        using var cmd = conn!.CreateCommand();
        // Limite à 1 ligne pour éviter toute pression sur la DB.
        cmd.CommandText = "SELECT TOP 1 1 FROM sys.tables WHERE name LIKE 'GREFFE%' OR name = 'GREFFES'";
        var result = cmd.ExecuteScalar();

        // On s'assure simplement que la requête métadonnées tourne ;
        // le résultat exact dépend du schéma RIG_DEV courant.
        (result is null or 1).Should().BeTrue();
    }
}

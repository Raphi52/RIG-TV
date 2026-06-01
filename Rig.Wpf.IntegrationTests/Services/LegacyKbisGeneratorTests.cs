using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Rig.Wpf.RigMetier.Legacy;
using Rig.Wpf.RigMetier.Legacy.Services;
using Rig.Wpf.RigMetier.Sql;
using Rig.Wpf.RigMetier.Sql.Repositories;
using Xunit;

namespace Rig.Wpf.IntegrationTests.Services;

[Trait("Category", "Integration")]
[Collection("RigDevSql")]
public class LegacyKbisGeneratorTests
{
    [SkippableFact]
    public async Task GeneratePdfAsync_OnExistingDossier_ProducesPdfFile()
    {
        // Skip si RIG_DEV inaccessible
        using (var c = RigDevConnection.TryOpen())
            Skip.If(c is null, "RIG_DEV inaccessible — skip.");

        // Trouve n'importe quel dossier dans RIG_DEV (préfixe "A" sur dénomination/SIREN/numGestion)
        var sqlOpts = new RigMetierSqlOptions { ConnectionString = RigDevConnection.ConnectionString };
        var sqlRepo = new SqlSocieteRepository(sqlOpts);
        var results = sqlRepo.Search("A", maxResults: 50);
        Skip.If(results.Count == 0, "Aucun dossier dans RIG_DEV pour ce test.");

        // Prend le premier dossier et dérive le code greffe depuis numGestion (ex "7401B00123" → "7401")
        var dossier = results[0];
        var codeGreffe = dossier.NumGestion.Length >= 4
            ? dossier.NumGestion.Substring(0, 4)
            : dossier.NumGestion;

        // Bootstrap legacy (P-invoke + Common.Init)
        var bootstrap = new LegacyRigMetierBootstrap(NullLogger<LegacyRigMetierBootstrap>.Instance);
        Skip.IfNot(bootstrap.Initialize(codeGreffe), "Legacy bootstrap KO.");

        var sut = new LegacyKbisGenerator(bootstrap, NullLogger<LegacyKbisGenerator>.Instance);

        string pdfPath;
        try
        {
            pdfPath = await sut.GeneratePdfAsync(dossier.IdDossier, codeGreffe);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("ConnectionString") ||
            ex.Message.IndexOf("connexion", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Skip.If(true, $"Connexion OleDb legacy non configurée pour le greffe {codeGreffe} — skip.");
            return; // unreachable, Skip throws
        }

        pdfPath.Should().NotBeNullOrEmpty();
        File.Exists(pdfPath).Should().BeTrue();
        new FileInfo(pdfPath).Length.Should().BeGreaterThan(1000, "un PDF K-bis pèse au moins 1 ko");

        // Vérifie la signature PDF (%PDF)
        var head = new byte[4];
        using var fs = File.OpenRead(pdfPath);
        fs.Read(head, 0, 4);
        System.Text.Encoding.ASCII.GetString(head).Should().Be("%PDF");
    }
}

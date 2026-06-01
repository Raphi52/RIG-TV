using System;
using System.Data.SqlClient;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Integration tests pour RaptureAudienceCreator. Reproduit le SQL INSERT
/// AUDIENCE_CABINET + audit AUDIT_IMPORT_RAPTURE et vérifie en BDD que le row
/// est bien là. Cleanup explicite à la fin pour ne pas polluer RIG_DEV.
///
/// ⚠ Vendor : pas LINK à RaptureAudienceCreator.cs qui dépend de RIG.METIER. On
/// reproduit la mécanique SQL en pur ADO.NET pour la couvrir.
///
/// Connection : SQL-DEV/DEV/RIG_DEV par défaut. Override via env
/// RIG_LEGACY_CONNECTION (cohérent avec le smoke).
/// </summary>
[Trait("Category", "DB")]
public class AudienceCreatorIntegrationTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
        ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";

    private const string TestCodeGreffe = "9995";

    [Fact]
    public void Create_audience_inserts_row_and_audit_then_cleanup()
    {
        // Random date dans le futur pour ne pas conflicter avec audiences réelles
        var date = new DateTime(2099, 1, 1).AddDays(new Random().Next(1000));
        var heure = "09:00";
        // Cols : chambre/heure/type max 5, section max 10
        var chambre = "TST";
        var section = "TSTRAP";
        var typeAudCab = "CX";

        int newId = 0;
        try
        {
            using (var conn = new SqlConnection(ConnectionString))
            {
                conn.Open();

                // INSERT (mêmes colonnes que RaptureAudienceCreator.CreateFromJson)
                var insertSql = @"
                    INSERT INTO AUDIENCE_CABINET (
                        AUDNC_AUDIENCE, AUDNC_TYPE_AUD_CAB, AUDNC_CHAMBRE, AUDNC_SECTION,
                        AUDNC_DATE, AUDNC_HEURE, AUDNC_ETAT_AUDIENCE,
                        AUDNC_NOMBRE_APPELS, AUDNC_NUM_APPEL_ATTRIB, AUDNC_PUBLIQUE,
                        AUDNC_TOP_DIFFUSION, AUDNC_NON_DISPONIBLE_WEB
                    ) VALUES (
                        1, @typeAudCab, @chambre, @section,
                        @date, @heure, 1,
                        0, 0, 1, 0, 0
                    ); SELECT CAST(SCOPE_IDENTITY() AS INT) AS id";
                using (var cmd = new SqlCommand(insertSql, conn))
                {
                    cmd.Parameters.AddWithValue("@typeAudCab", typeAudCab);
                    cmd.Parameters.AddWithValue("@chambre", chambre);
                    cmd.Parameters.AddWithValue("@section", section);
                    cmd.Parameters.AddWithValue("@date", date.Date);
                    cmd.Parameters.AddWithValue("@heure", heure);
                    var scalar = cmd.ExecuteScalar();
                    scalar.Should().NotBeNull();
                    newId = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
                }
                newId.Should().BeGreaterThan(0);

                // Audit log
                var auditSql = @"
                    INSERT INTO AUDIT_IMPORT_RAPTURE (
                        ARIMP_DATE, ARIMP_CODE_GREFFE, ARIMP_USER, ARIMP_ID_AUDNC,
                        ARIMP_TABLE_CIBLE, ARIMP_COLONNE_CIBLE, ARIMP_VALEUR_AVANT, ARIMP_VALEUR_APRES,
                        ARIMP_FICHIER_SOURCE, ARIMP_RAPTURE_PATH, ARIMP_RAPTURE_SCHEMA_VER
                    ) VALUES (
                        GETDATE(), @greffe, 'integration-test', @id,
                        'AUDIENCE_CABINET', '*', NULL, 'created from xUnit test',
                        'test://fixture.json', 'audience_header', '1.0'
                    )";
                using (var cmd = new SqlCommand(auditSql, conn))
                {
                    cmd.Parameters.AddWithValue("@greffe", TestCodeGreffe);
                    cmd.Parameters.AddWithValue("@id", newId);
                    cmd.ExecuteNonQuery();
                }

                // Verify : l'audience est bien là
                using (var cmd = new SqlCommand(
                    "SELECT AUDNC_TYPE_AUD_CAB, AUDNC_CHAMBRE, AUDNC_SECTION, AUDNC_DATE " +
                    "FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", newId);
                    using var dr = cmd.ExecuteReader();
                    dr.Read().Should().BeTrue();
                    dr.GetString(0).Should().Be(typeAudCab);
                    dr.GetString(1).Should().Be(chambre);
                    dr.GetString(2).Should().Be(section);
                    dr.GetDateTime(3).Date.Should().Be(date.Date);
                }

                // Verify : audit row exists
                using (var cmd = new SqlCommand(
                    "SELECT COUNT(*) FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC = @id " +
                    "AND ARIMP_TABLE_CIBLE = 'AUDIENCE_CABINET'", conn))
                {
                    cmd.Parameters.AddWithValue("@id", newId);
                    var n = Convert.ToInt32(cmd.ExecuteScalar());
                    n.Should().BeGreaterOrEqualTo(1);
                }
            }
        }
        finally
        {
            // Cleanup ALWAYS : delete audience + audit rows pour ne pas polluer RIG_DEV
            if (newId > 0)
            {
                using var conn = new SqlConnection(ConnectionString);
                conn.Open();
                using (var cmd = new SqlCommand(
                    "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", newId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SqlCommand(
                    "DELETE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", newId);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    [Fact]
    public void Insert_rejects_when_required_columns_missing()
    {
        // Test que les NOT NULL columns sont bien enforced. Cas dégénéré : type vide.
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();

        Action insertWithoutType = () =>
        {
            using var cmd = new SqlCommand(
                "INSERT INTO AUDIENCE_CABINET (AUDNC_AUDIENCE) VALUES (1)", conn);
            cmd.ExecuteNonQuery();
        };

        insertWithoutType.Should().Throw<SqlException>(
            "AUDNC_TYPE_AUD_CAB est NOT NULL sans default → INSERT doit fail");
    }
}

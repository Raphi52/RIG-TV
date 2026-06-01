using System;
using System.Data.SqlClient;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace Rig.Rapture.Tests.Import;

/// <summary>
/// Couvre le CONTRAT d'écriture de la couche « apply » Rapture (le maillon
/// RIG.METIER-dépendant non LINKable côté xUnit) : la convention de stockage
/// NOTE_PROCEDURE <c>NTPRC_ORIGINE_NOTE = 'RAPTURE_*'</c> + l'audit
/// <c>AUDIT_IMPORT_RAPTURE</c> append-only.
///
/// Ce que ApplyService garantit et qu'on fige ici :
///   - <see cref="RaptureNoteAccessor.SetForInstance"/> = UPSERT par
///     (parent, origine) : 1ère écriture → INSERT ; ré-import du même champ →
///     UPDATE in place de la MÊME row (last-writer-wins), PAS un doublon.
///   - <see cref="RaptureImportApplyService.AuditWrite"/> = INSERT append-only :
///     chaque apply (même rejoué) ajoute 1 ligne d'audit (l'historique des
///     versions est porté par l'audit, pas par NOTE_PROCEDURE).
///
/// ⚠ Vendor : on NE LINKe PAS RaptureNoteAccessor.cs / RaptureImportApplyService.cs
/// (ils dépendent de RIG.METIER + RIG.SQL, non instanciables hors WinForms/COM —
/// cf. csproj). On REPRODUIT à l'identique leur mécanique SQL en ADO.NET pur,
/// exactement comme <see cref="AudienceCreatorIntegrationTests"/> le fait pour
/// RaptureAudienceCreator. Source de vérité reproduite :
///   Source/RIG/DLL/Processus/PROC_RETAUD/RAPTURE_IMPORT/RaptureNoteAccessor.cs (SetByParent)
///   Source/RIG/DLL/Processus/PROC_RETAUD/RAPTURE_IMPORT/RaptureImportApplyService.cs (AuditWrite)
///
/// Cible : instance RÉELLE 494130 — une affaire de l'audience de test 28644
/// (greffe 9995), d'où le fixture rapture-pc-v5.json a été exporté (vérifié en
/// lecture seule sur RIG_DEV). On écrit sous une origine DÉDIÉE
/// 'RAPTURE_SMOKE_TEST' (0 row préexistante prouvée) → le cleanup final est
/// strictement borné, aucune donnée RIG réelle n'est touchée.
///
/// Connection : SQL-DEV/DEV/RIG_DEV par défaut, override env RIG_LEGACY_CONNECTION
/// (cohérent avec le smoke et AudienceCreatorIntegrationTests).
/// </summary>
[Trait("Category", "DB")]
public class RaptureNoteUpsertContractTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("RIG_LEGACY_CONNECTION")
        ?? @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";

    private const string TestCodeGreffe = "9995";

    /// <summary>Instance réelle de l'audience de test 28644 (greffe 9995). Existe en RIG_DEV.</summary>
    private const int TestInstanceId = 494130;

    /// <summary>
    /// Origine NOTE_PROCEDURE dédiée au test. Préfixe RAPTURE_ (donc filtrable
    /// comme les vraies notes Rapture) mais suffixe SMOKE_TEST jamais produit par
    /// le pipeline prod → 0 collision, cleanup final provablement borné.
    /// </summary>
    private const string TestOrigine = "RAPTURE_SMOKE_TEST";

    /// <summary>Marqueurs uniques pour borner le cleanup des lignes d'audit du test.</summary>
    private const string AuditFichierSource = "test://rapture-note-upsert-contract";
    private const string AuditUser = "rapture-upsert-contract-test";

    [Fact]
    public void Set_then_replay_updates_in_place_not_duplicate_and_audit_is_append_only()
    {
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();

        try
        {
            // Baseline : aucune note de test ne traîne (origine dédiée jamais produite en prod).
            CountTestNotes(conn).Should().Be(0, "l'origine dédiée ne doit jamais préexister");
            CountTestAudit(conn).Should().Be(0, "aucune ligne d'audit de test ne doit préexister");

            // ── 1er « apply » : champ note jamais écrit pour ce (instance, origine) → INSERT ──
            int id1 = SetNoteForInstance(conn, TestInstanceId, TestOrigine, "Rapture note v1 (import initial)");
            id1.Should().BeGreaterThan(0, "SetByParent retourne l'ID de la note insérée");
            CountTestNotes(conn).Should().Be(1, "1 seule row après le 1er import");
            ReadNoteText(conn, id1).Should().Be("Rapture note v1 (import initial)");
            AuditWrite(conn, TestInstanceId, before: null, after: "Rapture note v1 (import initial)");

            // ── Replay du MÊME import (même instance, même origine), valeur différente ──
            //    Contrat last-writer-wins : UPDATE in place de la row existante,
            //    PAS un 2e INSERT.
            int id2 = SetNoteForInstance(conn, TestInstanceId, TestOrigine, "Rapture note v2 (ré-import)");
            id2.Should().Be(id1, "replay = UPDATE de la MÊME row (last-writer-wins), pas un doublon");
            CountTestNotes(conn).Should().Be(1, "toujours 1 seule row après replay → aucun INSERT au 2e passage");
            ReadNoteText(conn, id1).Should().Be("Rapture note v2 (ré-import)", "le texte a été écrasé par le dernier import");
            AuditWrite(conn, TestInstanceId, before: "Rapture note v1 (import initial)", after: "Rapture note v2 (ré-import)");

            // ── Audit append-only : 2 imports → 2 lignes d'audit (l'historique des
            //    versions est porté par l'audit, pas par NOTE_PROCEDURE). ──
            CountTestAudit(conn).Should().Be(2, "AUDIT_IMPORT_RAPTURE est append-only : 1 ligne par apply, même rejoué");
        }
        finally
        {
            // Cleanup ALWAYS, strictement borné (origine dédiée + marqueurs d'audit
            // du test) → ne peut pas toucher de note/audit Rapture réelle.
            using var cleanup = new SqlConnection(ConnectionString);
            cleanup.Open();
            using (var cmd = new SqlCommand(
                "DELETE FROM NOTE_PROCEDURE WHERE NTPRC_ID_INSTN = @inst AND NTPRC_ORIGINE_NOTE = @org", cleanup))
            {
                cmd.Parameters.AddWithValue("@inst", TestInstanceId);
                cmd.Parameters.AddWithValue("@org", TestOrigine);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = new SqlCommand(
                "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_FICHIER_SOURCE = @src AND ARIMP_USER = @usr", cleanup))
            {
                cmd.Parameters.AddWithValue("@src", AuditFichierSource);
                cmd.Parameters.AddWithValue("@usr", AuditUser);
                cmd.ExecuteNonQuery();
            }
        }
    }

    #region Reproduction fidèle de RaptureNoteAccessor.SetByParent (NTPRC_ID_INSTN)

    /// <summary>
    /// Reproduit <c>RaptureNoteAccessor.SetByParent("NTPRC_ID_INSTN", …)</c> :
    ///   1. SELECT TOP 1 (ID, NUM_ORDRE) pour (instance, origine) ORDER BY NUM_ORDRE DESC
    ///   2. si trouvé → UPDATE texte + NTPRC_DATE=GETDATE() de cette row, retourne son ID
    ///   3. sinon → INSERT (NUM_ORDRE = max NUM_ORDRE du parent toutes origines + 1),
    ///      retourne SCOPE_IDENTITY()
    /// SQL paramétré (le contrat testé = la logique find/update/insert + NUM_ORDRE,
    /// pas l'échappement quote inline du prod).
    /// </summary>
    private static int SetNoteForInstance(SqlConnection conn, int idInstance, string origine, string texte)
    {
        int idExisting = 0;
        using (var find = new SqlCommand(
            "SELECT TOP 1 NTPRC_ID_NTPRC FROM NOTE_PROCEDURE " +
            "WHERE NTPRC_ID_INSTN = @inst AND NTPRC_ORIGINE_NOTE = @org " +
            "ORDER BY NTPRC_NUM_ORDRE DESC", conn))
        {
            find.Parameters.AddWithValue("@inst", idInstance);
            find.Parameters.AddWithValue("@org", origine);
            var scalar = find.ExecuteScalar();
            if (scalar != null && scalar != DBNull.Value)
                idExisting = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }

        if (idExisting > 0)
        {
            using var upd = new SqlCommand(
                "UPDATE NOTE_PROCEDURE SET NTPRC_TEXTE_NOTE = @txt, NTPRC_DATE = GETDATE() " +
                "WHERE NTPRC_ID_NTPRC = @id", conn);
            upd.Parameters.AddWithValue("@txt", (object)texte ?? DBNull.Value);
            upd.Parameters.AddWithValue("@id", idExisting);
            upd.ExecuteNonQuery();
            return idExisting;
        }

        int globalMax = 0;
        using (var gm = new SqlCommand(
            "SELECT ISNULL(MAX(NTPRC_NUM_ORDRE), 0) FROM NOTE_PROCEDURE WHERE NTPRC_ID_INSTN = @inst", conn))
        {
            gm.Parameters.AddWithValue("@inst", idInstance);
            globalMax = Convert.ToInt32(gm.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        int newNumOrdre = globalMax + 1;

        using var ins = new SqlCommand(
            "INSERT INTO NOTE_PROCEDURE (NTPRC_ID_INSTN, NTPRC_NUM_ORDRE, NTPRC_DATE, " +
            "NTPRC_TEXTE_NOTE, NTPRC_ORIGINE_NOTE, NTPRC_DIFFUSABLE) " +
            "VALUES (@inst, @ord, GETDATE(), @txt, @org, 0); SELECT CAST(SCOPE_IDENTITY() AS INT)", conn);
        ins.Parameters.AddWithValue("@inst", idInstance);
        ins.Parameters.AddWithValue("@ord", newNumOrdre);
        ins.Parameters.AddWithValue("@txt", (object)texte ?? DBNull.Value);
        ins.Parameters.AddWithValue("@org", origine);
        return Convert.ToInt32(ins.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ReadNoteText(SqlConnection conn, int idNote)
    {
        using var cmd = new SqlCommand(
            "SELECT NTPRC_TEXTE_NOTE FROM NOTE_PROCEDURE WHERE NTPRC_ID_NTPRC = @id", conn);
        cmd.Parameters.AddWithValue("@id", idNote);
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    private static int CountTestNotes(SqlConnection conn)
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM NOTE_PROCEDURE WHERE NTPRC_ID_INSTN = @inst AND NTPRC_ORIGINE_NOTE = @org", conn);
        cmd.Parameters.AddWithValue("@inst", TestInstanceId);
        cmd.Parameters.AddWithValue("@org", TestOrigine);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    #endregion

    #region Reproduction fidèle de RaptureImportApplyService.AuditWrite (append-only)

    /// <summary>
    /// Reproduit l'INSERT <c>AUDIT_IMPORT_RAPTURE</c> de
    /// <c>RaptureImportApplyService.AuditWrite</c> (table cible NOTE_PROCEDURE).
    /// Append-only : aucun UPDATE, 1 ligne par apply.
    /// </summary>
    private static void AuditWrite(SqlConnection conn, int idInstance, string before, string after)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO AUDIT_IMPORT_RAPTURE (" +
            " ARIMP_DATE, ARIMP_CODE_GREFFE, ARIMP_USER, ARIMP_ID_INSTN," +
            " ARIMP_TABLE_CIBLE, ARIMP_COLONNE_CIBLE, ARIMP_VALEUR_AVANT, ARIMP_VALEUR_APRES," +
            " ARIMP_FICHIER_SOURCE, ARIMP_RAPTURE_PATH, ARIMP_RAPTURE_SCHEMA_VER)" +
            " VALUES (" +
            " GETDATE(), @greffe, @usr, @inst," +
            " 'NOTE_PROCEDURE', @col, @before, @after," +
            " @src, 'note_audience_rapture', '1.0')", conn);
        cmd.Parameters.AddWithValue("@greffe", TestCodeGreffe);
        cmd.Parameters.AddWithValue("@usr", AuditUser);
        cmd.Parameters.AddWithValue("@inst", idInstance);
        cmd.Parameters.AddWithValue("@col", "NTPRC_TEXTE_NOTE (origine=" + TestOrigine + ")");
        cmd.Parameters.AddWithValue("@before", (object)before ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@after", (object)after ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@src", AuditFichierSource);
        cmd.ExecuteNonQuery();
    }

    private static int CountTestAudit(SqlConnection conn)
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_FICHIER_SOURCE = @src AND ARIMP_USER = @usr", conn);
        cmd.Parameters.AddWithValue("@src", AuditFichierSource);
        cmd.Parameters.AddWithValue("@usr", AuditUser);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    #endregion
}

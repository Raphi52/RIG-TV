using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.Sql.Repositories;

/// <summary>
/// Repository SQL direct sur <c>dbo.DOSSIER_RCS</c> JOIN <c>dbo.ENTREPRISE</c>.
/// Aucune dépendance au runtime legacy. Pas d'écriture (la phase C est en lecture seule).
/// </summary>
public sealed class SqlSocieteRepository : ISocieteRepository
{
    private readonly string _connectionString;

    public SqlSocieteRepository(RigMetierSqlOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("RigMetierSqlOptions.ConnectionString requis.", nameof(options));
        _connectionString = options.ConnectionString;
    }

    public IReadOnlyList<SocieteDto> Search(string filter, int maxResults = 50)
    {
        var list = new List<SocieteDto>();
        if (maxResults <= 0) return list;
        var f = (filter ?? "").Trim();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // TOP (@maxResults) côté SQL — évite de streamer 1M rows quand le filtre est vide
        // ou commence par une lettre commune. Le LIMIT client précédent ne sauvait pas le
        // server-side scan (full table sort sur ENTRP_DESIGNATION avant filter).
        cmd.CommandText = "SELECT TOP (@maxResults) " + SelectColumnsBody +
            " FROM dbo.DOSSIER_RCS d " +
            " LEFT JOIN dbo.ENTREPRISE e ON d.DSSRC_ID_RPRTR = e.ENTRP_ID_RPRTR " +
            " WHERE e.ENTRP_DESIGNATION LIKE @q + '%' " +
            "    OR d.DSSRC_SIREN       LIKE @q + '%' " +
            "    OR d.DSSRC_NUM_GESTION LIKE @q + '%' " +
            // Tiebreaker DSSRC_ID_DSRCS DESC : sans ça, deux dossiers homonymes
            // (ENTRP_DESIGNATION identique) sortent dans un ordre indéterminé entre
            // 2 appels SQL — un même 'click sur le 1er résultat' renvoie tantôt l'actif
            // local tantôt l'historique sur autre greffe → PDF échoue aléatoirement.
            // DESC = ID le plus récent en premier (typiquement le dossier actif).
            " ORDER BY e.ENTRP_DESIGNATION, d.DSSRC_ID_DSRCS DESC";
        cmd.Parameters.AddWithValue("@q", f);
        cmd.Parameters.AddWithValue("@maxResults", maxResults);
        using var dr = cmd.ExecuteReader();
        while (dr.Read())
        {
            list.Add(Map(dr));
        }
        return list;
    }

    public SocieteDto? GetById(int idDossier)
    {
        if (idDossier <= 0) return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns +
            " FROM dbo.DOSSIER_RCS d " +
            " LEFT JOIN dbo.ENTREPRISE e ON d.DSSRC_ID_RPRTR = e.ENTRP_ID_RPRTR " +
            " WHERE d.DSSRC_ID_DSRCS = @id";
        cmd.Parameters.AddWithValue("@id", idDossier);
        using var dr = cmd.ExecuteReader(CommandBehavior.SingleRow);
        return dr.Read() ? Map(dr) : null;
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // Colonnes sélectionnées — ordinal positions utilisées dans Map() :
    // [0] DSSRC_ID_DSRCS  [1] DSSRC_NUM_GESTION  [2] DSSRC_SIREN
    // [3] DSSRC_DATE_IMMAT  [4] DSSRC_DATE_RADIATION  [5] DSSRC_ETAT_DOSSIER
    // [6] ENTRP_DESIGNATION  [7] ENTRP_FORME_JURIDIQUE_UTI  [8] ENTRP_SIGLE
    // Body sans "SELECT" : permet d'insérer "SELECT TOP (@maxResults)" devant.
    private const string SelectColumnsBody =
        "d.DSSRC_ID_DSRCS, d.DSSRC_NUM_GESTION, d.DSSRC_SIREN, " +
        "d.DSSRC_DATE_IMMAT, d.DSSRC_DATE_RADIATION, d.DSSRC_ETAT_DOSSIER, " +
        "e.ENTRP_DESIGNATION, e.ENTRP_FORME_JURIDIQUE_UTI, e.ENTRP_SIGLE";
    private const string SelectColumns = "SELECT " + SelectColumnsBody;

    private static SocieteDto Map(IDataRecord dr) => new(
        IdDossier:      dr.GetInt32(0),
        NumGestion:     dr.IsDBNull(1) ? "" : dr.GetString(1),
        Siren:          dr.IsDBNull(2) ? null : dr.GetString(2),
        Denomination:   dr.IsDBNull(6) ? "(sans dénomination)" : dr.GetString(6),
        FormeJuridique: dr.IsDBNull(7) ? null : dr.GetString(7),
        Sigle:          dr.IsDBNull(8) ? null : dr.GetString(8),
        DateImmat:      dr.IsDBNull(3) ? DateTime.MinValue : dr.GetDateTime(3),
        DateRadiation:  dr.IsDBNull(4) ? (DateTime?)null : dr.GetDateTime(4),
        EtatDossier:    dr.IsDBNull(5) ? "" : dr.GetString(5));
}

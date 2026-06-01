using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using Rig.Wpf.RigMetier.Dtos;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.Sql.Repositories;

/// <summary>
/// Repository SQL direct contre la table <c>dbo.MANDATAIRE</c>. Pas de dépendance
/// au runtime legacy : ADO.NET pur, ouverture/fermeture de SqlConnection à chaque
/// appel, enrôlement automatique dans une <see cref="System.Transactions.TransactionScope"/>
/// ambiante si présente (ce qui permet aux tests d'intégration de roll-back proprement).
/// </summary>
public sealed class SqlMandataireRepository : IMandataireRepository
{
    private readonly string _connectionString;

    public SqlMandataireRepository(RigMetierSqlOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("RigMetierSqlOptions.ConnectionString requis.", nameof(options));
        _connectionString = options.ConnectionString;
    }

    public MandataireDto? GetById(int id)
    {
        if (id <= 0) return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " FROM dbo.MANDATAIRE WHERE MNDTR_ID_MNDTR = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var dr = cmd.ExecuteReader(CommandBehavior.SingleRow);
        return dr.Read() ? Map(dr) : null;
    }

    public IReadOnlyList<MandataireDto> GetTous()
        => Query(SelectColumns + " FROM dbo.MANDATAIRE ORDER BY MNDTR_NOM");

    public IReadOnlyList<MandataireDto> GetActifs()
        => Query(SelectColumns + " FROM dbo.MANDATAIRE WHERE MNDTR_ACTIF = 1 ORDER BY MNDTR_NOM");

    public int Save(MandataireDto m)
    {
        if (m is null) throw new ArgumentNullException(nameof(m));
        if (string.IsNullOrWhiteSpace(m.Civilite))
            throw new ArgumentException("Civilité requise.", nameof(m));
        if (string.IsNullOrWhiteSpace(m.Nom))
            throw new ArgumentException("Nom requis.", nameof(m));

        return m.Id == 0 ? Insert(m) : Update(m);
    }

    private int Insert(MandataireDto m)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // MNDTR_NUMERO_SOURCE : GUID auto-attribué — réplique le comportement de
        // RIG.METIER.JUDICIAIRE.Mandataire.CreateMandataire().
        cmd.CommandText =
            "INSERT INTO dbo.MANDATAIRE " +
            "(MNDTR_CIVILITE, MNDTR_NOM, MNDTR_ABREGE, MNDTR_NUMERO_CNBF, MNDTR_ACTIF, MNDTR_NUMERO_SOURCE) " +
            "VALUES (@civ, @nom, @abr, @cnbf, @actif, @src); " +
            "SELECT CAST(SCOPE_IDENTITY() AS int);";
        cmd.Parameters.AddWithValue("@civ", m.Civilite);
        cmd.Parameters.AddWithValue("@nom", m.Nom);
        cmd.Parameters.AddWithValue("@abr", (object?)m.Abrege ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cnbf", (object?)m.NumeroCnbf ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actif", m.EstActif);
        cmd.Parameters.AddWithValue("@src", Guid.NewGuid().ToString());
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    private int Update(MandataireDto m)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.MANDATAIRE SET " +
            "MNDTR_CIVILITE = @civ, " +
            "MNDTR_NOM = @nom, " +
            "MNDTR_ABREGE = @abr, " +
            "MNDTR_NUMERO_CNBF = @cnbf, " +
            "MNDTR_ACTIF = @actif " +
            "WHERE MNDTR_ID_MNDTR = @id";
        cmd.Parameters.AddWithValue("@id", m.Id);
        cmd.Parameters.AddWithValue("@civ", m.Civilite);
        cmd.Parameters.AddWithValue("@nom", m.Nom);
        cmd.Parameters.AddWithValue("@abr", (object?)m.Abrege ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cnbf", (object?)m.NumeroCnbf ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@actif", m.EstActif);
        var affected = cmd.ExecuteNonQuery();
        if (affected == 0)
            throw new InvalidOperationException(
                $"Aucun mandataire avec MNDTR_ID_MNDTR = {m.Id} — UPDATE rejeté.");
        return m.Id;
    }

    private IReadOnlyList<MandataireDto> Query(string sql)
    {
        var list = new List<MandataireDto>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var dr = cmd.ExecuteReader();
        while (dr.Read()) list.Add(Map(dr));
        return list;
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private const string SelectColumns =
        "SELECT MNDTR_ID_MNDTR, MNDTR_CIVILITE, MNDTR_NOM, MNDTR_ABREGE, MNDTR_NUMERO_CNBF, MNDTR_ACTIF";

    private static MandataireDto Map(IDataRecord dr) => new(
        Id: dr.GetInt32(0),
        Civilite: dr.IsDBNull(1) ? "" : dr.GetString(1),
        Nom: dr.IsDBNull(2) ? "" : dr.GetString(2),
        Abrege: dr.IsDBNull(3) ? null : dr.GetString(3),
        NumeroCnbf: dr.IsDBNull(4) ? null : dr.GetString(4),
        EstActif: !dr.IsDBNull(5) && dr.GetBoolean(5));
}

using System;
using System.Data.SqlClient;

namespace Rig.Wpf.IntegrationTests;

/// <summary>
/// Constantes et helpers pour atteindre RIG_DEV depuis les tests d'intégration.
/// La chaîne par défaut peut être surchargée via la variable d'environnement
/// <c>RIG_WPF_DEV_CONN</c>.
/// </summary>
public static class RigDevConnection
{
    public const string DefaultConnectionString =
        @"Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=3;";

    public static string ConnectionString
        => Environment.GetEnvironmentVariable("RIG_WPF_DEV_CONN") ?? DefaultConnectionString;

    /// <summary>
    /// Tente d'ouvrir une connexion à RIG_DEV. Retourne <c>null</c> si l'environnement
    /// ne permet pas la connexion (utile pour skipper les tests proprement).
    /// </summary>
    public static SqlConnection? TryOpen()
    {
        var conn = new SqlConnection(ConnectionString);
        try
        {
            conn.Open();
            return conn;
        }
        catch (Exception)
        {
            conn.Dispose();
            return null;
        }
    }
}

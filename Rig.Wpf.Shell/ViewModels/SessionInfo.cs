using System;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Rig.Wpf.Shell.ViewModels;

/// <summary>
/// Informations de session affichées dans la sidebar et le Dashboard :
/// utilisateur courant + base de données cible. Dérivées de la config
/// (<c>RigWpf:UserDisplayName</c>, <c>RigWpf:UserRole</c>,
/// <c>RigWpf:SqlConnectionString</c>) ou de l'environnement Windows si non
/// configurées. Plus de valeurs hardcodées "Auguste Manet / 7401 Annecy".
/// </summary>
public sealed class SessionInfo
{
    public SessionInfo(IConfiguration config)
    {
        var configuredName = config["RigWpf:UserDisplayName"];
        UserName = string.IsNullOrWhiteSpace(configuredName)
            ? FormatLogin(Environment.UserName)
            : configuredName!.Trim();

        UserRole = config["RigWpf:UserRole"] ?? "Utilisateur RIG";
        UserInitials = ComputeInitials(UserName);

        GreffeCode = config["RigWpf:DefaultGreffe"] ?? "—";

        var cs = config["RigWpf:SqlConnectionString"] ?? "";
        (BaseServer, BaseDatabase) = ParseConnectionString(cs);
    }

    /// <summary>Nom affiché de l'utilisateur (ex "Raphaël Vilain").</summary>
    public string UserName { get; }

    /// <summary>Rôle / fonction affichée sous le nom.</summary>
    public string UserRole { get; }

    /// <summary>Initiales pour l'avatar (ex "RV").</summary>
    public string UserInitials { get; }

    /// <summary>Code greffe configuré (ex "7401").</summary>
    public string GreffeCode { get; }

    /// <summary>Nom de la base de données cible (ex "RIG_DEV").</summary>
    public string BaseDatabase { get; }

    /// <summary>Serveur SQL cible (ex @"SQL-DEV\DEV").</summary>
    public string BaseServer { get; }

    private static string FormatLogin(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return "Utilisateur";
        // "raphael.vilain" → "Raphael Vilain" ; "rvilain" → "Rvilain"
        var parts = login.Split('.', '_', '-')
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpper(p[0], CultureInfo.CurrentCulture) + p.Substring(1));
        return string.Join(" ", parts);
    }

    private static string ComputeInitials(string name)
    {
        var parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "??";
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        var last = parts[parts.Length - 1];
        return (parts[0].Substring(0, 1) + last.Substring(0, 1)).ToUpperInvariant();
    }

    private static (string Server, string Database) ParseConnectionString(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return ("(non configuré)", "(in-memory)");
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            var server = string.IsNullOrWhiteSpace(b.DataSource) ? "(serveur ?)" : b.DataSource;
            var db = string.IsNullOrWhiteSpace(b.InitialCatalog) ? "(base ?)" : b.InitialCatalog;
            return (server, db);
        }
        catch
        {
            return ("(connexion invalide)", "(base ?)");
        }
    }
}

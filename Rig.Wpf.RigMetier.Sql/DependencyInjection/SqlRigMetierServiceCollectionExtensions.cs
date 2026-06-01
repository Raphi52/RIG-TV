using System;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.RigMetier.Repositories;
using Rig.Wpf.RigMetier.Sql.Repositories;

namespace Rig.Wpf.RigMetier.Sql.DependencyInjection;

public static class SqlRigMetierServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre <see cref="SqlMandataireRepository"/> en tant que
    /// <see cref="IMandataireRepository"/>. Aucune dépendance au runtime legacy.
    /// Les autres repositories (utilisateur, greffe) restent fournis par
    /// l'enregistrement précédent (InMemory ou Legacy).
    /// </summary>
    public static IServiceCollection AddRigMetierSql(
        this IServiceCollection services,
        Action<RigMetierSqlOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var options = new RigMetierSqlOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException(
                "RigMetierSqlOptions.ConnectionString requis pour AddRigMetierSql().",
                nameof(configure));

        // Remplace toute registration IMandataireRepository déjà présente
        // (typiquement celle de AddRigMetierInMemory).
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IMandataireRepository))
                services.RemoveAt(i);
        }

        services.AddSingleton(options);
        services.AddSingleton<IMandataireRepository>(_ => new SqlMandataireRepository(options));

        // Remplace toute registration ISocieteRepository déjà présente.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISocieteRepository))
                services.RemoveAt(i);
        }
        services.AddSingleton<ISocieteRepository>(_ => new SqlSocieteRepository(options));
        return services;
    }
}

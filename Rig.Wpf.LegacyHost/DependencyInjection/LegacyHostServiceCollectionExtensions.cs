using System;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.LegacyHost.Plugins;

namespace Rig.Wpf.LegacyHost.DependencyInjection;

public static class LegacyHostServiceCollectionExtensions
{
    /// <summary>
    /// Branche les implémentations legacy : <see cref="AssemblyPluginLoader"/> écrase
    /// le <c>NullPluginLoader</c> par défaut. Le bridge IFormAccueil est ajouté en Sprint 1
    /// (il a besoin du shell pour fonctionner).
    /// </summary>
    public static IServiceCollection AddRigWpfLegacyHost(
        this IServiceCollection services,
        Action<PluginLoaderOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<PluginLoaderOptions>(_ => { });
        }

        // Replace : on supprime tout enregistrement précédent de IPluginLoader
        // (typiquement le NullPluginLoader posé par AddRigWpfCore).
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IPluginLoader))
                services.RemoveAt(i);
        }
        services.AddSingleton<IPluginLoader, AssemblyPluginLoader>();

        return services;
    }
}

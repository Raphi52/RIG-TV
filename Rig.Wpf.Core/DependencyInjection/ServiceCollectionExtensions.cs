using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.Core.Plugins;
using Rig.Wpf.Core.Session;

namespace Rig.Wpf.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre les abstractions Core avec leurs implémentations par défaut.
    /// Les hôtes (Shell WPF, LegacyHost) overrideront ces enregistrements pour
    /// brancher les adapters legacy (RigConsoleAccueil.Connexion, Assembly.LoadFrom, etc.).
    /// L'utilisation de TryAdd* garantit que des enregistrements explicites
    /// faits avant cet appel ne sont pas écrasés.
    /// </summary>
    public static IServiceCollection AddRigWpfCore(this IServiceCollection services)
    {
        services.TryAddSingleton<ISessionContext, InMemorySessionContext>();
        services.TryAddSingleton<IUtilisateurService, NullUtilisateurService>();
        services.TryAddSingleton<IGreffeService, NullGreffeService>();
        services.TryAddSingleton<IPluginLoader, NullPluginLoader>();
        services.TryAddSingleton<IFormAccueilBridge, NullFormAccueilBridge>();
        return services;
    }
}

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rig.Wpf.Core.Abstractions;
using Rig.Wpf.LegacyBridge.Bridges;
using RIG.AUTOMATE;

namespace Rig.Wpf.LegacyBridge.DependencyInjection;

public static class LegacyBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre l'adapter <see cref="WpfFormAccueilLegacyAdapter"/> et l'installe
    /// dans le singleton statique <c>RIG.AUTOMATE.FormAccueil.FrmAccueil</c>
    /// au démarrage. À appeler APRÈS <c>AddRigWpfCore()</c> et le bootstrap DI.
    /// </summary>
    public static IServiceCollection AddRigLegacyBridge(this IServiceCollection services)
    {
        services.AddSingleton<WpfFormAccueilLegacyAdapter>();
        return services;
    }

    /// <summary>
    /// Installe l'adapter dans <c>FormAccueil.FrmAccueil</c>. Doit être appelé
    /// après que le DI est build. Ne lève pas d'exception si l'adapter n'est pas
    /// résolvable (mode dégradé, on log et continue).
    /// </summary>
    public static void InstallLegacyBridge(IServiceProvider services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Rig.Wpf.LegacyBridge");
        try
        {
            var adapter = services.GetRequiredService<WpfFormAccueilLegacyAdapter>();
            FormAccueil.FrmAccueil = adapter;
            logger?.LogInformation("WpfFormAccueilLegacyAdapter installé dans FormAccueil.FrmAccueil.");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Échec installation de l'adapter legacy dans FormAccueil.FrmAccueil.");
        }
    }
}

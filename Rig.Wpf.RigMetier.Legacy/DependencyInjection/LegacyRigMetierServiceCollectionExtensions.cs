using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rig.Wpf.RigMetier.Legacy.Repositories;
using Rig.Wpf.RigMetier.Legacy.Services;
using Rig.Wpf.RigMetier.Repositories;
using Rig.Wpf.RigMetier.Services;

namespace Rig.Wpf.RigMetier.Legacy.DependencyInjection;

public static class LegacyRigMetierServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre les repositories legacy. Le <see cref="LegacyRigMetierBootstrap"/>
    /// doit être <c>Initialize</c>'d explicitement après le build du DI, avant
    /// toute requête. À utiliser à la place de <c>AddRigMetierInMemory()</c>
    /// quand l'environnement legacy (<c>C:\rig\Bin Dot Net Gac</c>, SQL Server,
    /// <c>C:\rig\BinC\RIG_BinC_Common.dll</c>) est disponible.
    /// </summary>
    public static IServiceCollection AddRigMetierLegacy(this IServiceCollection services)
    {
        services.TryAddSingleton<LegacyRigMetierBootstrap>();

        // On remplace les enregistrements précédents (typiquement AddRigMetierInMemory).
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var t = services[i].ServiceType;
            if (t == typeof(IUtilisateurRepository)
                || t == typeof(IGreffeRepository)
                || t == typeof(IMandataireRepository))
                services.RemoveAt(i);
        }

        services.AddSingleton<IUtilisateurRepository, LegacyUtilisateurRepository>();
        services.AddSingleton<IGreffeRepository, LegacyGreffeRepository>();
        services.AddSingleton<IMandataireRepository, LegacyMandataireRepository>();
        services.AddSingleton<IKbisGenerator, LegacyKbisGenerator>();
        return services;
    }
}

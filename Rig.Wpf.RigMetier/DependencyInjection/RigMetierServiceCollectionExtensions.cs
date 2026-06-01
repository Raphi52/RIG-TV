using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rig.Wpf.RigMetier.InMemory;
using Rig.Wpf.RigMetier.Repositories;

namespace Rig.Wpf.RigMetier.DependencyInjection;

public static class RigMetierServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre les repositories en version <b>in-memory</b> (utile pour les tests
    /// et pour démarrer le shell sans accès SQL Server). Les overrides "legacy"
    /// (qui appelleront le vrai RigMetier.csproj) seront ajoutés par le projet
    /// <c>Rig.Wpf.RigMetier.Legacy</c> dans une vague ultérieure.
    /// </summary>
    public static IServiceCollection AddRigMetierInMemory(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryUtilisateurRepository>();
        services.TryAddSingleton<InMemoryGreffeRepository>();
        services.TryAddSingleton<InMemoryDemandeRepository>();
        services.TryAddSingleton<InMemoryMandataireRepository>();
        services.TryAddSingleton<IUtilisateurRepository>(sp => sp.GetRequiredService<InMemoryUtilisateurRepository>());
        services.TryAddSingleton<IGreffeRepository>(sp => sp.GetRequiredService<InMemoryGreffeRepository>());
        services.TryAddSingleton<IDemandeRepository>(sp => sp.GetRequiredService<InMemoryDemandeRepository>());
        services.TryAddSingleton<IMandataireRepository>(sp => sp.GetRequiredService<InMemoryMandataireRepository>());
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rig.Wpf.Mvvm.DependencyInjection;

namespace Rig.Wpf.Processus.Mandataire;

public static class MandataireServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le Processus MANDATAIRE comme natif WPF, plus son mapper.
    /// </summary>
    public static IServiceCollection AddMandataireProcessus(this IServiceCollection services)
    {
        services.TryAddSingleton<MandataireMapper>();
        services.AddNativeProcessus<MandataireProcessusViewModel>("MANDATAIRE");
        return services;
    }
}

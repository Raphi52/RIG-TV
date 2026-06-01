using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rig.Wpf.Mvvm.Navigation;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Mvvm.DependencyInjection;

public static class MvvmServiceCollectionExtensions
{
    public static IServiceCollection AddRigWpfMvvm(this IServiceCollection services)
    {
        services.TryAddSingleton<INavigationService, NavigationService>();
        services.TryAddSingleton<INativeProcessusRegistry, NativeProcessusRegistry>();
        return services;
    }

    /// <summary>
    /// Enregistre un Processus WPF natif sous son code et le rend résolvable
    /// via le DI. Le ViewModel est en transient (chaque ouverture d'onglet = nouveau VM).
    /// </summary>
    public static IServiceCollection AddNativeProcessus<TViewModel>(
        this IServiceCollection services,
        string codeProcessus)
        where TViewModel : ProcessusViewModelBase
    {
        services.AddTransient<TViewModel>();
        services.AddSingleton<NativeProcessusRegistration>(_ =>
            new NativeProcessusRegistration(codeProcessus, typeof(TViewModel)));
        return services;
    }
}

/// <summary>
/// Marker d'enregistrement utilisé pour câbler le <see cref="NativeProcessusRegistry"/>
/// au démarrage. Le <c>NativeProcessusRegistryInitializer</c> (côté Shell) itère
/// les <c>NativeProcessusRegistration</c> pour appeler <c>Register</c> à chaud.
/// </summary>
public sealed class NativeProcessusRegistration
{
    public NativeProcessusRegistration(string codeProcessus, System.Type viewModelType)
    {
        CodeProcessus = codeProcessus;
        ViewModelType = viewModelType;
    }

    public string CodeProcessus { get; }
    public System.Type ViewModelType { get; }
}

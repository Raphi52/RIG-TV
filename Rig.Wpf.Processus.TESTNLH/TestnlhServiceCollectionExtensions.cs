using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Mvvm.DependencyInjection;

namespace Rig.Wpf.Processus.Testnlh;

public static class TestnlhServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le Processus TESTNLH comme natif WPF.
    /// </summary>
    public static IServiceCollection AddTestnlhProcessus(this IServiceCollection services)
        => services.AddNativeProcessus<TestnlhProcessusViewModel>("TESTNLH");
}

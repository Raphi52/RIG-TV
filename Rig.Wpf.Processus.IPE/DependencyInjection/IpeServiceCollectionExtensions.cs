using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Mvvm.DependencyInjection;

namespace Rig.Wpf.Processus.Ipe.DependencyInjection;

public static class IpeServiceCollectionExtensions
{
    public static IServiceCollection AddIpeProcessus(this IServiceCollection services)
        => services.AddNativeProcessus<IpeProcessusViewModel>("IPE");
}

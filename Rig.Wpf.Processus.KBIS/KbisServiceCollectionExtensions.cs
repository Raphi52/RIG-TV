using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Mvvm.DependencyInjection;
using Rig.Wpf.RigMetier.Repositories;
using Rig.Wpf.RigMetier.Services;

namespace Rig.Wpf.Processus.Kbis;

public static class KbisServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le Processus KBIS comme natif. Le ViewModel a besoin du
    /// codeGreffe au runtime — fourni par la config (RigWpf:DefaultGreffe).
    /// </summary>
    public static IServiceCollection AddKbisProcessus(this IServiceCollection services)
    {
        // Ordre critique : AddNativeProcessus enregistre un AddTransient<KbisProcessusViewModel>()
        // qui essaierait d'instancier via DI les 3 args du ctor — dont la string codeGreffe que
        // DI ne sait pas résoudre. On le met EN PREMIER puis on override avec notre factory.
        services.AddNativeProcessus<KbisProcessusViewModel>("KBIS");
        services.AddTransient(sp =>
        {
            var societes = sp.GetRequiredService<ISocieteRepository>();
            var generator = sp.GetRequiredService<IKbisGenerator>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var greffe = configuration["RigWpf:DefaultGreffe"]
                ?? throw new InvalidOperationException(
                    "RigWpf:DefaultGreffe manquant dans appsettings.json — requis par KBIS.");
            return new KbisProcessusViewModel(societes, generator, greffe);
        });
        return services;
    }
}

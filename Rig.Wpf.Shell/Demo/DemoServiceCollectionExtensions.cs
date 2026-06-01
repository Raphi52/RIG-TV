using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Mvvm.DependencyInjection;

namespace Rig.Wpf.Shell.Demo;

public static class DemoServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre tous les modules de démonstration comme Processus natifs.
    /// Chaque module est ouvrable depuis la sidebar en passant son code à
    /// <see cref="ViewModels.ShellViewModel.OpenTabCommand"/>. Aucune dépendance
    /// base : tout est mock-data, sert à matérialiser la vision UI finale.
    /// </summary>
    public static IServiceCollection AddRigWpfDemo(this IServiceCollection services)
    {
        services.AddNativeProcessus<DemoJudInstanceVm>(DemoModuleConfigs.JudInstance.Code);
        services.AddNativeProcessus<DemoJudAudienceVm>(DemoModuleConfigs.JudAudience.Code);
        services.AddNativeProcessus<DemoJudDecisionVm>(DemoModuleConfigs.JudDecision.Code);
        services.AddNativeProcessus<DemoJudPartiesVm>(DemoModuleConfigs.JudParties.Code);

        services.AddNativeProcessus<DemoRcsSocieteVm>(DemoModuleConfigs.RcsSociete.Code);
        services.AddNativeProcessus<DemoRcsDirigeantVm>(DemoModuleConfigs.RcsDirigeant.Code);
        services.AddNativeProcessus<DemoRcsActeVm>(DemoModuleConfigs.RcsActe.Code);
        services.AddNativeProcessus<DemoRcsKbisVm>(DemoModuleConfigs.RcsKbis.Code);

        services.AddNativeProcessus<DemoInsPrivilegeVm>(DemoModuleConfigs.InsPrivilege.Code);
        services.AddNativeProcessus<DemoInsNantissementVm>(DemoModuleConfigs.InsNantissement.Code);

        services.AddNativeProcessus<DemoComptaClientVm>(DemoModuleConfigs.ComptaClient.Code);
        services.AddNativeProcessus<DemoComptaFactureVm>(DemoModuleConfigs.ComptaFacture.Code);
        services.AddNativeProcessus<DemoComptaEncaissementVm>(DemoModuleConfigs.ComptaEncaissement.Code);

        services.AddNativeProcessus<DemoEdiEntrantVm>(DemoModuleConfigs.EdiEntrant.Code);
        services.AddNativeProcessus<DemoEdiSortantVm>(DemoModuleConfigs.EdiSortant.Code);
        services.AddNativeProcessus<DemoEdiBodaccVm>(DemoModuleConfigs.EdiBodacc.Code);

        services.AddNativeProcessus<DemoPcDossierVm>(DemoModuleConfigs.PcDossier.Code);
        services.AddNativeProcessus<DemoPcCreancierVm>(DemoModuleConfigs.PcCreancier.Code);

        services.AddNativeProcessus<DemoAdminUserVm>(DemoModuleConfigs.AdminUtilisateur.Code);
        services.AddNativeProcessus<DemoAdminGreffeVm>(DemoModuleConfigs.AdminGreffe.Code);
        services.AddNativeProcessus<DemoAdminParamVm>(DemoModuleConfigs.AdminParametres.Code);

        return services;
    }
}

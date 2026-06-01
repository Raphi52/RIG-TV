namespace Rig.Wpf.Shell.Demo;

// Wrappers concrets : un type par module, instanciables sans paramètre par le DI.
// Chacun pointe sur sa config DemoModuleConfigs.

public sealed class DemoJudInstanceVm : DemoProcessusViewModel { public DemoJudInstanceVm() : base(DemoModuleConfigs.JudInstance) { } }
public sealed class DemoJudAudienceVm : DemoProcessusViewModel { public DemoJudAudienceVm() : base(DemoModuleConfigs.JudAudience) { } }
public sealed class DemoJudDecisionVm : DemoProcessusViewModel { public DemoJudDecisionVm() : base(DemoModuleConfigs.JudDecision) { } }
public sealed class DemoJudPartiesVm : DemoProcessusViewModel { public DemoJudPartiesVm() : base(DemoModuleConfigs.JudParties) { } }

public sealed class DemoRcsSocieteVm : DemoProcessusViewModel { public DemoRcsSocieteVm() : base(DemoModuleConfigs.RcsSociete) { } }
public sealed class DemoRcsDirigeantVm : DemoProcessusViewModel { public DemoRcsDirigeantVm() : base(DemoModuleConfigs.RcsDirigeant) { } }
public sealed class DemoRcsActeVm : DemoProcessusViewModel { public DemoRcsActeVm() : base(DemoModuleConfigs.RcsActe) { } }
public sealed class DemoRcsKbisVm : DemoProcessusViewModel { public DemoRcsKbisVm() : base(DemoModuleConfigs.RcsKbis) { } }

public sealed class DemoInsPrivilegeVm : DemoProcessusViewModel { public DemoInsPrivilegeVm() : base(DemoModuleConfigs.InsPrivilege) { } }
public sealed class DemoInsNantissementVm : DemoProcessusViewModel { public DemoInsNantissementVm() : base(DemoModuleConfigs.InsNantissement) { } }

public sealed class DemoComptaClientVm : DemoProcessusViewModel { public DemoComptaClientVm() : base(DemoModuleConfigs.ComptaClient) { } }
public sealed class DemoComptaFactureVm : DemoProcessusViewModel { public DemoComptaFactureVm() : base(DemoModuleConfigs.ComptaFacture) { } }
public sealed class DemoComptaEncaissementVm : DemoProcessusViewModel { public DemoComptaEncaissementVm() : base(DemoModuleConfigs.ComptaEncaissement) { } }

public sealed class DemoEdiEntrantVm : DemoProcessusViewModel { public DemoEdiEntrantVm() : base(DemoModuleConfigs.EdiEntrant) { } }
public sealed class DemoEdiSortantVm : DemoProcessusViewModel { public DemoEdiSortantVm() : base(DemoModuleConfigs.EdiSortant) { } }
public sealed class DemoEdiBodaccVm : DemoProcessusViewModel { public DemoEdiBodaccVm() : base(DemoModuleConfigs.EdiBodacc) { } }

public sealed class DemoPcDossierVm : DemoProcessusViewModel { public DemoPcDossierVm() : base(DemoModuleConfigs.PcDossier) { } }
public sealed class DemoPcCreancierVm : DemoProcessusViewModel { public DemoPcCreancierVm() : base(DemoModuleConfigs.PcCreancier) { } }

public sealed class DemoAdminUserVm : DemoProcessusViewModel { public DemoAdminUserVm() : base(DemoModuleConfigs.AdminUtilisateur) { } }
public sealed class DemoAdminGreffeVm : DemoProcessusViewModel { public DemoAdminGreffeVm() : base(DemoModuleConfigs.AdminGreffe) { } }
public sealed class DemoAdminParamVm : DemoProcessusViewModel { public DemoAdminParamVm() : base(DemoModuleConfigs.AdminParametres) { } }

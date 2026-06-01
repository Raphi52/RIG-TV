using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rig.Wpf.Core.Abstractions;
using RIG.AUTOMATE;

namespace Rig.Wpf.LegacyBridge.Bridges;

/// <summary>
/// Adapter qui implémente l'interface legacy <see cref="IFormAccueil"/> en
/// déléguant les appels critiques au bridge WPF (<see cref="IFormAccueilBridge"/>).
/// Les méthodes non encore implémentées loggent un warning et retournent des
/// valeurs neutres - elles seront comblées au fur et à mesure que les plugins
/// legacy hébergés en cohabitation en ont besoin.
/// </summary>
public sealed class WpfFormAccueilLegacyAdapter : IFormAccueil
{
    private readonly IFormAccueilBridge _wpfBridge;
    private readonly ILogger _logger;

    public WpfFormAccueilLegacyAdapter(IFormAccueilBridge wpfBridge, ILogger<WpfFormAccueilLegacyAdapter>? logger = null)
    {
        _wpfBridge = wpfBridge ?? throw new ArgumentNullException(nameof(wpfBridge));
        _logger = (ILogger?)logger ?? NullLogger<WpfFormAccueilLegacyAdapter>.Instance;
    }

    // ---- Méthodes implémentées (relai vers WPF) ----

    public void ChangeTabText(string tabName, string newTabText)
        => _wpfBridge.ChangeTabText(tabName, newTabText);

    // ---- Méthodes non implémentées : no-op + log ----

    private void NotImpl([System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => _logger.LogWarning("IFormAccueil.{Member}() appelé sur l'adapter WPF — non implémenté.", member);

    public bool ExecuteProcessus(string codeProcessus, string parametres) { NotImpl(); return false; }
    public bool ExecuteProcessus(string codeProcessus, string parametres, bool detacher, eScreen screen) { NotImpl(); return false; }
    public bool ExecuteProcessus(FormAutomate ownerForm, string codeProc, string parametres) { NotImpl(); return false; }
    public bool ExecuteProcessus(FormAutomate ownerForm, string codeProc, string parametres, bool detacher, eScreen screen) { NotImpl(); return false; }
    public Process ExecuteProcessus(ParamExecuteProcessus paramExecute) { NotImpl(); return null!; }

    public Processus ExecuteProcessusFromDLL(string pathFileDLL, string formName, string codeProcessus, string parametres, string tooltipText) { NotImpl(); return null!; }
    public Processus ExecuteProcessusFromDLL(string pathFileDll, string formName, string codeProcessus, string parametres, string tooltipText, bool detacher, eScreen screen) { NotImpl(); return null!; }
    public Processus ExecuteProcessusFromDLL(FormAutomate ownerForm, string pathFileDLL, string formName, string codeProcessus, string parametres, string tooltipText) { NotImpl(); return null!; }
    public Processus ExecuteProcessusFromDLL(FormAutomate ownerForm, string pathFileDll, string formName, string codeProcessus, string parametres, string tooltipText, bool detacher, eScreen screen) { NotImpl(); return null!; }
    public Processus ExecuteProcessusFromDLL(ParamExecuteProcessus paramExecute) { NotImpl(); return null!; }

    public Process ExecuteExe(FormAutomate ownerForm, string pathExe, string parametres, bool detacher, eScreen screen, bool unique) { NotImpl(); return null!; }
    public Process ExecuteExe(FormAutomate ownerForm, string pathExe, string parametres, bool unique) { NotImpl(); return null!; }
    public Process ExecuteExe(string pathExe, string parametres, bool detacher, eScreen screen, bool unique) { NotImpl(); return null!; }
    public Process ExecuteExe(string pathExe, string parametres, bool unique) { NotImpl(); return null!; }
    public Process ExecuteExe(ParamExecuteProcessus paramExecute) { NotImpl(); return null!; }

    public bool ExecuteProcExeToTab(string codeProcessus, string parametres, out string messError)
    { NotImpl(); messError = "Non implémenté côté shell WPF."; return false; }
    public bool ExecuteProcExeToTab(string codeProcessus, string parametres, bool detacher, eScreen screen, out string messError)
    { NotImpl(); messError = "Non implémenté côté shell WPF."; return false; }
    public Process ExecuteProcExeToTab(ParamExecuteProcessus paramExecute, out string messError)
    { NotImpl(); messError = "Non implémenté côté shell WPF."; return null!; }
    public bool ExecuteExeToTab(string exeFilename, string parametres, out string messError, bool hideMenu = true, bool removeButtons = true, bool removeCaption = true, bool removeBorders = true, bool maximizeAfterPlug = false, bool allowToUnplug = false, int numEcranToUnplug = 1)
    { NotImpl(); messError = "Non implémenté côté shell WPF."; return false; }
    public Process ExecuteExeToTab(ParamExecuteExe paramExecute, out string messError)
    { NotImpl(); messError = "Non implémenté côté shell WPF."; return null!; }

    public bool AutoLocationExe(List<int> lstIdProcess, eScreen screen, FormAccueil.eModeAutolocation modeLocation) { NotImpl(); return false; }
    public bool AutoLocationWindows(List<IntPtr> handleWindows, eScreen screen, FormAccueil.eModeAutolocation modeLocation) { NotImpl(); return false; }

    public IProcDemande GetProcDemande() { NotImpl(); return null!; }
    public void ActivateTabDemande() { NotImpl(); }
    public void ClearCache() { NotImpl(); }
    public void ReprendreProcessus(int idDemande) { NotImpl(); }
    public void ReloadThemeColor(string skinTheme) { NotImpl(); }
    public void ReloadColor(string colorier1LigneSur2, string intensite) { NotImpl(); }
    public void ApplyThemeSkinToChildren() { NotImpl(); }
    public void ExecuteAction(string ClasseName, string fctName) { NotImpl(); }
    public void ArticleWiki(string idArticle) { NotImpl(); }
    public void NotifierLancementArret(string id, string description) { NotImpl(); }
    public bool CheckAutorisationExecuteProcessus() { NotImpl(); return false; }
    public bool TabPageAccueilEnAlerte() { NotImpl(); return false; }
    public void DisplayIconTabPageAccueil(bool alerteNonLue, bool alerteEnErreur, bool alerteEnWarning) { NotImpl(); }

    // ---- IProcDemande ----
    public void RechercherDemandes(string requeteOuWhere, bool setFocusOnListDemandes, int idAlerte) { NotImpl(); }
    public void ClearCriteres() { NotImpl(); }
    public void SetFocusToListDemandes() { NotImpl(); }
    [Obsolete("Use overload with idAlerte", true)]
    void IProcDemande.RechercherDemandes(string requeteOuWhere, bool setFocusOnListDemandes) { NotImpl(); }

    // ---- IProcAccueil ----
    public void SlideShowAlertes(bool demarrer) { NotImpl(); }
    public void ActivateProcAccueil() { NotImpl(); }
    public void DisableTimerTableauDeBord() { NotImpl(); }
    public void DisableTimerNews() { NotImpl(); }
}

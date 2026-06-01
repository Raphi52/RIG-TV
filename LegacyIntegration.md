# Branchement legacy — à exécuter sur dev box provisionné

Ce document décrit comment connecter `Rig.Wpf.Shell` au socle legacy (`RigAutomate`,
`RigMetier`) une fois qu'on dispose d'une machine où `C:\rig` est peuplé via
`composant_externe` (Azure Artifacts feed `rig/composantannexe`).

## Pré-requis

- `C:\rig\Bin Dot Net Gac\` peuplé (au moins ~50 DLL legacy).
- Visual Studio 2022 Pro + workload .NET Desktop + workload C++ (pour MVP build complet ;
  pour le shell WPF seul on n'a besoin que de .NET Desktop).
- Pour le bridge `IFormAccueil` (1) : la solution legacy buildée au moins une fois
  (`Source\CompilationLivraison\build.ps1 rel mvp v48`).
- Pour le bridge RigMetier (2) : SQL Server `SQL-DEV\DEV` accessible avec auth Windows.

## 1. Adapter `IFormAccueil` legacy

Le shell WPF doit s'enregistrer comme implémentation de
`RIG.AUTOMATE.IFormAccueil` pour intercepter les appels que les plugins legacy
font sur `FormAccueil.FrmAccueil` (notamment `ChangeTabText`, `ExecuteProcessus`,
`ExecuteProcessusFromDLL`). Le setter du singleton est public — on peut donc
l'overrider directement.

### Étapes

1. Ajouter un projet `Source\Wpf\Rig.Wpf.LegacyBridge\Rig.Wpf.LegacyBridge.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rig.Wpf.Core\Rig.Wpf.Core.csproj" />
    <!-- chemin RELATIF vers le legacy ; ne compile que sur dev box provisionné -->
    <ProjectReference Include="..\..\RIG\DLL\RigAutomate\RigAutomate.csproj" />
  </ItemGroup>
</Project>
```

2. Implémenter l'adapter :

```csharp
// Source\Wpf\Rig.Wpf.LegacyBridge\Bridges\WpfFormAccueilLegacyAdapter.cs
using RIG.AUTOMATE;            // <- depuis RigAutomate.csproj
using Rig.Wpf.Core.Abstractions;
using System.Diagnostics;

namespace Rig.Wpf.LegacyBridge.Bridges;

public sealed class WpfFormAccueilLegacyAdapter : IFormAccueil
{
    private readonly IFormAccueilBridge _wpfBridge;

    public WpfFormAccueilLegacyAdapter(IFormAccueilBridge wpfBridge)
        => _wpfBridge = wpfBridge;

    // Méthode CRITIQUE : utilisée par tous les plugins
    public void ChangeTabText(string tabName, string newTabText)
        => _wpfBridge.ChangeTabText(tabName, newTabText);

    // Méthodes ExecuteProcessus / ExecuteProcessusFromDLL : à implémenter
    // en délégant à IPluginLoader.LoadPlugin() côté WPF, puis en hostant via
    // LegacyPluginHostControl. Voir IFormAccueil.cs pour les 5 surcharges.
    public bool ExecuteProcessus(string codeProcessus, string parametres) { /* TODO */ return false; }
    // ... ~40 autres méthodes — la plupart peuvent être no-op + log warning
    //     pour démarrer. À combler au fur et à mesure que les plugins en ont
    //     besoin (laisser une exception NotImplementedException avec message clair
    //     pour identifier rapidement les manques).
}
```

3. Installer l'adapter au démarrage du shell :

```csharp
// Dans App.xaml.cs OnStartup, APRÈS que le DI est build :
var bridge = _host.Services.GetRequiredService<IFormAccueilBridge>();
RIG.AUTOMATE.FormAccueil.FrmAccueil = new WpfFormAccueilLegacyAdapter(bridge);
```

### Risque (#1 du plan)

Si une autre instance (RigClientAccueil.exe) tourne en parallèle, son démarrage
écrasera `FormAccueil.FrmAccueil` (statique = partagé par AppDomain, mais chaque
.exe a SON AppDomain donc il n'y a PAS de partage — chaque processus a sa propre
copie). Pas de conflit en pratique. Le mutex `Global\RigWpfShell` reste utile
pour détecter le cas d'usage et logger.

## 2. Pont RigMetier réel

Aujourd'hui `Rig.Wpf.RigMetier` ne contient que des interfaces et des impls
in-memory. Pour brancher le vrai SQL Server / les vraies entités RigMetier :

1. Créer `Source\Wpf\Rig.Wpf.RigMetier.Legacy\Rig.Wpf.RigMetier.Legacy.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Library</OutputType></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rig.Wpf.RigMetier\Rig.Wpf.RigMetier.csproj" />
    <!-- RigMetier legacy tire ~30 deps GAC ; HintPath vers C:\rig\Bin Dot Net Gac\ -->
    <ProjectReference Include="..\..\Outils\DLL\RigMetier\RigMetier.csproj" />
  </ItemGroup>
</Project>
```

2. Implémenter chaque repository en wrappant les façades RigMetier (`RIG.METIER.Utilisateur.GetUtilisateurCourant()`, `RIG.METIER.RigGreffe`, `RIG.METIER.Demande.GetDemande()`, etc.) et en mappant l'entité legacy vers le DTO via un `IDbObjectMapper`.

3. Dans `App.xaml.cs`, remplacer `services.AddRigMetierInMemory()` par
   `services.AddRigMetierLegacy()` (à coder dans `Rig.Wpf.RigMetier.Legacy`).

## 3. Smoke test bout-en-bout

Sur dev box, après build et déploiement :

```powershell
# 1. Build legacy + WPF
cd "C:\Code RIG\RigApplication\Source\CompilationLivraison"
powershell -ExecutionPolicy Bypass -File .\build.ps1 rel mvp v48
cd "..\Wpf"
dotnet build Rig.Wpf.sln -c Release

# 2. Déployer le shell WPF
copy /Y "Rig.Wpf.Shell\bin\Release\net48\Rig.Wpf.Shell.exe" "C:\rig\exe\"
copy /Y "Rig.Wpf.*\bin\Release\net48\Rig.Wpf.*.dll" "C:\rig\exe\"

# 3. Lancer
& "C:\rig\exe\Rig.Wpf.Shell.exe"

# Attendu : tab TESTNLH s'ouvre en WPF natif (UseNativeFor),
# tout autre code de processus tente un chargement legacy depuis Bin Processus.
```

## 4. Diagnostic

- Logs Serilog : `C:\rig\logs\rig-wpf-shell-{date}.log`
- Si un plugin legacy ne s'affiche pas dans son tab : check `LegacyPluginHostControl.OnLoaded`
  — il discrimine `Form` vs `UserControl`. Les autres types résolus (rares) lèvent un Label
  d'erreur visible dans le tab.
- Si `FormAccueil.FrmAccueil.ChangeTabText("X", "Y")` ne met pas à jour l'onglet :
  vérifier que `WpfFormAccueilLegacyAdapter` a bien été assigné au singleton AVANT
  le `Show()` du shell (cf. ordre dans `App.xaml.cs`).

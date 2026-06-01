# AGENTS.md - Source/Wpf

Reconstruction WPF de RIG, test-first. Cohabite avec le legacy WinForms
(`RigClientAccueil.exe`) pendant la migration multi-plugins.

## Stack technique

- **Cible** : .NET Framework 4.8 + WPF (SDK-style avec `<UseWPF>true</UseWPF>`)
- **MVVM** : CommunityToolkit.Mvvm 8.x (`ObservableObject`, `RelayCommand`)
- **DI** : Microsoft.Extensions.DependencyInjection 8.x (compat net48)
- **Logging** : Serilog (file sink vers `C:\rig\logs\`)
- **Tests** : xUnit + Moq + FluentAssertions + Xunit.SkippableFact + FlaUI.UIA3
- **Signing** : Amitel.snk partagé via `Directory.Build.props` local

Les conventions communes (TargetFramework, signing, packages de test) sont
centralisées dans [Directory.Build.props](Directory.Build.props), scope local au sous-arbre
`Source\Wpf\` pour ne pas impacter les ~1500 csproj legacy.

## Architecture

```
Rig.Wpf.Core                 abstractions (ISessionContext, IPluginLoader, IFormAccueilBridge)
  +-- Rig.Wpf.Mvvm           ViewModelBase, NavigationService, ProcessusViewModelBase, IDbObjectMapper
        +-- Rig.Wpf.LegacyHost   AssemblyPluginLoader (Assembly.LoadFrom encapsule)
        +-- Rig.Wpf.RigMetier    IUtilisateurRepository, IGreffeRepository, IDemandeRepository (+ in-memory)
              +-- Rig.Wpf.Shell        Rig.Wpf.Shell.exe (host WPF cohabitant)
                  +-- Rig.Wpf.Processus.TESTNLH (et autres ProcessusVM natifs)
```

`Rig.Wpf.TestHelpers` fournit `StubPluginFactory.CreatePluginAssembly()` pour
fabriquer des PROC_*.dll factices via `Reflection.Emit` dans les tests.

## Cohabitation legacy / natif

Le shell WPF charge un **TabViewModel** par tab :
- `LegacyPluginTabViewModel` : PROC_*.dll legacy chargé via `IPluginLoader`,
  affiché dans `WindowsFormsHost`.
- `NativeTabViewModel` : Processus WPF natif, résolu via `INativeProcessusRegistry`.

La discrimination se fait dans `ShellViewModel.OpenTab(code)` : si
`registry.IsNative(code)` retourne `true`, on instancie un VM natif via DI ;
sinon on charge le plugin legacy.

Le mapping code -> VM natif vient de :
1. `services.AddNativeProcessus<TVm>("CODE")` dans le DI (côté projet du Processus)
2. La liste `RigWpf:UseNativeFor` dans `appsettings.json` qui filtre les codes activés

Pour basculer un Processus en mode legacy (rollback), retirer son code de
`UseNativeFor` ou supprimer le projet du DI ; aucun changement de code utilisateur.

## Créer un nouveau Processus natif

1. **Scaffolder** :
   ```powershell
   cd Source\Wpf
   .\New-RigProcessus.ps1 -Code <CODE> -Libelle "<libelle>"
   ```
   Crée `Rig.Wpf.Processus.<CODE>\` + `.Tests\` + sln/csproj wiring.

2. **Wiring manuel** (le script l'imprime à la fin) :
   - `App.xaml` : ajouter le `DataTemplate` global pour le ProcessusViewModel
   - `App.xaml.cs` : appeler `services.Add<Code>Processus()` dans `ConfigureServices`
   - `appsettings.json` : ajouter le code à `RigWpf:UseNativeFor`

3. **TDD** :
   - Écrire les tests rouges dans `Rig.Wpf.Processus.<CODE>.Tests`
   - Implémenter dans `Rig.Wpf.Processus.<CODE>` jusqu'au vert
   - Lancer `dotnet test Rig.Wpf.Processus.<CODE>.Tests`

4. **UI tests** (optionnel) : ajouter un `[Trait("Category","Ui")]` dans
   `Rig.Wpf.UiTests` qui ouvre le tab via FlaUI et vérifie les bindings.

## Branchement legacy (dev box uniquement)

Voir [LegacyIntegration.md](LegacyIntegration.md). Résumé :

- **IFormAccueil** : créer `Rig.Wpf.LegacyBridge` qui référence `RigAutomate.csproj`
  legacy, implémenter un `WpfFormAccueilLegacyAdapter : RIG.AUTOMATE.IFormAccueil`,
  l'assigner au singleton `FormAccueil.FrmAccueil`. Le shell ne compile **pas**
  ce projet sur les postes sans legacy.
- **RigMetier réel** : créer `Rig.Wpf.RigMetier.Legacy` qui référence
  `RigMetier.csproj` legacy, implémenter `IUtilisateurRepository` etc. en wrappant
  les façades `RIG.METIER.*`. Remplacer `AddRigMetierInMemory()` par
  `AddRigMetierLegacy()` dans `App.xaml.cs`.

## Tests : 3 niveaux

| Catégorie | Filtre `dotnet test` | Quand |
|---|---|---|
| **Unit** | `--filter "Category!=Integration&Category!=Ui"` | À chaque commit (CI standard) |
| **Integration** | `--filter "Category=Integration"` | Nightly sur poste avec accès `SQL-DEV\DEV` |
| **UI (FlaUI)** | `--filter "Category=Ui"` | Nightly sur agent self-hosted Windows interactif |

`Xunit.SkippableFact` skippe automatiquement les tests si l'environnement
(DB, dossier `C:\rig\Bin Processus`, etc.) n'est pas accessible.

UI tests : `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
parce que FlaUI ne supporte pas plusieurs `Application.Launch` en parallèle.

## Cohabitation `Rig.Wpf.Shell.exe` / `RigClientAccueil.exe`

Mutex nommé `Global\RigWpfShell` ouvert au démarrage. **N'empêche pas** le
démarrage simultané (c'est le sens de la cohabitation), mais log un warning si
détecté pour aider au diagnostic des conflits sur les singletons partagés.
Cf. [App.xaml.cs](Rig.Wpf.Shell/App.xaml.cs).

## Ce qu'on évite

- **Pas de `RigUltControl`** miming l'API legacy `Ult.Link(IDB)`. Binding WPF natif uniquement.
- **Pas de `RigToGac.ps1`** pour les nouveaux assemblies WPF tant qu'aucun plugin
  legacy ne les référence. Le shell se déploie en plain-copy dans `C:\rig\exe\`.
- **Pas de référence directe à un singleton statique** (`RigConsoleAccueil.Connexion`,
  `Utilisateur.GetUtilisateurCourant()`). Toujours via une interface injectable
  (`ISessionContext`, `IUtilisateurRepository`).
- **Pas de classes UI dans la couche métier** (les ViewModels exposent INotifyPropertyChanged,
  jamais d'`Ult` / `Form` / `UserControl` dans les ViewModels).

## Vérification rapide

```powershell
.\verify.ps1                 # build + tests unit (rapide, pour pre-commit)
.\verify.ps1 -All            # + integration + UI tests
```

Voir [verify.ps1](verify.ps1).

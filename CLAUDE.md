# Rig Testing — CLAUDE.md

> **Rôle de ce fichier** : amorcer une session IA sur le harnais de test
> `RIG-TV` (TestViewer + SmokeRunner + Rig.Rapture.Tests) — repo à `C:\Code RIG\RIG-TV\`,
> anciennement `RigApplication-testing\Source\Wpf\` (déplacé/aplati ; structure actuelle = `RIG-TV\<projet>\` sans `Source\Wpf\`).
> Complète sans dupliquer `C:\Code RIG\CLAUDE.md` (workspace racine, règles
> transverses) et `\\ged2\rig\Projets IA\Documentation\AI-Generated\Claude.md`
> (codebase RIG legacy).
> **Langue** : réponds en français sauf demande contraire.

---

## ⚡ Quickstart

**Rig Testing** = pilote WPF (`Rig.Wpf.Kbis.TestViewer`) + driver UI Automation
(`Rig.Wpf.Kbis.SmokeRunner`) + xUnit (`Rig.Rapture.Tests`) qui orchestrent les
scénarios Rapture import / export sur RIG legacy.

**Plateforme** : .NET Framework 4.8, WPF, **AnyCPU OK ici** (pas d'interop COM,
contrairement au workspace RIG legacy qui exige x86).

**3 réflexes** :

1. Bug fonctionnel → table **Symptômes** ci-dessous
2. Tâche concrète → section **Workflows types**
3. Lancer un batch / itérer → section **ML LOOP — pipeline complet**

---

## 🚨 Règles non-négociables — spécifiques au harnais

Les règles du workspace racine (`C:\Code RIG\CLAUDE.md`) s'appliquent + ces
ajouts propres au harnais :

**Avant TOUT lancement TestViewer.exe ou SmokeRunner.exe** :

* Appeler `C:\Code RIG\ensure-fresh.ps1 -Project TestViewer` (puis `-Project SmokeRunner`)
* Compare `LastWriteTime` exe vs max source → rebuild auto si stale
* Throw si build fail → ne JAMAIS piloter un binaire vieux (incident 5h perdues le 2026-05-26)
* **Recommandé : `& "C:\Code RIG\run-debug-smoke.ps1" -Module {RAPTURE|DCADEMAT|KBIS|ALERTES} [-Parallelism N]`** — helper vetté (kaizen 2026-06-16) qui câble l'ordre sûr : **kill-first** (sinon worker leftover verrouille le .exe → MSB3027) → ensure-fresh Debug → **lance `bin\Debug\` EN DIRECT** (JAMAIS `$RigCfg.TvExe` = Release) → **ASSERT path `\Debug\`**. Renvoie `{Pid, RunStamp}` ; le caller invoque ensuite le bouton smoke (UIA) + monitore par **cutoff** (tee-log `sr-<mode>-<stamp-démarrage-worker>-<pid>` ≠ RIG_RUN_STAMP)

**Configuration Debug par défaut + 2 raccourcis bureau distincts** (depuis 2026-05-28) :

| Raccourci bureau | Cible EXE | Icône | Usage |
|---|---|---|---|
| `RIG Testing (Dev).lnk` | `bin\Debug\net48\` | gear (imageres.dll,109) | **Quotidien dev — c'est CELUI-CI que l'utilisateur lance par défaut** |
| `RIG Testing.lnk` | `bin\Release\net48\` | icône RIG | Validation prod-like ponctuelle |

* **Règle build** : pour tout fix UI à valider visuellement par l'utilisateur, build **Debug**
  (suffit dans 99 % des cas) :
  ```powershell
  & $msbuild "Rig.Wpf.Kbis.TestViewer.csproj" /p:Configuration=Debug /p:Platform=AnyCPU
  ```
* Quand l'utilisateur demande un build Release explicite (perf, démo, signature),
  builder Release séparément.
* **Vérification timestamp avant de demander une relance** :
  ```powershell
  Get-Item "C:\Code RIG\RIG-TV\Rig.Wpf.Kbis.TestViewer\bin\Debug\net48\Rig.Wpf.Kbis.TestViewer.exe" | Select LastWriteTime
  ```
  Si l'utilisateur signale « rien changé après relance » → vérifier en priorité :
  1. Le bon EXE (Debug **OU** Release selon le raccourci utilisé)
  2. Le timestamp postérieur à l'édition
  3. Le process running pointe vers l'EXE attendu (`Get-Process | Select MainModule.FileName`)
* Incident 2026-05-28 : 5 fixes invisibles parce que je buildais Debug et l'utilisateur
  lançait depuis le raccourci Release. Maintenant les 2 raccourcis existent, Debug par défaut.

**Tests / batchs** :

* Toujours passer par **Rig Testing UI** (bouton ▶ Start E2E + sélection scenario)
  ou par `SmokeRunner --drive-testviewer-rapture-process` qui pilote la UI.
* **Jamais** lancer `SmokeRunner.exe` en mode CLI scenario direct → tout nouveau
  mode SmokeRunner doit avoir : bouton GUI + commande VM + AutomationId + mode
  `--drive-testviewer-*` livrés ensemble.

**Pas de polling brut** :

* Bannir `Start-Sleep -Seconds 5`, `Thread.Sleep(3000)`, `timeout 600000`.
* Toujours poll-jusqu'à-condition, plafond 30-60 s, sleeps ≤ 500 ms.
* Justifier par écrit tout sleep > 1 s.
* **Exception settle-UI/BDD** : un sleep d'attente de stabilisation APRÈS une action, SANS condition observable à poller (drivers FlaUI/legacy, rendu WPF post-`InitializeAsync`) → n'invente pas un faux poll : annote la ligne `sleep-ok: <raison>` (l'anti-flaky l'exempte). Le poll reste obligatoire dès qu'un signal existe.

**Loop UI / screenshot loop (règle 16 master)** :

* Pendant pilotage UI : capturer dans `C:\Code RIG\RIG-TV\Audit\screenshots-loop\`
* **Lire le PNG via `Read` après chaque capture** (sinon capture = aucune valeur)
* Pas de capture pour les builds / SQL / fichiers texte

**Reporting "done"** :

* Build OK ≠ done. Verif fonctionnelle obligatoire (xUnit / smoke / screenshot).
* **Jamais** « PASS » sur une feature UI sans screenshot post-invoke confirmant.
  Un bouton peut « Invoke OK » via UIA sans rien faire (binding silent, IsEnabled trap).

**Snapshot vs commit** :

* Convention « copie dans Z », « snapshot », « pas de commit » →
  `Copy-Item` vers `Z:\test\rig-testing\`, `Z:\test\rapture-import\`,
  `Z:\test\_codeRIG-md\` — pas de `git commit`.

---

## 🤖 Discipline IA

Voir `C:\Code RIG\CLAUDE.md` § Discipline IA. Rappel des points qui foirent
en boucle sur ce harnais :

1. **Ne pas inventer un AutomationId** → vérifier dans le XAML (`AutomationProperties.AutomationId="..."`).
2. **Ne pas inventer une signature de méthode VM** → vérifier dans `MainWindowViewModel.cs`.
3. **Pas de « le test passe »** sans avoir lu le RECAP ou le JSON `last-batch-result.json`.
4. **Marquer ⚠ contre-intuitif** : ex. « Mode visible » dans UI ≠ HeadlessMode dans settings.
5. **LOOP_STATE.md obligatoire** pour toute boucle multi-itération (`RIG-TV\Audit\LOOP_STATE.md`).

---

## 🌿 Git

Voir `C:\Code RIG\CLAUDE.md` § Git — workflow propre. Rien de spécifique au harnais
sauf : **les screenshots du loop, les JSON de batch, les snapshots `Z:\test\` ne
doivent JAMAIS apparaître en stage** (sont hors repo de toute façon, mais reste
vigilant si tu fais un `git add` accidentel).

---

## 📖 Vocabulaire vital

| Terme | Sens |
|---|---|
| **TestViewer** | App WPF pilote (`Rig.Wpf.Kbis.TestViewer.exe`) — UI utilisateur |
| **SmokeRunner** | Driver console (`Rig.Wpf.Kbis.SmokeRunner.exe`) — pilote TestViewer ou tourne les scénarios standalone |
| **Scénario** | JSON dans `RaptureScenarios/*.json` décrivant un cas de test Rapture import |
| **Batch** | Run de TOUS les scénarios en parallèle (sentinel `All scenarios` dans le combo) |
| **--rapture-selfdrive** | Mode worker invisible in-process (rapide, ~2 s/scénario) |
| **--legacy-rapture-process** | Mode worker visible : ouvre RigClientAccueil, login, PROC_RETAUD, import (~30 s/scénario) |
| **HDESK** | Windows Desktop isolé (invisible) où RigClientAccueil tourne quand `HeadlessMode=true` |
| **Mode visible** (checkbox UI) | Switch entre `selfdrive` (off) et `legacy-rapture-process` (on) côté worker |
| **HeadlessMode** (setting) | Switch HDESK isolé (on) vs Desktop utilisateur (off) — propagé via env `RIG_DRIVER_HEADLESS` |
| **Mosaïque** | Tiling 2x2 / 4x4 des fenêtres RIG via `SetWindowPos` quand visible + parallel |
| **Sentinel** | Fichier `RIG-TV\Audit\last-batch-end.txt` écrit par TestViewer à la fin du batch (source de vérité fin de run, plus fiable que le log box UIA) |
| **JSON feedback** | `RIG-TV\Audit\last-batch-result.json` — résumé structuré par scénario (schema_version=3) |
| **ML LOOP** | Pipeline auto-apprenant 5 phases (cf. section dédiée) |
| **dHash** | Perceptual hash 64-bit du screenshot final batch (Phase 5) |

---

## 🏛️ Architecture du harnais

```
┌─ Rig.Wpf.Kbis.TestViewer (.exe WPF, AnyCPU, net48) ─┐
│  MainWindow.xaml ─ rail modules + tabs + boutons    │
│  ViewModels/                                         │
│    MainWindowViewModel.cs ─ RunAllRaptureScenariosAsync,
│                              JSON output, cache, dHash │
│  Services/                                            │
│    GlobalSettingsService     (parallelism, headless…) │
│    TestResultCacheService    (Phase 4 memoization)    │
│    ScreenshotDiffService     (Phase 5 dHash + Hamming)│
│    SmokeRunnerProxy          (spawn workers)          │
│    RegressionRunner          (xUnit driver)           │
│  Views/                                               │
│    GlobalSettingsDialog       (⚙ paramètres globaux)  │
│  RaptureScenarios/  *.json    (16 scénarios fixtures) │
└──────────────────────────────────────────────────────┘
                │ Process.Start (workers parallèles)
                ▼
┌─ Rig.Wpf.Kbis.SmokeRunner (.exe console) ───────────┐
│  Program.cs ─ dispatcher CLI (--drive-* / --legacy-*)│
│  LegacyDriver.cs   ─ pilote RigClientAccueil via FlaUI│
│  TestViewerDriver.cs ─ pilote TestViewer via FlaUI    │
│  Interaction.cs    ─ helpers PostMessage (mouse-free) │
└──────────────────────────────────────────────────────┘
                │ FlaUI / PostMessage
                ▼
┌─ RigClientAccueil.exe (RIG legacy, x86, WinForms) ──┐
│  Login <user> → PROC_RETAUD → audience → Importer    │
└──────────────────────────────────────────────────────┘

┌─ Rig.Rapture.Tests (xUnit, net48) ──────────────────┐
│  Tests unitaires logique pure Rapture (parser, diff)│
│  Lancé par RegressionRunner dans TestViewer          │
└──────────────────────────────────────────────────────┘
```

**Hiérarchie d'un batch** :
TestViewer (orchestrateur) → N × SmokeRunner workers parallèles
→ chaque worker mode `legacy-rapture-process` lance 1 RigClientAccueil.

**Mouse-free + focus-free** : tout pilotage RIG via `PostMessage WM_LBUTTON*` /
`WM_KEYDOWN*` ciblés sur hwnd. Pas de `Mouse.MoveTo`, pas de `SetForegroundWindow`,
pas de `Keyboard.Type` (SendInput global). Exception tolérée : ~40 ms de mini-focus
pour TestViewer WPF au setup (PostMessage WM_LBUTTON ne marche pas sur fenêtres WPF inactives).

---

## 🧪 Smoke par module (KBIS / ALERTES / DCADEMAT / RAPTURE)

⚠️ Le harnais est **RAPTURE-centré** : seul RAPTURE a des scénarios JSON, un batch
parallèle, le sentinel `last-batch-end.txt`, le JSON `last-batch-result.json` et le ML
LOOP. Les 3 autres modules ont leurs scénarios définis **en dur dans le code**
(`LegacySmokeCatalog.cs`), pilotés par bouton + CLI, mais **n'écrivent NI sentinel NI
JSON** : leur verdict = exit code worker (0 = tous passent / 1 = ≥1 FAIL) + lignes
`✓`/`✗` parsées du stdout redirigé.

**Sélection du module** : env `RIG_TV_MODULE` avant lancement (pré-sélection ; évite le
clic UIA sur le rail dont les ToggleButton n'ont pas d'AutomationId), ou `SelectModule(...)`
/ `Invoke-RailModule` (clic souris). Modules `MANDATAIRE` / `IPE` = prévus mais
`IsAvailable:false`. Réf : `MainWindowViewModel.cs:215-234` + `:422-426` (props `IsXxxModule`).

| Module | Bouton (AutomationId) | Commande VM | Modes CLI SmokeRunner | Scénarios | Drive CLI ? |
|---|---|---|---|---|---|
| **KBIS** | `BtnKbisRunSmokeLegacy` | `RunLegacyCommand` → `RunKbisPlanAsync` | `--legacy-kbis-vk`, `--legacy-kbis-xex` | **2** (vk = PROC_VK, xex = PROC_XEX) | ✅ `--drive-testviewer-legacy-kbis`, `--drive-testviewer-kbis-stress` |
| **ALERTES** | `BtnAlertesRunSmokeLegacy` | `RunAlertesCommand` → `RunAlertesPlanAsync` | `--legacy-alertes-{int-form,int-dca,rec-form,rec-dca}` | **4** | ❌ → piloter via `Invoke-TileButton` |
| **DCADEMAT** | `BtnDcadematRunSmokeLegacy` | `RunDcadematCommand` → `RunDcadematPlanAsync` | `--legacy-dcademat-{dca,form}-{validation,reclamation,refus,interrompue}` | **8** (⚠️ v1 : la plupart s'arrêtent à « Ouvrir demande » ; seuls `dca-validation` et `dca-reclamation` ont le step terminal « Action ») | ❌ → `Invoke-TileButton` |
| **RAPTURE** | `BtnRaptureProcessE2E` | `RunAllRaptureScenariosAsync` | `--legacy-rapture-process`, `--rapture-selfdrive`, `--drive-testviewer-rapture-process` | **16** (JSON dans `RaptureScenarios/`) | ✅ |

**Où vivent les scénarios non-RAPTURE** : en dur dans
`Rig.Wpf.Kbis.TestViewer\ViewModels\Smoke\LegacySmokeCatalog.cs` (tags `kbis`/`alertes`/`dcademat`),
adapters par module sous `ViewModels\Smoke\<Module>\` (`KbisLegacyScenarioAdapter`,
`AlertesLegacyScenarioAdapter`, `DcadematLegacyScenarioAdapter`). **Pas** de dossier
`*Scenarios/` (contrairement à `RaptureScenarios/`).

**Verdict / sortie par module** :
* **RAPTURE** → `RIG-TV\Audit\last-batch-result.json` (schema 3, `regressions`/`fixed_now`) +
  `RIG-TV\Audit\last-batch-end.txt`. Écrits **uniquement** par `RunAllRaptureScenariosAsync`
  (`MainWindowViewModel.cs`). C'est le seul module exploitable par `ml-loop.ps1`.
* **KBIS / ALERTES / DCADEMAT** → **ni JSON ni sentinel**. Verdict = exit code SmokeRunner
  + `✓`/`✗` stdout. Self-snaps PNG sous `%LOCALAPPDATA%\rig-wpf-testviewer\self-snaps\<RIG_RUN_STAMP>\<instanceId>\`
  (instanceId ex. `kbis-vk`, `alertes-int-form`, `dcademat-dca-validation`).

**Pilotage PS** : `kbis-harness.ps1` est générique → `Launch-TV -Module <X>`,
`Invoke-TileButton <PID> <AidRegex>`, `Run-Scenario -Arg <flag>`, `Wait-MarkerCountAbove`.
Fonctions RAPTURE-spécifiques : `Navigate-RaptureSmokeImport`, `Invoke-RaptureTab`.
**Pas** de `Run-KbisSuite`/`Run-AlertesSuite`/`Run-DcadematSuite` dédiées (à composer).

**Asymétrie → ce qui manque pour une boucle unifiée des 3 modules** :
1. Pas de verdict JSON/sentinel pour KBIS/ALERTES/DCADEMAT (seul RAPTURE) → pas de
   `regressions`/`fixed_now` automatiques.
2. Pas de mode `--drive-testviewer-legacy-{alertes,dcademat}` (seul KBIS a son drive CLI).
3. Pas de run unifié enchaînant les 3 modules (ni bouton ni script) → 3 lancements séparés.
4. `ml-loop.ps1` = RAPTURE-only (lit `last-batch-result.json`).
5. DCADEMAT v1 partiel (6/8 kinds s'arrêtent à « Ouvrir demande »).

> Source : cartographie harnais via sous-agent Explore + vérif grep des AutomationIds,
> flags CLI et writers JSON. Le code (`LegacyScenarioCatalog.cs`, `Program.cs`, `MainWindow.xaml`) fait foi.

---

## 🧱 Engine de scénarios déclaratif (SmokeRunner — commit `ecdf6b0`)

⚠️ **Change la façon d'ajouter un scénario KBIS/ALERTES/DCADEMAT** (≠ RAPTURE, qui reste en JSON
dans `RaptureScenarios/`). Les 14 scénarios legacy sont désormais définis via un **builder fluent**,
plus de méthode `RunLegacyXxx` ni de flag CLI à câbler à la main.

**Où** : `Rig.Wpf.Kbis.SmokeRunner\Scenarios\` — `ScenarioModel.cs` (builder + executor),
`LegacyScenarioCatalog.cs` (les 14 défs), `Program.ScenarioEngine.cs` (pont).

**Ajouter un scénario MAINTENANT** = **un seul endroit** : ajouter une `ScenarioBuilder.New(id, …).…Build()`
dans `LegacyScenarioCatalog.All()`. Le flag `--legacy-<id>` est **auto-généré** (dispatch
`foreach scen in LegacyScenarioCatalog.All()` dans `Program.cs`). Pas de JSON, pas d'adapter.

**Verbes du builder** (`ScenarioModel.cs:101-193`) :

| Verbe | Rôle |
|---|---|
| `.Step(label, d => …)` | Geste de pilotage via `LegacyDriver` ; throw = Fail |
| `.StepCtx(label, ctx => …)` | Partage d'état entre steps (`ctx.Set/Get<T>`) |
| `.GateStep(label, flag, d => …)` | Enregistre un flag booléen (Pass=true) pour conditionner la suite |
| `.StepIf(flag, label, d => …, skipLabel)` | Step exécuté seulement si flag vrai, sinon Skip |
| `.ActionableStepIf(flag, label, ctx => bool, skipLabel)` | Gaté + 3 états : true=Pass / false=Skip / throw=Fail |
| `.Expect(label, ctx => bool)` | Assertion sémantique (faux → `ScenarioAssertionException`) |

`Build()` **valide à la construction** que tout `flag` requis est produit par un `GateStep` antérieur
(sinon `InvalidOperationException` immédiate — pas de skip silencieux sur faute de frappe). L'executor
est testable sans RIG (`Rig.Rapture.Tests\ScenarioEngineTests.cs`).

---

## 🗄️ Sélecteur de base de données (smoke — commit `12ede91`)

⚠️ **SÉCURITÉ — aucun garde-fou anti-prod.** Choisir la base sur laquelle tournent les smoke tests.

| Élément | Valeur / source |
|---|---|
| **Défaut serveur** | `SQL-DEV\DEV` (`GlobalSettingsService.cs:71`) |
| **Défaut base** | `RIG_DEV` (`GlobalSettingsService.cs:79`) |
| **UI** | ⚙ Paramètres globaux → section « Base de données (smoke tests) » : `CmbDatabaseServer` (combo éditable), `TxtDatabaseName`, bouton scan `BtnScanServers` |
| **Persistance** | `%LOCALAPPDATA%\rig-wpf-kbis\global-settings.json` (`databaseServer` / `databaseName`) |
| **Propagation worker** | env **`RIG_LEGACY_CONNECTION`** (connection string complète), héritée par les workers SmokeRunner |
| **Override CLI (App)** | `--server=…`, `--db=…`, `--connection=…` (précédence : `--connection` > `--server`/`--db` > JSON > défaut) |

⚠️ Le champ est **libre** + le scan réseau (UDP 1434) remonte **toute** instance SQL visible → **rien
n'empêche de pointer une prod** (`RIGBD5`/`SQL-PROD`). Les modes `--apply`, `--reset-smoke-db`,
`CleanupCreatedAudience` (`DELETE`), `RestoreAfterApply` (`UPDATE`) écrivent **réellement** sur la base
choisie. **Réflexe : avant tout run avec écriture, vérifier `RIG_LEGACY_CONNECTION` / la base affichée.**
(Rappel règle racine : PROD `RIGBD5` = interdite par défaut, SELECT-only sur accord explicite par tour.)

---

## 🔁 Boucle CLI autonome `--loop` + skill `/rig-loop` (commit `ecdf6b0`/`ca90689`)

⚠️ **Nouveau, distinct de `ml-loop.ps1`** : `SmokeRunner --loop` est une boucle **sans UI TestViewer**
(spawn direct de workers `--rapture-selfdrive`), pilotée par la skill Claude **`/rig-loop`** (6 phases).
`ml-loop.ps1` reste l'orchestrateur « haute couche » du **bouton 🤖 ML Loop** (UI).

```
SmokeRunner --loop {run|build|bench} [options]
  run    [--scenarios all|<id,id>] [--apply]   # batch //, retry-once, timeout 180s/worker, env RIG_SMOKE_PARALLELISM (4)
  build                                         # build PROC_RETAUD Release + deploy (safeBuild.ps1)
  bench  {acquire|release|status}               # verrou coopératif fichier .bench-lock, TTL 2h
```

* Émet `result.json` sous `%LOCALAPPDATA%\rig-wpf-testviewer\loop\<runId>\`.
* ⚠️ **`--loop run` lit les scénarios dans `bin\Release\net48\RaptureScenarios\`** → un build **Debug
  seul = 0 scénario** (`LoopRun.cs:232`). C'est l'exception à la règle « Debug par défaut » : pour `--loop`,
  builder **Release**.
* Pour un agent CLI autonome → passer par la skill `/rig-loop` (chemin recommandé). Détail : `.claude/skills/rig-loop/SKILL.md`.

---

## ⚠️ Faux amis (TestViewer-specific)

| Tu crois… | Réalité |
|---|---|
| `safeBuild.ps1` build le harnais | ❌ `safeBuild.ps1` est pour la solution RIG principale. Pour le harnais : `msbuild` direct sur le `.csproj` ou `ensure-fresh.ps1 -Project TestViewer` |
| TestViewer est dans `Rig.Wpf.sln` | ❌ Pas inclus par défaut. Faut l'ajouter manuellement (Add → Existing Project) dans VS2022 |
| « Mode visible » UI checkbox = afficher RIG | ⚠ Selon `HeadlessMode` setting : si headless=true, visible mode = RIG sur HDESK isolé (toujours invisible à l'utilisateur). Sinon RIG sur user desktop |
| Le log box TextBox montre la fin du batch en temps réel | ⚠ Oui en streaming, mais **pas source de vérité** pour fin de batch — utiliser fichier sentinel `RIG-TV\Audit\last-batch-end.txt` (race-free) |
| `last-batch-end.txt` et `last-batch-result.json` = pareil | ❌ Deux fichiers complémentaires. `.txt` = sentinel timestamp pour driver wait. `.json` = résumé structuré pour orchestrateur ML |
| FlaUI `_window.FindAllDescendants` ne franchit jamais les processus | ⚠ En théorie scoped au PID. En pratique avec UIA cache + 16 RIG en //, occasionnellement contaminé. Filtrer `b.Properties.ProcessId.ValueOrDefault == _app.ProcessId` par sécurité |
| `Process.WaitForExitAsync()` dispo | ❌ Pas en .NET Fx 4.8. Utiliser `await Task.Run(() => proc.WaitForExit())` |
| `[Console]::OutputEncoding` est UTF-8 par défaut en PS 5.1 | ❌ C'est CP850 (OEM Windows FR). Forcer `New-Object System.Text.UTF8Encoding($false)` au top de tout script PS appelé depuis C# avec stdout redirigé |
| TestViewer `bin\Debug\net48\` cwd | ⚠ La WorkingDirectory au lancement = dossier du `.exe`. Donc `RaptureScenarios/` doit y être copié (post-build event) |
| « Build OK = scénario valide » | ❌ Le scénario JSON peut compiler sans erreur ET produire un FAIL runtime. Toujours valider via batch + lecture `last-batch-result.json` |
| Cache hit = test pas exécuté = pas testé | ⚠ Vrai mais voulu : Phase 4 = memoization. Si tu doutes → décoche « Cache de résultats E2E » dans Settings ou clique « Vider le cache » |
| MessageBox dans CC dialog affiche les emojis/box-drawing | ❌ Police MS Shell Dlg n'a pas les glyphes U+2500-257F ni emojis. Sanitize avant MessageBox.Show |
| 5 instances PowerShell partagent l'état de `Start-Job` | ❌ Chaque session PS = ses propres jobs. Pour spawner un process qui survit à la session → `Start-Process -RedirectStandardOutput <file>` |
| Mode B `exit 0` / « passed » = pas de mail envoyé | ❌ Le driver FlaUI ne voit PAS les crash-logs internes de RIG. RIG **maile chaque crash** (`AmiLog`→`eLog9Crash`→`RigLog.SendMail`→SmtpClient) pendant un import même si le driver renvoie `exit 0`. **Expéditeur observé : `automate@amitel.fr`** (pas `rig@amitel.fr`), sujet `Crash <PROC> / <Classe> / <Méthode> sur <MACHINE>/<base>`. Trigger connu : import Rapture `RaptureImportOrchestrator.TryAudienceLabel`→`GetCHAMBRE("Mise ")`. **→ La vraie source de vérité d'un échec passe souvent par le mail** : cf. section **📧 Erreurs par mail (Outlook)** + grep `C:\RIG\Data\Log\9995\RigClientAccueil-<user>-<stamp>.txt` (`TestValidite`/`eLog9Crash`/`SendMail`). (Réf : mémoire `project_rigtv_modeb_mail_not_safe`) |
| Le sélecteur de BDD pointe forcément une base de test | ❌ Champ **libre, AUCUN garde-fou anti-prod** : défaut `SQL-DEV\DEV` / `RIG_DEV`, mais le scan réseau (UDP 1434) remonte toute instance visible et on peut taper n'importe quel serveur. Les modes `--apply` / `--reset-smoke-db` / cleanups SQL (`DELETE`/`UPDATE`) tournent contre la base **choisie**. ⚠️ Toujours vérifier serveur+base avant un run avec écriture — cf. section **🗄️ Sélecteur de base de données** |
| Les scénarios KBIS/ALERTES/DCADEMAT vivent dans `LegacySmokeCatalog.cs` (TestViewer) | ⚠ L'**exécution** des 14 scénarios legacy se définit désormais côté **SmokeRunner** dans `Scenarios\LegacyScenarioCatalog.cs` (engine déclaratif fluent, commit `ecdf6b0`). C'est ce catalogue qui fait foi pour ajouter/modifier un scénario — cf. section **🧱 Engine de scénarios déclaratif** |

---

## 🩺 Symptômes → causes

| Symptôme | Cause probable | Fix |
|---|---|---|
| Batch coupe à ~30-50 s alors que workers tournent encore | `WaitForSmokeCompletion` no-change bail prématuré | Vérifier que le mode batch utilise sentinel fichier EXCLUSIVEMENT (cf. fix S1.1) |
| `Bouton Importer Rapture introuvable` en // | UIA cache transitoire après navigation | Retry pattern 5 s avec filtre `Properties.ProcessId == _app.ProcessId` |
| « Valeur 'Mise ' inexistante dans CHAMBRE » + mails spam | UIA `cell.Name` tronqué selon column width | Whitelist CHAMBRE dans `LegacyDriver.SelectAudience*` (REF, AU, CX, MD, PC, TC, CC, JI, JE, FT) |
| Vol focus / souris pendant batch | `HeadlessMode=false` → RIG sur user desktop | Toggler `HeadlessMode=true` dans Settings (⚙) — RIG passe sur HDESK isolé |
| OpenFileDialog `Importer Rapture` ne reçoit pas le path | ComboBoxEx `AutomationId="1148"` est wrapper | SetValue sur Edit inner-child du combo + Enter sur hwnd dialog |
| Dialog MessageBox affiche `♦♦♦♦` | PS stdout en CP850 décodé UTF-8 par C# | Forcer `[Console]::OutputEncoding = UTF8Encoding($false)` au top du script PS |
| `Process.Start null` ou worker n'apparaît pas | TestViewer cwd ≠ dossier de SmokeRunner.exe | Vérifier `WorkingDirectory = Path.GetDirectoryName(smokeExe)` |
| TestViewer crash au boot : « ML Loop service KO » | `ScreenshotDiffService` ou `TestResultCacheService` jette à l'init | Lire log `%LOCALAPPDATA%\rig-wpf-testviewer\testviewer-yyyyMMdd.log` |
| Cache hit alors qu'on a modifié le driver | Cache key = hash(SmokeRunner.exe + scenario JSON). Pas rebuild ? | `ensure-fresh.ps1 -Project SmokeRunner` avant le batch |
| dHash distance énorme entre deux batchs identiques | Capture plein écran = wallpaper / notif / curseur souris bougent | À traiter par ROI sur hwnd TestViewer (Phase 5 amélioration) |
| Le `Run all` ne fait rien | Pas en module RAPTURE, ou aucun scénario chargé | Switcher module → KBIS / RAPTURE, vérifier `RaptureScenarios.Count > 0` |
| FlaUI `COMException 0x8001010E` (RPC_E_WRONG_THREAD) | UIA appelé depuis mauvais thread MTA/STA | `await Task.Run(...)` ou recourir aux retries (déjà géré sur 10 attempts dans WaitForSmokeCompletion) |

---

## 📁 Carte des fichiers / artefacts

| Path | Contenu | Lifecycle |
|---|---|---|
| `RIG-TV\Rig.Wpf.Kbis.TestViewer\bin\Debug\net48\` | Binaire TestViewer | Build output, gitignored |
| `RIG-TV\Rig.Wpf.Kbis.TestViewer\bin\Debug\net48\RaptureScenarios\` | Copie des scénarios JSON post-build | Auto-copié, ne pas éditer ici |
| `RIG-TV\Rig.Wpf.Kbis.TestViewer\RaptureScenarios\*.json` | **Source** des 16 scénarios + sentinel `_all-scenarios.json` | Versionné |
| `RIG-TV\Rig.Wpf.Kbis.SmokeRunner\bin\Debug\net48\` | Binaire SmokeRunner | Build output |
| `%LOCALAPPDATA%\rig-wpf-kbis\global-settings.json` | Settings utilisateur (parallelism, headless, cache) | Persisté, écrasé par UI |
| `%LOCALAPPDATA%\rig-wpf-kbis\test-cache.json` | Cache ML Phase 4 | Persisté, vidable via UI |
| `%LOCALAPPDATA%\rig-wpf-testviewer\testviewer-yyyyMMdd.log` | Log applicatif TestViewer | Append day-by-day |
| `C:\Code RIG\RIG-TV\Audit\last-batch-end.txt` | Sentinel timestamp fin de batch | Écrasé à chaque batch |
| `C:\Code RIG\RIG-TV\Audit\last-batch-result.json` | JSON feedback structuré (Phase 1) | Écrasé à chaque batch |
| `C:\Code RIG\RIG-TV\Audit\ml-loop-history\iter-*.json` | Snapshots historiques des JSON | Append, jamais effacé |
| `C:\Code RIG\RIG-TV\Audit\ml-loop-prompt.md` | Prompt master généré (Phase 3) | Écrasé à chaque run ml-loop.ps1 |
| `C:\Code RIG\RIG-TV\Audit\LOOP_STATE.md` | État live boucle d'itération | Écrasé par ml-loop.ps1 |
| `C:\Code RIG\RIG-TV\Audit\screenshots-loop\` | Screenshots règle 16 + batch-recap-*.png | Append manuel + auto-batch-end |
| `C:\Code RIG\RIG-TV\Audit\ML_LOOP_PHASE{3,4,5}.md` | Docs implémentation phases | Permanent |
| `C:\Code RIG\Tools\ml-loop.ps1` | Orchestrateur PowerShell (bouton ML Loop UI) | Versionné dans `Z:\test\rig-testing\Tools\` |
| `C:\Code RIG\ensure-fresh.ps1` | Rebuild-if-stale | Versionné |
| `%LOCALAPPDATA%\rig-wpf-testviewer\loop\<runId>\result.json` | Résultat de `SmokeRunner --loop run` (boucle CLI autonome) | Écrasé par run |
| `RIG-TV\docs\RIG-UI-MAP.md` | Carte des interactions RIG **prouvées live** (5 flux + nav) | Versionné |
| `RIG-TV\docs\RIG-SCREENS-BACKLOG.md` | ~150 PROC_* inventoriés **non prouvés** (backlog) | Versionné |
| `RIG-TV\docs\rig-menu-tree.txt` | Arbre menu RIG (6 rails/56 sous-menus/603 procs) régénéré par `--dump-menu` | Régénérable |
| `%USERPROFILE%\Desktop\JsonRapture\screenshots\<PID>\` | Screenshots auto SmokeRunner per worker | Append, à nettoyer |
| `Z:\test\rig-testing\` | Snapshot fichiers modifiés (convention « copie dans Z ») | Mirror manuel |

> **Racine d'audit configurable** (`SmokeRunner\AuditPaths.cs`) : tous les artefacts ci-dessus sous
> `Audit\` se résolvent via env **`RIG_AUDIT_ROOT`** si défini, sinon défaut **`C:\Code RIG\RIG-TV\Audit`**.
> Autres overrides (`TestingPaths.cs`) : `RIG_KBIS_SMOKERUNNER_EXE`, `RIG_KBIS_REGRESSION_PROJECT_DIR`,
> `RIG_RAPTURE_REGRESSION_PROJECT_DIR`. La connexion BDD : env `RIG_LEGACY_CONNECTION` (cf. § sélecteur BDD).

---

## 🔧 Workflows types

### Implémenter une feature côté TestViewer (UI / VM)

1. Code (XAML + VM + Service si besoin)
2. `ensure-fresh.ps1 -Project TestViewer` → rebuild auto
3. Lancer TV → click via UIA (vérifier `AutomationId` existe) → screenshot post-invoke
4. **Lire le screenshot** via `Read` (sinon = aucune valeur)
5. Si comportement OK → snapshot vers `Z:\test\rig-testing\`
6. Mettre à jour `RIG-TV\Audit\LOOP_STATE.md` (Goal / Tried / Next)

### Ajouter un nouveau scénario Rapture

1. Créer le JSON dans `RIG-TV\Rig.Wpf.Kbis.TestViewer\RaptureScenarios\<id>.json`
2. Ajouter l'entrée dans `manifest.json` (si présent) ou laisser auto-discovery
3. `ensure-fresh.ps1 -Project TestViewer` (le post-build copie les JSON dans `bin\`)
4. Lancer batch « All scenarios » via Rig Testing → vérifier scenario apparaît
5. Run individuel pour debug si FAIL initial
6. Snapshot `Z:\test\rapture-import\<id>.json`

### Itérer sur un bug FAIL en boucle (workflow ML LOOP)

1. Lire `RIG-TV\Audit\LOOP_STATE.md` (Goal / Hypothesis / Tried)
2. Lancer batch courant via Rig Testing UI
3. Lire `RIG-TV\Audit\last-batch-result.json` → identifier les FAIL
4. Cliquer 🤖 ML Loop dans TV → génère `RIG-TV\Audit\ml-loop-prompt.md`
5. Lire le prompt → investiguer (log RIG, screenshots, JSON scenario)
6. Fix le code → `ensure-fresh.ps1`
7. Re-lancer batch → comparer `regressions` / `fixed_now` dans le JSON
8. Update `LOOP_STATE.md` après chaque tentative
9. Stop quand 100 % PASS ou 3 fixes consécutifs échouent (re-questionner archi)

### Lancer un batch en CLI (sans UI)

```powershell
& "C:\Code RIG\RIG-TV\Rig.Wpf.Kbis.SmokeRunner\bin\Debug\net48\Rig.Wpf.Kbis.SmokeRunner.exe" `
  --drive-testviewer-rapture-process `
  --json "All scenarios" `
  [--visible]   # optionnel : RIG s'ouvre via legacy-rapture-process
```

Cela lance SmokeRunner qui démarre TestViewer, clique « Start E2E », attend
fin via sentinel, capture screenshot.

### Modes de visibilité RIG

| Mode | `HeadlessMode` | `RaptureSmokeVisibleMode` | Effet | Quand |
|---|:-:|:-:|---|---|
| A (déprécié) | false | true | RIG live sur ton desktop, vol focus | Démo unique |
| **B** (défaut) | **true** | true | RIG sur HDESK isolé + self-snap PNG | **Batch normal** |
| C | false | true | RIG sur Desktop 2 (Sysinternals `Desktops.exe`) | Peek live Win+Alt+2 |
| Selfdrive | n/a | false | In-process, pas de RIG GUI | Tests rapides logique pure |

---

## 🤖 ML LOOP — pipeline complet (Phases 1-5)

Cycle « code → batch → JSON → diff → fix → reboucle » :

```
[code change]
    │
    ▼
[ensure-fresh.ps1] ── rebuild auto si stale
    │
    ▼
[Run all / ▶ Start E2E]  ─── 16 workers // (parallelism configurable)
    │
    ▼  pour CHAQUE scénario :
[Cache lookup]  ── Phase 4 : hash(smokeExe + scenarioJson) → PASS connu = skip
    │
    ├─ hit PASS  → emit cached, durée ~ms
    └─ miss/FAIL → spawn worker SmokeRunner --legacy-rapture-process ou --rapture-selfdrive
                   │
                   └─ worker → RIG → assertions → exit 0/1
    │
    ▼
[Recap]
    │
    ▼
[JSON output]  ── Phase 1 : last-batch-result.json schema_version=3
    │  + Phase 2 : diff vs N-1 (regressions, fixed_now)
    │  + Phase 4 : cache_hits, cache_misses, code_hash
    │  + Phase 5 : screenshot_dhash, screenshot_diff_vs_previous
    │
    ▼
[🤖 ML Loop button] OU [Tools\ml-loop.ps1]   ── Phase 3 : orchestrateur
    │
    ├─ 100 % PASS → exit 0, update LOOP_STATE.md
    └─ FAIL count > 0 → écrit ml-loop-prompt.md (table fails, screenshots, tendance)
                       → optionnel -AutoSpawn : claude -p <prompt> en //
                       → optionnel -Watch -AutoRunBatch : re-lance batch tout seul
```

**Fichiers clés du pipeline** :

| Phase | Fichier | Rôle |
|---|---|---|
| 1 | `RIG-TV\Audit\last-batch-result.json` | JSON résumé batch (16 entries + meta) |
| 2 | (inclus dans le même JSON) | `regressions` / `fixed_now` calculés en lisant l'ancien JSON |
| 3 | `Tools\ml-loop.ps1` | Orchestrateur PowerShell |
| 3 | `RIG-TV\Audit\ml-loop-prompt.md` | Prompt structuré master agent |
| 3 | `RIG-TV\Audit\LOOP_STATE.md` | État courant de la boucle |
| 3 | `RIG-TV\Audit\ml-loop-history\iter-*.json` | Snapshots chronologiques |
| 4 | `%LOCALAPPDATA%\rig-wpf-kbis\test-cache.json` | Cache memoization |
| 5 | `RIG-TV\Audit\screenshots-loop\batch-recap-*.png` | Screenshot fin de batch (pour dHash) |

**Activer le cache** : Settings ⚙ → cocher « 🗄 Cache de résultats E2E ». Désactivé
par défaut (peut masquer une régression si invalidation rate un fichier influent).

**Boucle entièrement autonome** :

```powershell
& "C:\Code RIG\Tools\ml-loop.ps1" -AutoSpawn -Watch -AutoRunBatch -MaxIterations 5
```

---

## 🧪 Tests xUnit (Rig.Rapture.Tests)

Le projet `RIG-TV\Rig.Rapture.Tests\` couvre la **logique pure** Rapture
(parser, diff, validation). Exécuté par `RegressionRunner` dans TestViewer
ou directement via `dotnet test`.

**Workflow TDD** :

1. Écrire le test xUnit qui reproduit le bug
2. Voir le test rouge (`dotnet test --filter "FullyQualifiedName~<NomTest>"`)
3. Fix dans le code
4. Voir le test vert
5. Lancer la suite complète pour détecter les régressions

**Couverture connue manquante** :

* `TestResultCacheService` — pas de tests xUnit dédiés (testé par reflection PS — hack)
* `ScreenshotDiffService` — pas de tests xUnit (validé via PNG existantes en PowerShell)

→ à combler (cf. plan 20/20 S1.4).

---

## ⛔ Limites connues du harnais

* **Cache trop coarse-grained** : hash = SmokeRunner.exe entier. Un rebuild banal
  (commentaire dans LegacyDriver) invalide tout. À granulariser sur les fichiers
  source qui touchent vraiment le pipeline.
* **dHash trop noisy** : capture plein écran inclut wallpaper / taskbar / curseur.
  À restreindre au hwnd TestViewer.
* **`-AutoSpawn` jamais validé end-to-end** : feature codée, `claude -p` jamais
  effectivement lancé pour fixer un bug réel et reboucler.
* **Pas de CLI batch mode TestViewer** : pour vraiment fermer la boucle sans humain,
  il faudrait `TestViewer.exe --run-all-rapture --no-ui --output X.json`.
* **xUnit non câblé sur services Phase 4/5** : tests par reflection PowerShell uniquement.
* **Mode visible parallel 16 instances** : RAM lourde (~8 Go). Mode 4 parallel
  recommandé pour usage quotidien.
* **TestViewer pas dans `Rig.Wpf.sln`** : ajout manuel requis (Add Existing Project).

Cf. plan complet de stabilisation : `RIG-TV\Audit\ML_LOOP_PHASE3.md` + `ML_LOOP_PHASE4.md`
+ `ML_LOOP_PHASE5.md` + roadmap 20/20.

---

## 🧰 Commandes utilisables (cheat-sheet)

> Tous chemins relatifs au repo `C:\Code RIG\RIG-TV\`. **Toujours `ensure-fresh` AVANT de piloter un exe.**

**Scripts helper (PowerShell)** :

| Commande | Rôle |
|---|---|
| `& "C:\Code RIG\ensure-fresh.ps1" -Project TestViewer` (puis `-Project SmokeRunner`) | Rebuild-if-stale, throw si build fail |
| `& "C:\Code RIG\run-debug-smoke.ps1" -Module {RAPTURE\|KBIS\|ALERTES\|DCADEMAT} [-Parallelism N]` | **Lancement vetté** : kill-first → fresh Debug → lance `bin\Debug\` → assert path. Renvoie `{Pid, RunStamp}` |
| `Get-Item "…\bin\Debug\net48\Rig.Wpf.Kbis.TestViewer.exe" \| Select LastWriteTime` | Vérifier la fraîcheur de l'exe avant relance |
| `& "C:\Code RIG\Tools\ml-loop.ps1" -AutoSpawn -Watch -AutoRunBatch -MaxIterations 5` | Orchestrateur ML LOOP (RAPTURE, via bouton/JSON UI) |

**SmokeRunner — boucle CLI autonome** (`SmokeRunner.exe`, depuis `bin\Release\net48\` pour `--loop run`) :

| Commande | Rôle |
|---|---|
| `--loop run [--scenarios all\|<id,id>] [--apply]` | Batch // sans UI ; `--apply` = écriture réelle BDD ⚠️ |
| `--loop build` | Build PROC_RETAUD Release + deploy |
| `--loop bench {acquire\|release\|status}` | Verrou de banc (TTL 2h) |
| `--dump-menu` | Scan READ-ONLY du menu RIG → `Audit\rig-menu-tree.txt` (mail-safe) |

**SmokeRunner — modes de pilotage** (dispatcher `Program.cs:64-154`) :

| Flag | Rôle |
|---|---|
| `--drive-testviewer-rapture-process` | Pilote « ▶ Start E2E » Smoke Import via FlaUI (le mode batch standard) |
| `--drive-testviewer-rapture-export` | Idem tab Smoke Export |
| `--drive-testviewer-rapture-import` / `-diag` / `-stoppause` | Pilote import / diagnostic / vérif Stop-Pause-Reprise |
| `--drive-testviewer-legacy-kbis` / `--drive-testviewer-kbis-stress` | Rejoue / stresse le smoke KBIS via UI (seul module non-RAPTURE avec drive CLI) |
| `--legacy-<id>` | **Auto-dispatch** vers `LegacyScenarioCatalog` (`kbis-vk`/`kbis-xex`, `alertes-*` ×4, `dcademat-*` ×8) |
| `--legacy-rapture-process` / `--legacy-rapture-export` / `--legacy-rapture-import` / `--legacy-rapture` | Worker Rapture visible (RIG live), variantes import/export/complet |
| `--rapture-selfdrive` | Worker invisible in-process, snapshot/restore SQL net-zero (rapide) |
| `--rapture-diag` | Diagnostic import complet (Mapper/Validator/Diff/Apply + rapport placement) |
| `--drive-retaud-pubs --audience-id <N>` | Navigation READ-ONLY « Publicités en attente » PROC_RETAUD + screenshot |
| `--reset-smoke-db` | ⚠️ Panic restore SQL : restaure tous les résidus écrits par les smoke scenarios |
| `--inspect-testviewer-kbis-legacy` | Diag : screenshot + dump UIA du tab Smoke Legacy KBIS |

**xUnit (logique pure)** : `dotnet test --filter "FullyQualifiedName~<NomTest>"` dans `Rig.Rapture.Tests\`.

⚠️ Rappel : **ne JAMAIS** lancer un mode scenario direct sans que le triplet bouton GUI + commande VM +
AutomationId existe (cf. § Règles non-négociables). Le chemin par défaut reste **Rig Testing UI** ou
`--drive-testviewer-*` / la skill `/rig-loop`.

---

## 📧 Erreurs par mail (Outlook) — souvent la VRAIE source de vérité

⚠️ **Un exit code 0 / « passed » du driver ne prouve PAS l'absence d'erreur.** RIG envoie chaque crash
interne par mail pendant un import, **invisible** au driver FlaUI. Quand un run paraît OK mais qu'un doute
subsiste (ou pour confirmer un FAIL), **vérifier les mails de crash**.

**Forme du mail** (vérifié) :
* **Expéditeur** : `automate@amitel.fr`
* **Sujet** : `Crash <PROC> / <Classe> / <Méthode> sur <MACHINE>/<base>`
  (ex. `Crash PROC_RETAUD / RaptureImportValidator / ValidateAffaire sur PCLHOPITALW11/9995`)
* **Corps** : `LogFile : C:\RIG\Data\Log\9995\RigClientAccueil-<user>-<stamp>.txt` + la ligne de crash
  (ex. `Crash Invalid object name 'INSTANCE'. : SELECT INSTANCE.* FROM INSTANCE WHERE …` → typiquement
  une **base mal choisie** dans le sélecteur BDD, cf. § dédiée).

**Lister les mails de crash via Outlook (COM PowerShell, READ-ONLY — vérifié)** :

```powershell
$ol = New-Object -ComObject Outlook.Application
$ns = $ol.GetNamespace('MAPI')
$filter = "@SQL=urn:schemas:httpmail:subject LIKE '%Crash%'"
# Les mails automate@amitel.fr n'arrivent PAS forcément dans le store par défaut → on balaie TOUS les stores.
foreach ($store in $ns.Stores) {
  $inbox = $store.GetRootFolder().Folders | Where-Object { $_.Name -match 'ception|nbox' } | Select-Object -First 1
  if (-not $inbox) { continue }
  $hits = $inbox.Items.Restrict($filter)
  if ($hits.Count -eq 0) { continue }
  $hits.Sort('[ReceivedTime]', $true)
  "== $($store.DisplayName) : $($hits.Count) mails Crash =="
  $hits | Select-Object -First 10 | ForEach-Object {
    '{0} | {1}' -f $_.ReceivedTime.ToString('yyyy-MM-dd HH:mm'), $_.Subject
    ($_.Body -split "`n") | Select-String 'LogFile|Crash' | Select-Object -First 3 | ForEach-Object { '    ' + $_.Line.Trim() }
  }
}
```

* ⚠️ **Plusieurs stores** peuvent coexister (boîte O365 `*.onmicrosoft.com` **+** boîte on-prem `amitel.fr`).
  Les mails `automate@amitel.fr` n'arrivent **pas forcément dans le store par défaut** → `GetDefaultFolder()`
  seul peut tout rater. **Balayer tous les stores** (la boucle ci-dessus) est la voie sûre.
* Filtrer par date : ajouter `… LIKE '%Crash%' AND urn:schemas:httpmail:datereceived > '06/17/2026'`.
* Le `LogFile :` du corps pointe le log RIG exact → chaîner avec le grep `C:\RIG\Data\Log\9995\…` pour la
  stack complète (`TestValidite`/`eLog9Crash`/`SendMail`).

---

## 📞 Fallback

* **Bloqué sur un échec batch que les logs ne tranchent pas** → lire `RIG-TV\Audit\screenshots-loop\`
  + `%USERPROFILE%\Desktop\JsonRapture\screenshots\<PID>\` (per-worker), puis
  `%LOCALAPPDATA%\rig-wpf-testviewer\testviewer-yyyyMMdd.log`.
* **Cache te ment** (suspect : test marqué PASS alors qu'on a touché le code) →
  Settings ⚙ → « Vider le cache », relance batch.
* **TestViewer ne démarre pas** → kill `Rig.Wpf.Kbis.TestViewer.exe` puis
  `ensure-fresh.ps1 -Project TestViewer` + check log `testviewer-yyyyMMdd.log`.
* **`ml-loop.ps1` jette `JSON invalide`** → soit aucun batch n'a tourné, soit
  Phase 1 a crash en milieu de batch. Lancer un batch propre d'abord.
* **Doute sur où placer un fichier d'audit / temp** → JAMAIS dans le repo, toujours
  `RIG-TV\Audit\`, `Z:\test\…` ou `$env:TEMP`.
* **Doute sur une règle** → ce fichier, puis `C:\Code RIG\CLAUDE.md`, puis
  `\\ged2\rig\Projets IA\Documentation\AI-Generated\Claude.md`.
* **Doute persistant ou règle manquante** → contacter le mainteneur du harnais (`<alias-équipe-RIG>@amitel.fr`).

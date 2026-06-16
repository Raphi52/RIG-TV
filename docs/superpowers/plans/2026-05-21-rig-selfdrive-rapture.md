# RIG self-drive Rapture — Plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps en `- [ ]`.

**Goal:** Exécuter les 16 scénarios de smoke import Rapture **en parallèle**, sans monopoliser souris/clavier/focus, sans VM — en transformant `RigClientAccueil.exe` en worker self-drive in-process.

**Architecture:** `RigClientAccueil --rapture-smoke <id> --json <path> --audience-id <n> --out <résultat>` démarre RIG, se connecte à la DB (greffe= → pas de mire), puis **au lieu d'afficher la console d'accueil** exécute le pipeline d'import Rapture in-process et se termine — process **sans aucune fenêtre**. 16 workers invisibles = 16 scénarios parallèles. Orchestration : TestViewer WPF (UI) → SmokeRunner (lanceur : spawn N workers + AudienceLock + snapshot/restore + collecte) → workers RigClientAccueil.

**Tech Stack:** .NET Framework 4.8, C#, WinForms (RIG legacy), pipeline `RAPTURE_IMPORT` de PROC_RETAUD.

**Source faisabilité:** investigation 2026-05-21 (verdict FAISABLE, anchor `RigConsoleAccueil.Main2` après `CheckUser()`).

---

## Contraintes repo

- **Pas de commit** sans « go » explicite (CLAUDE.md règle 6). Persistance = snapshot `Z:\test\` (mémoire `feedback_no_commit_snapshot_to_z`).
- **Vérif règle 15** : build → deploy → run self-drive → analyse résultat (JSON + DB + screenshot si UI) → non-régression.
- Sous-agents implémenteurs/exploration en `model: sonnet`.
- x86 préservé, `safeBuild.ps1` si COM-interop.

## Réutilisé du travail précédent

- ✅ `AudienceLock` (verrou cross-process par audience) — déjà fait, réutilisé tel quel pour sérialiser les workers sur une même audience.
- ✅ SmokeRunner : code SQL `SnapshotForApply`/`RestoreAfterApply`/`QueryAuditCounts`, manifest des 16 scénarios, UI TestViewer "All scenarios" — réutilisés.
- ❌ Abandonné (HDESK) : `RigDesktop`, `Interaction`, `RunAttached`, la conversion FlaUI input-free pour le rapture. (`Interaction`/`RigDesktop` restent comme fichiers inertes ; la conversion `LegacyDriver` reste valable pour les modes KBIS/export.)

## Structure de fichiers

| Fichier | Rôle | Action |
|---|---|---|
| `Source/RIG/EXE/Accueil/RigClientAccueil/RigRaptureSelfDrive.cs` | Pipeline d'import in-process | Créer |
| `Source/RIG/EXE/Accueil/RigClientAccueil/.../RigConsoleAccueil.cs` | Hook `--rapture-smoke` dans `Main2` | Modifier |
| `Source/RIG/EXE/Accueil/RigClientAccueil/RigClientAccueil.csproj` | `ProjectReference` → PROC_RETAUD | Modifier |
| `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs` | Mode `--rapture-selfdrive` (lanceur workers) | Modifier |
| `Source/Wpf/Rig.Wpf.Kbis.TestViewer/ViewModels/MainWindowViewModel.cs` | Câbler le batch sur le nouveau mode | Modifier |

---

## Task 1 : RigClientAccueil référence PROC_RETAUD

**Files:** Modifier `Source/RIG/EXE/Accueil/RigClientAccueil/RigClientAccueil.csproj`

- [ ] **Step 1 — ajouter la ProjectReference**

S'inspirer du pattern éprouvé de `Source/RIG/DLL/Processus/PROC_RETAUD/RaptureImportDiag_EXE/RaptureImportDiag_EXE.csproj` (il référence déjà `PROC_RETAUD.csproj` + RigMetier/RigBaseGreffe). Ajouter dans `RigClientAccueil.csproj`, dans un `<ItemGroup>` de `<ProjectReference>` :
```xml
    <ProjectReference Include="..\..\..\DLL\Processus\PROC_RETAUD\PROC_RETAUD.csproj">
      <Project>{GUID-de-PROC_RETAUD}</Project>
      <Name>PROC_RETAUD</Name>
    </ProjectReference>
```
(Récupérer le GUID exact dans `PROC_RETAUD.csproj`. Vérifier le chemin relatif réel depuis `RigClientAccueil.csproj`.)

- [ ] **Step 2 — build**

`safeBuild.ps1` sur `RigClientAccueil.csproj` (COM-interop possible). Attendu : build vert. Si des dépendances transitives de PROC_RETAUD manquent (RigBaseGreffe, etc.), les ajouter en ProjectReference comme le fait `RaptureImportDiag_EXE`.

- [ ] **Step 3 — ⚠ PAUSE persistance** : snapshot `Z:\test\` du csproj sur « go » utilisateur.

---

## Task 2 : RigRaptureSelfDrive — pipeline in-process

**Files:** Créer `Source/RIG/EXE/Accueil/RigClientAccueil/RigRaptureSelfDrive.cs`

- [ ] **Step 1 — créer la classe**

Classe statique `RigRaptureSelfDrive` avec `public static int Run(string scenarioId, string jsonPath, int audienceId, string codeGreffe, string user, string outPath)`. Séquence (calquée sur `RaptureImportOrchestrator.Run`, sans file-picker ni form modal) :

```
1. RaptureImportParser parser = new RaptureImportParser();
   RaptureImportDto dto = parser.ParseFile(jsonPath);
2. AudienceCabinet audience = AudienceCabinet.GetAudienceCabinet(audienceId, codeGreffe);
3. RaptureImportMapper mapper = new RaptureImportMapper(codeGreffe); mapper.Load();
4. RaptureValidationReport vReport = new RaptureImportValidator(mapper).Validate(dto);
5. RaptureDiffReport diff = new RaptureImportDiff().Compute(dto, audience, vReport);
6. foreach (FieldDiff fd in diff.<tous les FieldDiff>) fd.Accepted = fd.Modifiable;
7. RaptureImportApplyService svc = new RaptureImportApplyService(codeGreffe, user, jsonPath, dto.SchemaVersion?, dto.ExportedBy?, dto.ExportedAt?);
   RaptureApplyReport report = svc.Apply(diff, audience, dto);
8. Sérialiser un objet résultat JSON vers outPath : { scenarioId, validation: {erreurs, avertissements}, diff: {modifications}, apply: {Applied, Skipped, Errors}, ok: bool }.
9. return ok ? 0 : 1;
```

Tout encadré d'un `try/catch` : sur exception, écrire le résultat JSON avec `ok:false` + le message, return 1. Logger chaque étape sur `Console` (préfixe lisible).

⚠ Vérifier les signatures exactes en lisant les fichiers de `Source/RIG/DLL/Processus/PROC_RETAUD/RAPTURE_IMPORT/` (`RaptureImportApplyService.cs`, `RaptureImportDiff.cs`, `FormRaptureImportRecap.cs` lignes 222-258 pour la logique d'acceptation des FieldDiff). La signature `Apply` et le constructeur d'`ApplyService` viennent de l'investigation faisabilité — confirmer par lecture.

- [ ] **Step 2 — Cas B / Cas C**

Pour les scénarios où l'audience n'existe pas (Cas C) ou multi-match (Cas B) : `audienceId` peut être absent. Si `audienceId <= 0`, répliquer la logique de `RaptureImportOrchestrator.ResolveAudienceForImport` **sans les MessageBox** : `RaptureAudienceMatcher.Find(dto, codeGreffe)` → si unique, prendre ; si multi, prendre la 1ère ; si aucune, `RaptureAudienceCreator.CreateFromJson(dto, codeGreffe, user, jsonPath)`. Logger le cas retenu.

- [ ] **Step 3 — build** : `safeBuild.ps1` `RigClientAccueil.csproj` → vert.

- [ ] **Step 4 — ⚠ PAUSE persistance** snapshot Z sur « go ».

---

## Task 3 : hook `--rapture-smoke` dans Main2

**Files:** Modifier `RigConsoleAccueil.cs`

- [ ] **Step 1 — parsing + hook**

Dans `RigConsoleAccueil.Main2`, juste après `CheckUser()` (vers ligne 95) et **avant** `new FormRigClientAccueil()` :
```csharp
string smokeScenario = param.GetParametre("rapture-smoke");
if (!string.IsNullOrEmpty(smokeScenario))
{
    int rc = RigRaptureSelfDrive.Run(
        smokeScenario,
        param.GetParametre("json"),
        ParseIntSafe(param.GetParametre("audience-id")),
        RIG.Common.CodeGreffeRig,
        AmiSystem.AmiEnvironment.UserName,   // adapter au vrai accès user
        param.GetParametre("out"));
    Environment.ExitCode = rc;
    return;   // pas d'Application.Run, pas de form accueil
}
```
Vérifier le vrai nom de la variable du parser (`param` / `ListeParametre`) et la vraie méthode d'accès au user. `ParseIntSafe` : helper local `int.TryParse` → 0 si absent.

- [ ] **Step 2 — bypass garde single-instance**

Si la garde single-instance (lignes ~82-91) s'exécute avant le hook : déplacer le hook AVANT la garde, ou ajouter `&& string.IsNullOrEmpty(param.GetParametre("rapture-smoke"))` à la condition de garde. (L'investigation dit que la garde ne matche pas faute de MainWindowTitle, mais on sécurise.)

- [ ] **Step 3 — build** `safeBuild.ps1` → vert.

- [ ] **Step 4 — smoke unitaire**

Lancer en ligne de commande **via Rig Testing** (pas de CLI directe — sera câblé Task 4/5) — pour ce step, validation = build vert seulement ; le run réel est en Task 6.

- [ ] **Step 5 — ⚠ PAUSE persistance** snapshot Z sur « go ».

---

## Task 4 : SmokeRunner — mode lanceur `--rapture-selfdrive`

**Files:** Modifier `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs`

- [ ] **Step 1 — nouveau mode**

Ajouter un mode `--rapture-selfdrive` qui prend `--json`, `--scenario-id`, `--audience-id`, `--apply`, et les flags expected. Au lieu de piloter RIG via FlaUI, il :
1. Si `--apply` : `AudienceLock.Acquire(audienceId, 5min)` + `SnapshotForApply(audienceId)`.
2. Lance `RigClientAccueil.exe greffe=<g> rapture-smoke=<id> json=<path> audience-id=<id> out=<tmpResult.json>` via `Process.Start`, attend la fin (timeout ~120s).
3. Lit le `tmpResult.json` produit par le worker → parse validation/diff/apply.
4. Assertions vs expected (warnings, modifications) — réutiliser la logique existante.
5. Si `--apply` : `RestoreAfterApply` + libère `AudienceLock`.
6. Écrit le verdict sur stdout (format `✓`/`✗` déjà parsé par le proxy).

Réutiliser au maximum le code SQL existant (`SnapshotForApply`, `RestoreAfterApply`, `QueryAuditCounts`, `AudienceLock`).

- [ ] **Step 2 — build** `dotnet build` SmokeRunner Release → vert.

- [ ] **Step 3 — ⚠ PAUSE persistance** snapshot Z sur « go ».

---

## Task 5 : TestViewer — câbler le batch parallèle

**Files:** Modifier `Source/Wpf/Rig.Wpf.Kbis.TestViewer/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1 — RunRaptureProcessE2E + RunAllRaptureScenarios**

Basculer `FixedArgs` de `--legacy-rapture-process` vers `--rapture-selfdrive`. Le batch « All scenarios » : pool de N workers parallèles (la logique pool existe déjà depuis l'ex-Task 7 — la réutiliser) — chaque worker = un SmokeRunner `--rapture-selfdrive` qui lance un RigClientAccueil self-drive. Degré configurable `RIG_SMOKE_PARALLELISM` (défaut 4-6 — chaque worker RIG ≈ quelques centaines de Mo).

- [ ] **Step 2 — build** TestViewer Release → vert.

- [ ] **Step 3 — ⚠ PAUSE persistance** snapshot Z sur « go ».

---

## Task 6 : Vérification (CLAUDE.md règle 15)

- [ ] **Step 1 — deploy** : `PROC_RETAUD.dll` + `RigClientAccueil.exe` rebâtis → `C:\rig` (deploy.ps1) ; GAC push si strong-named touché.
- [ ] **Step 2 — 1 scénario** : via TestViewer, lancer `cas-a-pc-clotures` en self-drive. Vérifier : le worker tourne **sans fenêtre**, le JSON résultat est produit, l'import est correct (assertions DB).
- [ ] **Step 3 — pendant le run** : bouger souris + taper dans une autre app → **zéro interférence**.
- [ ] **Step 4 — 16 en parallèle** : « All scenarios ». Vérifier : 16 workers RIG simultanés invisibles, durée fortement réduite, non-régression des verdicts (vs le batch de référence corrigé), net-zero DB.
- [ ] **Step 5 — ⚠ PAUSE persistance** snapshot Z final sur « go ».

---

## Risques

| Risque | Mitigation |
|---|---|
| Dépendances transitives de PROC_RETAUD manquantes dans RigClientAccueil | Copier le set de ProjectReference de `RaptureImportDiag_EXE` qui compile déjà |
| 16 RIG = trop de RAM | Degré `RIG_SMOKE_PARALLELISM` configurable (défaut 4-6) |
| Le worker laisse un process zombie si crash | Timeout + `Process.Kill` côté SmokeRunner lanceur |
| `FieldDiff.Accepted`/`Modifiable` : noms réels à confirmer | Lire `FormRaptureImportRecap.cs:222-258` (la logique du bouton Importer) |

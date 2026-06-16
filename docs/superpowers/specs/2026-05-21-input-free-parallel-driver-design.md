# Driver de tests input-free + parallélisable — Design

**Date :** 2026-05-21
**Statut :** validé (brainstorming)
**Périmètre :** `Rig.Wpf.Kbis.SmokeRunner` (`LegacyDriver.cs`, `Program.cs`) + `Rig.Wpf.Kbis.TestViewer`

## Problème

Aujourd'hui un run de tests Rig Testing **monopolise la souris et le clavier** de
l'utilisateur : le driver FlaUI (`LegacyDriver.cs`) synthétise de l'input global
(FlaUI `.Click()` / `.DoubleClick()` qui déplacent le curseur réel, `Mouse.MoveTo/Click`,
`Keyboard.Type` via `SendInput`, `SetForegroundWindow`/`AttachThreadInput`). Pendant
les ~8 min d'un batch « All scenarios » (16 scénarios séquentiels), la machine est
inutilisable.

Conséquences :
- Impossible de travailler pendant un run.
- Impossible de **paralléliser** des runs : deux runs pilotant la souris globale se
  marchent dessus.

## Objectifs

1. **Critère de succès #1** : pouvoir utiliser souris/clavier normalement pendant un run.
2. RIG **invisible** pendant les runs (aucune fenêtre sur le desktop utilisateur).
3. **Paralléliser** : batch « All scenarios » réparti sur N workers + plusieurs runs
   indépendants concurrents.
4. **Non-régression** : les 16 scénarios continuent de passer (16/16 PASS actuel).

## Décisions de design (actées)

| Décision | Choix |
|---|---|
| Isolation | Desktop Windows séparé (`HDESK`) — RIG totalement invisible |
| Périmètre | Tout le driver (Rapture import/export/diag + KBIS) |
| Parallélisme | Batch interne **et** runs indépendants concurrents |
| Contention DB | Verrou Windows nommé **par audience** (tous les runs restent greffe 9995 / même `RIG_DEV`) |

### Pourquoi pas « une base par run »

Investigué et écarté. RIG choisit sa base par **code greffe**
(`HKLM\SOFTWARE\Wow6432Node\RIG\<CodeGreffe>\General\` → `BaseSql`/`ServeurSql`).
« 1 base par run » = « 1 code greffe par run », or le code greffe sert simultanément
à 3 usages contradictoires : sélection registre de la base (codes distincts),
validation du JSON Rapture (`codeGreffe: "9995"` → tous en 9995), garde
single-instance par titre de fenêtre. Des greffes distincts cassent la validation
JSON, et le provisionnement exige HKLM admin + N bases SQL restaurées + approbation
infra partagée. Le verrou par audience sidestep les 3 contraintes pour ~30 lignes.

### Fait technique structurant

| Marche sur HDESK séparé | Ne marche PAS sur HDESK |
|---|---|
| Patterns UIA (`Invoke`, `Toggle`, `Value.SetValue`, `SelectionItem.Select`, `ExpandCollapse`) | FlaUI `.Click()` / `.DoubleClick()`, `Mouse.*`, `Keyboard.*` (`SendInput` global) |
| `PostMessage` / `SendMessage` ciblés sur un hwnd (`WM_LBUTTONDOWN/UP`, `BM_CLICK`, `WM_SETTEXT`) | `Capture.Screen` plein écran (mauvais desktop) |
| `PrintWindow` (capture élément/fenêtre, rend indépendamment de la visibilité) | |

## Architecture

Un **run** = 1 process SmokeRunner = 1 HDESK = 1 `RigClientAccueil.exe`. Tout est
auto-isolé ; la seule coordination inter-run passe par des **mutex Windows nommés**
(cross-process), donc fonctionne identiquement pour le batch et pour des runs
indépendants qui ne se connaissent pas.

### Unités nouvelles — `Rig.Wpf.Kbis.SmokeRunner`

#### `RigDesktop.cs` (~120 lignes)
Cycle de vie du desktop isolé, **nom unique par run**.
- `RigDesktop Create(string runId)` — si headless : `CreateDesktop($"RigSmoke_{runId}")`
  (`runId` = PID du SmokeRunner) ; sinon no-op (desktop courant).
- `Process LaunchProcess(string exePath, string workingDir)` — P/Invoke `CreateProcess`
  avec `STARTUPINFO.lpDesktop` pointant le desktop (`.NET Process.Start` n'expose pas
  `lpDesktop`). Le process enfant + ses fenêtres + ses sous-process héritent du desktop.
- `void AttachCurrentThread()` — `SetThreadDesktop(hdesk)` sur le thread d'automation,
  **appelé avant** `new UIA3Automation()` (sinon FlaUI n'énumère pas le HDESK).
- `IDisposable` — tue l'arbre process RIG, restaure le thread desktop, `CloseDesktop`.

#### `Interaction.cs` (~190 lignes)
Helper d'interaction input-free, sans état. Cascade pattern UIA → message fenêtre.
- `Click(AutomationElement)` — clic non-bloquant. hwnd natif valide → `PostMessage(hwnd,
  WM_LBUTTONDOWN, MK_LBUTTON, lparam_centre)` + `PostMessage(hwnd, WM_LBUTTONUP, 0,
  lparam_centre)`. **Posté** = non-bloquant = fire-and-forget. Marche pour boutons (y
  compris ceux ouvrant `MessageBox.Show` → résout le deadlock du bouton Importer recap),
  lignes de grille, cellules. Pas de hwnd → `InvokePattern.Invoke()` /
  `SelectionItemPattern.Select()` selon dispo.
- `Invoke(AutomationElement)` — `InvokePattern.Invoke()`, pour boutons connus sûrs
  (sans modale), quand le blocage synchrone est acceptable.
- `SetText(AutomationElement, string)` — `ValuePattern.SetValue()` si supporté ; sinon
  `SendMessage(hwnd, WM_SETTEXT)` (Send et non Post : porte un pointeur string ;
  rapide, sans modale → pas de deadlock).
- `Select(AutomationElement row)` — `SelectionItemPattern.Select()` si supporté ; sinon
  `Click(row)` (messages souris postés sur le hwnd de la ligne, ou sur le hwnd de la
  grille aux coords-client de la ligne — cas grille RigAutomate custom sans hwnd propre).
- `CaptureWindow(AutomationElement, string path)` — `PrintWindow(hwnd, PW_RENDERFULLCONTENT)`
  → bitmap → PNG. Aucun `Capture.Screen`.

#### `AudienceLock.cs` (~40 lignes)
- `IDisposable Acquire(int audienceId, TimeSpan timeout)` — `Mutex` nommé
  `Global\RigSmokeAud_{id}`. Runs sur audiences différentes → parallèles ; même
  audience → sérialisés. Timeout dépassé → exception claire (pas de hang).

### Modifications

#### `LegacyDriver.cs`
- `Launch()` → délègue à `RigDesktop` au lieu de `Process.Start`. Ordre :
  `RigDesktop.Create(pid)` → `AttachCurrentThread()` → `LaunchProcess(rigExe)` →
  `new UIA3Automation()` → recherche fenêtre principale.
- ~22 `element.Click()` / `.DoubleClick()` → `Interaction.Click(...)`.
- `try { x.AsButton().Invoke(); } catch { x.Click(); }` → `Interaction.Click(x)`.
- `Keyboard.Type(jsonPath)` (chemin OpenFileDialog) → `Interaction.SetText(...)` ;
  soumission du dialogue (ex-`Keyboard.Type(ENTER)`) → `Interaction.Click(boutonOuvrir)`.
- `Mouse.MoveTo/Click` (recap Importer) → `Interaction.Click(importer)`.
- `Capture.Element` + fallback plein écran → `Interaction.CaptureWindow(...)`.
- `SetForeground()` / `AttachThreadInput` / `SetForegroundWindow` → **supprimés**
  (inutiles et impossibles sur HDESK).

#### `Program.cs` (SmokeRunner)
- Section `snapshot → apply → restore` encadrée par
  `using (AudienceLock.Acquire(applyAudienceId, timeout))`.
- Scénarios dry-run (sans `--apply`) → pas de snapshot/restore → pas de lock.
- Cas C (crée une audience neuve, ID unique) → aucune contention sur audience existante.
- Cas B (`cas-b-multi-match.setup.sql` clone 28590) → acquiert aussi le lock de 28590
  pendant le setup.
- Screenshots écrits dans un sous-dossier par run : `screenshots\{runId}\`.

#### TestViewer (`MainWindowViewModel` + `SmokeRunnerProxy`)
- Le batch « All scenarios » (`RunAllRaptureScenariosAsync`) passe d'une boucle
  séquentielle à un **pool de N workers** (degré configurable `RIG_SMOKE_PARALLELISM`,
  défaut 3). Les 16 scénarios → une file ; N workers tirent dedans ; chaque worker =
  1 process SmokeRunner = 1 HDESK = 1 RigClientAccueil.
- `SmokeRunnerProxy` est aujourd'hui singleton (`RunAsync` throw si occupé) → **pool
  de N instances**, une par worker.
- Recap final PASS/FAIL agrégée comme aujourd'hui. Stop/Pause agissent sur **tous**
  les workers.

### Flag `RIG_DRIVER_HEADLESS` (env var, défaut ON)
- **ON** : HDESK créé, RIG invisible, `Interaction` strict (pattern/message only),
  parallélisme actif.
- **OFF** : RIG sur le desktop normal, `Interaction` s'autorise un dernier recours
  souris FlaUI (debug visuel d'un contrôle récalcitrant), **force le degré à 1** (la
  souris ne se parallélise pas).

## Gestion d'erreurs

- **`Interaction`** : élément sans hwnd ET sans pattern exploitable → exception claire
  (Name + ControlType + AutomationId). Pas de fallback souris silencieux — on identifie
  exactement le contrôle à durcir.
- **Échec création HDESK** (collision de nom, droits) → abort de ce run, message clair ;
  les runs frères continuent.
- **Timeout `AudienceLock`** (défaut 5 min) → le scénario échoue explicitement
  (« audience X verrouillée par un autre run >5min »), pas de hang.
- **Crash RIG** → détection existante (`_app.HasExited`) conservée ; le worker marque
  le scénario FAIL, les frères continuent.
- **Isolation des workers** : un worker qui meurt ne tue pas le batch.
- **Cleanup** : `RigDesktop.Dispose` en `finally` tue l'arbre RIG + `CloseDesktop` même
  sur exception. Le HDESK se libère automatiquement quand plus aucun thread/process ne
  le référence → pas de fuite même sur kill brutal.

## Isolation des unités

- `RigDesktop` ignore les interactions.
- `Interaction` ignore le HDESK (marche identique sur les deux desktops).
- `AudienceLock` ignore tout le reste.
- `LegacyDriver` orchestre.

Chaque unité est compréhensible et testable seule.

## Vérification

1. Build SmokeRunner + TestViewer vert.
2. Lancer « All scenarios », degré 3, `RIG_DRIVER_HEADLESS=1`.
3. **Pendant le run** : bouger la souris, taper dans Notepad/Teams → **zéro
   interférence** *(critère de succès #1)*.
4. RIG **jamais visible** sur le desktop.
5. 3 `RigClientAccueil` coexistent (3 HDESK).
6. **Non-régression : 16/16 PASS** (vs 16/16 actuel).
7. Logs : 2 scénarios sur la même audience sérialisent (attente lock visible),
   audiences différentes se chevauchent.
8. Temps total ~8 min → ~3 min.
9. **Runs indépendants** : lancer un 2e run pendant le batch → pas de corruption
   (audit net-zero tient des deux côtés).
10. Screenshots produits par sous-dossier `{runId}\`, recap lisible.

## Risques et mitigations

| Risque | Mitigation |
|---|---|
| Un contrôle COM/ActiveX ne répond ni au pattern UIA ni aux messages fenêtre | `Interaction` lève une exception nommant le contrôle ; durcissement ciblé au cas par cas (messages `WM_MOUSE*` postés sur le hwnd de la grille restent disponibles) |
| `PrintWindow` rend un contrôle COM en noir | Best-effort ; la recap (WinForms pur) se capture bien ; assertions SQL/UIA = gate primaire (règle 15d) |
| `SetThreadDesktop` échoue si le thread a déjà des fenêtres/hooks | SmokeRunner est une console sans UI ; `SetThreadDesktop` appelé tôt, avant toute init UIA |
| Saturation machine si degré trop haut (chaque RIG est lourd) | Degré configurable, défaut prudent 3 ; `RIG_DRIVER_HEADLESS=0` force 1 |
| Plafond de parallélisme ~5x (5 audiences distinctes) | Acceptable pour la phase 1 ; « 1 base par run » documenté comme évolution si insuffisant |

## Hors périmètre

- « 1 base par run » (provisionnement registre/SQL) — documenté comme évolution future.
- Refonte des scénarios ou des assertions (le design est purement infrastructurel :
  zéro changement de comportement de test attendu).

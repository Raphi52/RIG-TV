---
name: rig-loop
description: Use when working on rig-testing — running the test loop, executing rig-testing scenarios, triaging test results, or iterating on Rapture import tests. Orchestrates the 6-phase loop via the SmokeRunner --loop CLI.
---

# Boucle de travail rig-testing

Orchestre la boucle de test rig-testing en 6 phases. Le travail mécanique est fait
par le CLI `SmokeRunner --loop` ; cette skill ne fait que **piloter** et **interpréter**.

`<repo>` = racine du repo = `C:\Code RIG\RIG-TV` (le repo a été aplati : plus de `Source\Wpf\`).

CLI : `<repo>\Rig.Wpf.Kbis.SmokeRunner\bin\Release\net48\Rig.Wpf.Kbis.SmokeRunner.exe`
Forme courte employée ci-dessous : `rig-loop <verbe>` = `SmokeRunner.exe --loop <verbe>`.

Exemple complet (PowerShell) :

```powershell
$sr = "<repo>\Rig.Wpf.Kbis.SmokeRunner\bin\Release\net48\Rig.Wpf.Kbis.SmokeRunner.exe"
& $sr --loop bench status      # le banc est-il libre ?
& $sr --loop bench acquire     # le réserver
& $sr --loop run --scenarios all
& $sr --loop bench release     # le rendre à la fin
```

**Prérequis** — le mode `--loop` lit les scénarios depuis le build **Release** de TestViewer
(fallback codé en dur, `LoopRun.cs:232`). Builder TestViewer en Release **au moins une fois** :
`dotnet build Rig.Wpf.Kbis.TestViewer\Rig.Wpf.Kbis.TestViewer.csproj -c Release` — sinon `run`
trouve **0 scénario**. Note : `--loop run` ne rebuild PAS le harnais ; seul `--loop build`
rebuild PROC_RETAUD.

**Vocabulaire** — *banc* : verrou logiciel partagé garantissant qu'une seule personne lance
les scénarios à la fois (sinon machine saturée + DB de test corrompue). *FLAKY* : scénario
instable (échoue puis passe au retry). *data-drift* : la donnée/audience de la fixture est
périmée, pas le code. *manifest* : `RaptureScenarios\manifest.json`, déclare les scénarios
et leurs compteurs attendus.

## Les 6 phases

0. **Acquérir le banc** — `rig-loop bench status`. Si occupé, STOP et prévenir l'utilisateur.
   Sinon `rig-loop bench acquire`.
1. **Cadrer** — l'utilisateur énonce le besoin. Le traduire en scénarios concrets
   (entrées du manifest + fixtures JSON). NE PAS deviner le besoin : demander si flou.
2. **Écrire** — écrire/étendre les scénarios dans `RaptureScenarios\manifest.json`
   + fixtures JSON + compteurs attendus (`expectedWarnings`, `expectedDetectedModifications`,
   `expectedAppliedModifications`).
3. **Exécuter** — `rig-loop run --scenarios all` (ou `--scenarios id1,id2`).
   Si le besoin implique une écriture DB : `--apply` (⚠ partiel : certains scénarios le
   marquent encore théorique, ex. `cas-diff-replacement-note` — vérifier le manifest avant).
   Ne pas babysitter — le CLI gère parallélisme + retry.
4. **Observer & trier** — lire le `result.json` (chemin affiché en fin de run). Pour
   chaque scénario non-PASS, appliquer l'ARBRE DE TRIAGE ci-dessous. Ne lire les
   screenshots que pour les cas qui exigent une confirmation visuelle.
5. **Proposer → GATE HUMAIN** — présenter à l'utilisateur : résumé PASS/FAIL/FLAKY,
   triage, et une PROPOSITION de suite. NE JAMAIS relancer un cycle complet sans
   validation humaine du nouveau besoin.
6. **Boucler ou clore** — si l'utilisateur valide une suite → retour phase 1.
   Sinon → `rig-loop bench release` + résumer.

## Arbre de triage (phase 4)

Lire `<repo>\Rig.Wpf.Kbis.SmokeRunner\loop\baseline.json`. Pour chaque
scénario `FAIL` ou `FLAKY` :

1. `verdict == "FLAKY"` (champ de `result.json`) OU `id` ∈ `baseline.knownFlakes[*].id` →
   **FLAKE** : ne pas remonter comme bug. Mentionner « flake connu ».
2. `id` ∈ `baseline.dataDriftScenarios[*].id` → **DATA-DRIFT** : ce n'est pas un bug du code,
   c'est une fixture/audience périmée. Proposer la correction de la donnée.
3. `failReason` parle d'un compteur attendu (`DIVERGE`, `attendus=`) → **MANIFEST STALE** :
   le compteur du manifest ne correspond plus. Proposer de corriger le manifest.
4. Sinon → **VRAI BUG** : remonter en priorité avec `failReason` + artefacts.

## Garde-fous

- Un échec n'est déclaré « vrai bug » que si `verdict == "FAIL"` ET `retried == true`
  (les deux ensemble) — `retried:true` seul peut accompagner un `FLAKY` (2ᵉ essai réussi).
- La phase 5 est TOUJOURS un gate humain — la skill propose, l'humain décide.
- Avant tout `rig-loop run` : vérifier `rig-loop bench status` (phase 0).
- Si un nouveau flake apparaît de façon répétée, proposer de l'ajouter à `baseline.json`.

## Playbook

Référence détaillée : `<repo>\Rig.Wpf.Kbis.SmokeRunner\loop\LOOP.md`.

---
name: rig-loop
description: Use when working on rig-testing — running the test loop, executing rig-testing scenarios, triaging test results, or iterating on Rapture import tests. Orchestrates the 6-phase loop via the SmokeRunner --loop CLI.
---

# Boucle de travail rig-testing

Orchestre la boucle de test rig-testing en 6 phases. Le travail mécanique est fait
par le CLI `SmokeRunner --loop` ; cette skill ne fait que **piloter** et **interpréter**.

CLI : `<repo>\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\bin\Release\net48\Rig.Wpf.Kbis.SmokeRunner.exe`
Forme courte employée ci-dessous : `rig-loop <verbe>` = `SmokeRunner.exe --loop <verbe>`.

## Les 6 phases

0. **Acquérir le banc** — `rig-loop bench status`. Si occupé, STOP et prévenir l'utilisateur.
   Sinon `rig-loop bench acquire`.
1. **Cadrer** — l'utilisateur énonce le besoin. Le traduire en scénarios concrets
   (entrées du manifest + fixtures JSON). NE PAS deviner le besoin : demander si flou.
2. **Écrire** — écrire/étendre les scénarios dans `RaptureScenarios\manifest.json`
   + fixtures JSON + compteurs attendus (`expectedWarnings`, `expectedDetectedModifications`,
   `expectedAppliedModifications`).
3. **Exécuter** — `rig-loop run --scenarios all` (ou `--scenarios id1,id2`).
   Si le besoin implique une écriture DB : `--apply`. Ne pas babysitter — le CLI gère
   parallélisme + retry.
4. **Observer & trier** — lire le `result.json` (chemin affiché en fin de run). Pour
   chaque scénario non-PASS, appliquer l'ARBRE DE TRIAGE ci-dessous. Ne lire les
   screenshots que pour les cas qui exigent une confirmation visuelle.
5. **Proposer → GATE HUMAIN** — présenter à l'utilisateur : résumé PASS/FAIL/FLAKY,
   triage, et une PROPOSITION de suite. NE JAMAIS relancer un cycle complet sans
   validation humaine du nouveau besoin.
6. **Boucler ou clore** — si l'utilisateur valide une suite → retour phase 1.
   Sinon → `rig-loop bench release` + résumer.

## Arbre de triage (phase 4)

Lire `<repo>\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\loop\baseline.json`. Pour chaque
scénario `FAIL` ou `FLAKY` :

1. `verdict == "FLAKY"` OU `id` ∈ `baseline.knownFlakes` → **FLAKE** : ne pas remonter
   comme bug. Mentionner « flake connu ».
2. `id` ∈ `baseline.dataDriftScenarios` → **DATA-DRIFT** : ce n'est pas un bug du code,
   c'est une fixture/audience périmée. Proposer la correction de la donnée.
3. `failReason` parle d'un compteur attendu (`DIVERGE`, `attendus=`) → **MANIFEST STALE** :
   le compteur du manifest ne correspond plus. Proposer de corriger le manifest.
4. Sinon → **VRAI BUG** : remonter en priorité avec `failReason` + artefacts.

## Garde-fous

- Un `FAIL` n'est jamais déclaré « vrai bug » sans que le `retried:true` du result.json
  confirme que le retry-once a aussi échoué.
- La phase 5 est TOUJOURS un gate humain — la skill propose, l'humain décide.
- Avant tout `rig-loop run` : vérifier `rig-loop bench status` (phase 0).
- Si un nouveau flake apparaît de façon répétée, proposer de l'ajouter à `baseline.json`.

## Playbook

Référence détaillée : `<repo>\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner\loop\LOOP.md`.

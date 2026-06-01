# Boucle de travail rig-testing — Playbook

Filet d'onboarding. La skill `/rig-loop` automatise tout ceci ; ce doc sert aux
nouveaux et à ceux qui n'utilisent pas Claude Code.

## Le CLI

`SmokeRunner.exe` (dossier `bin\Release\net48\`), mode `--loop` :

| Commande | Effet |
|---|---|
| `--loop run [--scenarios all\|<id,id>] [--apply]` | lance les scénarios en parallèle, retry-once sur échec, écrit `result.json` |
| `--loop build` | build PROC_RETAUD + deploy, verdict unique |
| `--loop bench acquire\|release\|status` | lock du banc de test |

## Les 6 phases

0. **Acquérir le banc** — `--loop bench status`, puis `acquire`. Évite que 2 personnes
   lancent 16 RIG en même temps (machine saturée + DB corrompue).
1. **Cadrer** — transformer le besoin en scénarios concrets.
2. **Écrire** — éditer `RaptureScenarios\manifest.json` + fixtures JSON.
3. **Exécuter** — `--loop run`.
4. **Observer & trier** — lire `result.json` ; classer chaque échec via `baseline.json` :
   flake / data-drift / manifest périmé / vrai bug.
5. **Proposer** — formuler la suite. Décision = humaine.
6. **Boucler ou clore** — `--loop bench release` à la fin.

## Artefacts

- `result.json` + logs + screenshots : `%LOCALAPPDATA%\rig-wpf-testviewer\loop\runs\<runId>\`
- Historique : `%LOCALAPPDATA%\rig-wpf-testviewer\loop\history.jsonl`
- Triage : `loop\baseline.json` (versionné — flakes connus, data-drift connus)

## Owner

`baseline.json`, la skill et le CLI ont un **owner désigné** : _(à renseigner)_.
Tout nouveau flake récurrent → l'ajouter à `baseline.json` via une PR.

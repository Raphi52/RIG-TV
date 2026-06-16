# Boucle de travail rig-testing — Design

**Date** : 2026-05-21
**Statut** : validé (brainstorming) — à transformer en plan d'implémentation
**Objectif** : élaborer la boucle de travail Claude « parfaite » autour de rig-testing,
adoptable par toute l'équipe pour gagner en productivité.

---

## 1. Contexte

`rig-testing` = `Rig.Wpf.Kbis.TestViewer` + `Rig.Wpf.Kbis.SmokeRunner`. Le moteur
d'exécution parallèle (lancer 16+ instances RigClientAccueil simultanément, « selon
la machine ») est **déjà réalisé** et hors périmètre de ce design.

Le cycle de travail vécu aujourd'hui comporte 5 étapes informelles : rédaction de
tests dans rig-testing → run parallèle → prise de screenshots / lecture des logs →
définition du nouveau besoin → relancement.

Ce design transforme ce cycle informel en une **boucle nommée, outillée et
partageable**.

## 2. Friction observée (diagnostic)

Analyse fondée sur une session complète passée dans ce cycle. Les pertes de temps
ne sont **pas** dans les 5 étapes visibles — elles sont transverses :

1. **Outillage shell** — ~12 scripts PowerShell jetables par session, uniquement à
   cause du quoting bash↔PowerShell qui casse en permanence. Friction à chaque tour.
2. **Pas de format de résultat structuré** — les logs sont du texte brut à parser au
   regex ; les fichiers `rapture-smoke-<timestamp>.log` n'ont pas le nom du scénario.
3. **Triage difficile** — distinguer vrai bug / flake / data-drift / manifest périmé
   demande une ré-analyse manuelle à chaque itération, sans mémoire.
4. **Build/déploiement fragile** — recouvrement long quand le build casse.
5. **Continuité inter-itération** — re-câbler le contexte après compaction.

Les deux tueurs dominants : **l'outillage** et **l'absence de format structuré**.

## 3. Architecture — 3 couches

Une skill seule ne suffit pas : une skill est de la prose interprétée, pas du code
déterministe. La fondation doit être un CLI déterministe + un historique persistant,
la skill n'étant qu'une fine couche de jugement par-dessus.

| Couche | Quoi | Corrige |
|---|---|---|
| **Mécanique** | CLI déterministe `rig-loop` | outillage, build, non-déterminisme |
| **Mémoire** | dossier `runs/` versionné | triage data-driven, continuité |
| **Jugement** | skill `/rig-loop` (mince) | orchestration, interprétation |

### 3.1 Couche Mécanique — CLI `rig-loop` (mode SmokeRunner)

**Décision arrêtée** : le CLI est un **nouveau mode de `Rig.Wpf.Kbis.SmokeRunner`**,
pas un exécutable séparé. Justification : SmokeRunner porte déjà le moteur 16-RIG,
l'infrastructure de modes (`--legacy-rapture-process`, `--reset-smoke-db`, …) et son
parsing d'arguments — zéro projet ni build supplémentaire.

Sous-commandes (mode `--loop` + verbe) :

| Invocation réelle | Forme courte | Comportement |
|---|---|---|
| `SmokeRunner --loop run [--scenarios all\|<ids>] [--apply]` | `rig-loop run` | lance le run 16-RIG parallèle ; **retry-once automatique** sur tout FAIL avant de le déclarer ; émet `result.json` ; range les artefacts |
| `SmokeRunner --loop build` | `rig-loop build` | encapsule build + deploy en une commande au verdict pass/fail unique |
| `SmokeRunner --loop bench acquire \| release \| status` | `rig-loop bench …` | gère le lock du banc de test |

Dans la suite du document, `rig-loop <verbe>` désigne la forme courte ;
l'invocation réelle est toujours `SmokeRunner --loop <verbe>`.

**Contrat de sortie — `result.json`** :

```json
{
  "runId": "20260521-1430",
  "summary": { "passed": 14, "failed": 1, "flaky": 1, "durationMs": 482000 },
  "scenarios": [
    {
      "id": "cas-a-subset",
      "verdict": "PASS",
      "retried": false,
      "durationMs": 30100,
      "failReason": null,
      "artifacts": ["screenshots/cas-a-subset.png", "logs/cas-a-subset.log"]
    },
    {
      "id": "cas-diff-replacement-note",
      "verdict": "FLAKY",
      "retried": true,
      "durationMs": 31800,
      "failReason": "OpenFileDialog absent après 10s",
      "artifacts": ["screenshots/cas-diff-replacement-note.png", "logs/..."]
    }
  ]
}
```

`verdict` ∈ `PASS | FAIL | FLAKY | SKIPPED`. `FLAKY` = a échoué puis réussi au retry.

Le CLI absorbe toute la complexité shell **une seule fois**. Les consommateurs (skill
ou humain) ne voient que du JSON.

### 3.2 Couche Mémoire — `runs/`

Dossier versionné qui rend le triage data-driven :

- `runs/<runId>/` — `result.json` + `screenshots/` + `logs/`, un dossier par run,
  artefacts nommés par scénario (fini la chasse au timestamp).
- `runs/baseline.json` — flakes connus (id + taux observé), scénarios data-drift
  connus, bugs préservés volontairement.
- `runs/history.jsonl` — une ligne par run (le `summary`), pour la tendance.

### 3.3 Couche Jugement — skill `/rig-loop`

Mince. N'implémente aucune mécanique : elle **appelle le CLI** et **lit la mémoire**.
Son corps contient :

- Phase 1-2 : comment traduire un besoin en scénarios concrets (manifest + fixtures).
- Phase 4 : l'arbre de triage —
  ```
  FAIL → présent dans baseline.flakes ?     → FLAKE, ignorer
       → présent dans baseline.dataDrift ?  → DATA-DRIFT, signaler (pas un bug)
       → compteur manifest périmé ?         → MANIFEST STALE, proposer correction
       → sinon                              → VRAI BUG, remonter en priorité
  ```
- Phase 5 : quand s'arrêter, comment formuler la proposition au gate humain.

## 4. La boucle — 6 phases

```
Ø. ACQUÉRIR LE BANC      lock — évite les exécutions concurrentes
1. CADRER                humain énonce le besoin → Claude le traduit en scénarios
2. ÉCRIRE                Claude écrit manifest + fixtures JSON + compteurs attendus
3. EXÉCUTER              CLI : run 16-RIG // + retry-once flake → result.json
4. OBSERVER & TRIER      Claude lit result.json + baseline → classe chaque FAIL
5. PROPOSER → GATE       Claude propose la suite ; l'humain valide / redéfinit
6. BOUCLER ou CLORE      validé → retour phase 1 ; fini → release lock + archive
```

**Principe directeur** : boucle **semi-autonome**. Claude enchaîne 2→3→4 sans
intervention. La phase 5 (« définir le nouveau besoin ») est du jugement métier —
elle reste **toujours** un gate humain. On nomme ce gate plutôt que de prétendre
l'automatiser.

## 5. Répartition humain / Claude / CLI

| Phase | Humain | Claude | CLI |
|---|---|---|---|
| Ø Acquérir banc | — | invoque | exécute le lock |
| 1 Cadrer | énonce le besoin | traduit en scénarios | — |
| 2 Écrire | — | écrit manifest + fixtures | — |
| 3 Exécuter | — | invoque | run // + retry + result.json |
| 4 Observer & trier | — | lit + classe | — |
| 5 Proposer → gate | **valide / redéfinit** | propose | — |
| 6 Boucler / clore | décide | exécute | release lock |

## 6. Structure de fichiers

```
RigApplication-testing/
├── Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/        ← CLI : nouveau mode --loop
├── .claude/skills/rig-loop/SKILL.md            ← skill (versionnée dans le repo)
└── Audit/rapture-smoke/
    ├── LOOP.md                                 ← playbook 1 page (filet)
    ├── .bench-lock                             ← lock du banc
    └── runs/
        ├── baseline.json
        ├── history.jsonl
        └── 20260521-1430/
            ├── result.json
            ├── screenshots/
            └── logs/
```

## 7. Adoption équipe

- **Lock du banc** — `rig-loop bench acquire` écrit `.bench-lock` (qui + échéance).
  `status` à consulter avant tout lancement. Empêche N collègues × 16 RIG = machine
  saturée + DB `RIG_DEV` corrompue par écritures croisées.
- **Owner désigné** — une personne possède le CLI, la skill et `baseline.json`,
  nommée dans `LOOP.md`. Sans owner, la skill périme et exécute du faux.
- **Playbook `LOOP.md`** — une page : les 6 phases, comment invoquer, liens. Filet
  pour l'onboarding et les collègues sans Claude Code.
- **Onboarding** — la première boucle d'un collègue se fait en binôme.

## 8. Garde-fous

- **Gate humain phase 5** — toujours. Claude ne relance jamais un cycle complet sans
  validation humaine du nouveau besoin.
- **Confirmation de flake** — un FAIL n'est jamais remonté comme vrai bug sans que le
  retry-once du CLI l'ait confirmé.
- **Bornes** — la partie autonome (phases 2→4) est bornée : cap du nombre de
  scénarios par run, timeout dur du CLI.
- **Budget** — option : budget temps/tokens par session de boucle.

## 9. Ce qui change vs aujourd'hui

| Aujourd'hui | Cible |
|---|---|
| ~12 scripts `.ps1` jetables par session | 1 CLI `rig-loop`, zéro script jetable |
| Logs `*-<timestamp>.log` à mapper à la main | `runs/<id>/` rangé par scénario |
| Texte brut parsé au regex | `result.json` structuré |
| Triage re-déduit à chaque itération | `baseline.json` → triage data-driven |
| Build fragile, recouvrement long | `rig-loop build` au verdict unique |
| Boucle implicite dans la tête de chacun | 6 phases nommées, gate explicite |
| Collisions de banc possibles | lock `.bench-lock` |

## 10. Périmètre

**Dans le périmètre** : le CLI `rig-loop` (modes run / build / bench), la couche
mémoire `runs/` + `baseline.json`, la skill `/rig-loop`, le playbook `LOOP.md`.

**Hors périmètre** : le moteur d'exécution 16-RIG parallèle (déjà fait) ; la refonte
des scénarios eux-mêmes ; les évolutions fonctionnelles de l'import Rapture.

## 11. Vérification — comment prouver que la boucle marche

1. `rig-loop build` retourne un verdict pass/fail unique et lisible.
2. `rig-loop run --scenarios all` produit un `result.json` valide + un dossier
   `runs/<id>/` complet (screenshots + logs nommés par scénario).
3. Un FAIL volontairement introduit (ex. compteur manifest faux) est correctement
   classé « MANIFEST STALE » par la skill via `baseline.json`.
4. Un scénario flaky connu (`cas-diff-replacement-note`) est marqué `FLAKY` après
   retry-once, et n'est pas remonté comme vrai bug.
5. `rig-loop bench acquire` puis un second `acquire` depuis une autre invocation est
   refusé tant que le lock n'est pas relâché.
6. Un collègue suit `LOOP.md` seul et complète une boucle sans assistance.

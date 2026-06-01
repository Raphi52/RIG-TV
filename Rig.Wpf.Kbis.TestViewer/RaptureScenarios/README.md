# RaptureScenarios — fixtures embarqués Smoke Import

Dossier embarqué dans le binaire **Rig Testing** (TestViewer WPF), copié dans
`bin\Release\net48\RaptureScenarios\` à chaque build. Suit donc le zip de
distribution — chaque dev qui récupère Rig Testing a immédiatement un set de
scénarios pertinents à jouer.

## Layout

- `manifest.json` : catalogue des scénarios (id, nom, description, JSON de
  référence, audience cible par défaut, cas Orchestrator attendu, compteurs
  attendus pour vérification).
- `cas-{a|b|c}-*.json` : fichiers JSON Rapture utilisés par chaque scénario.
  Format conforme à `RaptureImportParser` (DataContractJsonSerializer,
  `RaptureImportDto`).

## Convention de nommage

```
cas-{caseLetter}-{shortDescription}-{nbAffaires}-affaires.json
```

- `caseLetter` = `a` (match audience), `a-bis` (audience différente), `b` (multi-match), `c` (création)
- `shortDescription` = kebab-case du type d'audience (`contentieux`, `pc-ouvertures`, etc.)
- `nbAffaires` = nombre d'affaires dans le JSON (`10`, `44`, etc.)

## Override côté utilisateur

L'UI TestViewer permet à l'utilisateur de :

1. **Changer le dossier source** : pointer sur un autre dossier (ex.
   `Desktop\JsonRapture\`) si on veut tester ses propres JSONs.
2. **Override l'AUDNC_ID cible** : par défaut le scénario propose un AUDNC_ID
   (cf. `defaultAudienceId` dans `manifest.json`) mais l'utilisateur peut
   pointer ailleurs (utile si la même date+heure matche plusieurs audiences
   ou si on veut tester sur un autre greffe).
3. **Override le JSON** : choisir un autre fichier que celui du manifest pour
   le scénario en cours (ex. tester une variation locale).

## Ajouter un scénario

1. Déposer un nouveau JSON dans ce dossier (format `RaptureImportDto`).
2. Ajouter une entrée dans `manifest.json` :

```json
{
  "id": "cas-erreur-greffe-inconnu",
  "name": "Erreur — Greffe inconnu",
  "description": "Le codeGreffe du JSON ne matche aucun greffe en référentiel — l'Orchestrator doit bloquer avec une erreur header.",
  "jsonFile": "cas-erreur-greffe-inconnu.json",
  "defaultAudienceId": null,
  "expectedCase": "ERROR_HEADER",
  "tags": ["error", "header"]
}
```

3. Rebuild TestViewer — le scénario apparaît dans le combo Smoke Import.

## DB partagée

Les `defaultAudienceId` référencent des audiences de `SQL-DEV\DEV` qui sont
**partagées entre tous les devs** (base de dev commune Amitel). Pas besoin de
setup par machine — les AUDNC_ID 28590 / 28625 / 28644 existent pour tout le
monde.

Si ces audiences sont un jour supprimées ou refactorées, les scénarios qui
référencent leur `defaultAudienceId` deviendront cassés ; il faudra alors :
- Soit recréer une audience équivalente et mettre à jour `defaultAudienceId`
- Soit mettre `defaultAudienceId: null` pour fallback sur le matching par
  date+heure (l'Orchestrator gère ce cas)

## Manquants connus (à générer)

Cf. `Audit/2026-05-20-rapture-failles-ameliorations.md`. Idéalement le repo
contiendra à terme un scénario pour chacune des branches du pipeline :

- `cas-a-bis-audience-differente` (popup "Importer dans l'audience trouvée ?")
- `cas-b-multi-match` (besoin d'un doublon en base)
- `cas-erreur-greffe-inconnu`
- `cas-erreur-date-invalide`
- `cas-erreur-affaire-id-instance-manquant`
- `cas-diff-affaire-non-trouvee` (idInstance bidon dans le JSON)
- `cas-diff-replacement-note` (2 imports successifs — vérif idempotence UPSERT)
- `cas-valid-plumitif-resolu` (vrai code plumitif du référentiel, pas un marker)

# SP0 — Socle de la documentation de reconstruction RIG — Plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Créer le socle de la documentation de reconstruction RIG — arborescence, point d'entrée, matrice de couverture, templates, ordre de reconstruction, critères d'équivalence, runbook — sur le partage `\\ged2\rig\Projets IA\Documentation\AI-Generated\`.

**Architecture:** SP0 ne documente aucun sous-système métier ; il pose la structure et les conventions que les SP1-SP9 rempliront. Les livrables sont des fichiers Markdown + une arborescence de dossiers, écrits sur un partage réseau (hors repo git). La matrice de couverture est pré-remplie par énumération des unités du code source.

**Tech Stack:** Markdown, PowerShell (création d'arborescence + énumération), partage réseau SMB.

**Spec source:** `docs/superpowers/specs/2026-05-21-rig-doc-reconstruction-design.md` (périmètre SP0 = §20).

---

## Contraintes & notes

- **Livrables hors git.** Tout le contenu SP0 est écrit sur `\\ged2\rig\…\AI-Generated\` — un partage réseau, pas le repo. **Aucun `git commit`** ne concerne ces fichiers. Seuls le spec et ce plan sont dans le repo ; ne pas les commiter sans « go » utilisateur (CLAUDE.md règle 6).
- **Pas de TDD classique.** Livrables = fichiers Markdown. La vérification de chaque task = relecture structurée contre des critères d'acceptation explicites. La vérification finale (Task 13) = un agent neuf ouvre `README.md` et sait naviguer + reconstruire.
- **`DEST`** désigne partout `\\ged2\rig\Projets IA\Documentation\AI-Generated`.
- **Encoding** : tous les `.md` en UTF-8 (sans BOM de préférence).
- **Sources réutilisables** (repo `C:\Code RIG\RigApplication-testing\`) : `documentation/architecture.md`, `documentation/modules/*.md`, `AGENTS.md`, `C:\Code RIG\Audit\docs\phase0-final-report.md`, `C:\Code RIG\CLAUDE.md`, `C:\Code RIG\OPERATIONS.md`, et les 8 synthèses d'analyse de code du 2026-05-21 (dans la conversation de design — à recopier depuis le spec et le contexte).

---

## Task 1: Accès au partage + arborescence

**Files:**
- Vérifie/crée : `\\ged2\rig\Projets IA\Documentation\AI-Generated\` et toute son arborescence.

- [ ] **Step 1: Vérifier l'accès en écriture au partage — GATE BLOQUANT**

Créer et exécuter `C:\Code RIG\check-share.ps1` :

```powershell
$share = '\\ged2\rig\Projets IA\Documentation'
if (-not (Test-Path $share)) {
    Write-Host "BLOQUANT : partage injoignable : $share"
    Write-Host "Action requise : monter le partage (net use) ou demander l'acces a l'utilisateur."
    exit 1
}
$dest = Join-Path $share 'AI-Generated'
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
$probe = Join-Path $dest '.write-probe'
try {
    Set-Content -LiteralPath $probe -Value 'ok' -Encoding utf8 -ErrorAction Stop
    Remove-Item -LiteralPath $probe -Force
    Write-Host "ACCES ECRITURE OK : $dest"
} catch {
    Write-Host "BLOQUANT : pas d'acces en ecriture sur $dest"
    exit 1
}
```

Run: `powershell -ExecutionPolicy Bypass -File "C:\Code RIG\check-share.ps1"`
Expected: `ACCES ECRITURE OK`. **Si `BLOQUANT` : STOPPER le plan et demander à l'utilisateur de rendre le partage accessible.** Ne pas continuer.

- [ ] **Step 2: Créer l'arborescence complète**

Créer et exécuter `C:\Code RIG\create-tree.ps1` :

```powershell
$dest = '\\ged2\rig\Projets IA\Documentation\AI-Generated'
$dirs = @(
    'reference\10-build-deploy', 'reference\20-host-plugins', 'reference\30-moteur-graphique',
    'reference\40-modele-donnees', 'reference\40-modele-donnees\familles',
    'reference\50-metier-judiciaire', 'reference\50-metier-judiciaire\workflows', 'reference\50-metier-judiciaire\proc',
    'reference\60-metier-rcs', 'reference\60-metier-rcs\workflows', 'reference\60-metier-rcs\proc',
    'reference\70-edi-integrations', 'reference\80-services-batch', 'reference\90-transverse',
    '95-reglementaire', 'guides', '_templates',
    'artifacts', 'artifacts\sql-ddl', 'artifacts\com-idl', 'artifacts\xsd', 'artifacts\data-config'
)
foreach ($d in $dirs) {
    $p = Join-Path $dest $d
    New-Item -ItemType Directory -Path $p -Force | Out-Null
    Write-Host "cree : $d"
}
```

Run: `powershell -ExecutionPolicy Bypass -File "C:\Code RIG\create-tree.ps1"`
Expected: 22 lignes `cree : …`.

- [ ] **Step 3: Vérifier l'arborescence**

Run: `powershell -ExecutionPolicy Bypass -Command "Get-ChildItem '\\ged2\rig\Projets IA\Documentation\AI-Generated' -Recurse -Directory | Measure-Object | Select-Object -ExpandProperty Count"`
Expected: `23` (les 22 dossiers de la liste + le parent `reference` créé implicitement).

---

## Task 2: Les 3 templates

**Files:**
- Create: `DEST\_templates\fiche-sous-systeme.md`
- Create: `DEST\_templates\fiche-proc.md`
- Create: `DEST\_templates\fiche-famille-tables.md`

- [ ] **Step 1: Écrire `_templates\fiche-sous-systeme.md`**

Contenu exact (repris du spec §11.1) :

```markdown
---
sous-systeme: <NN-nom>
fiche: <slug>
sp: SP<N>
etape-reconstruction: R<n>
statut: vide | esquisse | documenté | reconstruction | vérifié
derniere-analyse: AAAA-MM-JJ
sources: [chemins repo analysés]
---

# <Titre>

## Spécification
> Bloc NORMATIF. Exact, testable, zéro référence non résolue, zéro inconnue.

### Contrat de données
<tables/colonnes/contraintes touchées, ou renvoi artifacts/sql-ddl/.>

### Interfaces
<signatures exactes des API/contrats exposés et consommés.>

### Règles
<chaque règle : « SI <condition> ALORS <effet> ». Exhaustif.>

### Workflows
<machines à états : états, transitions, gardes.>

### Critères d'acceptation
<scénarios concrets entrée→sortie, testables.>

## Explication
> Bloc NON normatif. Aide à comprendre, n'est pas le contrat.

### Rôle et raison d'être
### Emplacement dans le code   <chemins exacts, chemin:ligne>
### Dépendances                <requiert / requis par>
### Artefacts liés             <liens artifacts/>
### Règles réglementaires      <savoir juridique absent du code ; vide si sans objet>
### Pièges                     <gotchas, dette, counter-intuitive>
### Provenance                 <comment ce savoir a été établi>

## Checklist de reconstruction
<liste ordonnée : ce qu'un implémenteur produit pour recréer ce sous-système.>

## Couverture
<statut, et EXPLICITEMENT : ce qui reste inconnu / non couvert.>
```

- [ ] **Step 2: Écrire `_templates\fiche-proc.md`**

Reprendre la structure de `fiche-sous-systeme.md`, en remplaçant le contenu du bloc
`## Spécification` par les sous-sections du spec §11.2 :

```markdown
## Spécification

### Identité
- Code : <CODE>   Type : DLL | EXE | PROCVB6 | EXETAB | FONC | FONCVB6
- Formulaire : RIG.PROCESSUS.FORM_<CODE>   Onglet : tab<CODE>
- Modes / paramètres : <enum exhaustif>

### Étapes et Opérations composées
<séquence ; pour chaque Etape, ses Operations.>

### Écrans
<chaque écran : champs, validations, comportements enable/disable.>

### Workflow
<machine à états du processus.>

### Écritures base
<tables écrites, ordre, conditions.>

### Critères d'acceptation
<scénarios entrée→sortie.>
```

Le reste de la fiche (`## Explication`, `## Checklist de reconstruction`, `## Couverture`,
en-tête YAML) est identique à `fiche-sous-systeme.md`.

- [ ] **Step 3: Écrire `_templates\fiche-famille-tables.md`**

Structure de `fiche-sous-systeme.md`, bloc `## Spécification` remplacé par (spec §11.3) :

```markdown
## Spécification

### Tables
<tableau : table, rôle, volumétrie, lien artifacts/sql-ddl/<table>.sql.>

### Graphe des entités
<ASCII : FK internes + vers autres familles.>

### Table(s) pivot
<hub(s) et pourquoi.>

### Colonnes discriminantes
<colonnes portant une sémantique de routage.>

### Procédures et vues associées
<liens vers procedures-stockees.md / vues.md.>

### Données de référence
<les CODE_* liés → artifacts/data-config/.>
```

- [ ] **Step 4: Vérifier**

Critère d'acceptation : les 3 fichiers existent, chacun a l'en-tête YAML, les titres H2
exacts `## Spécification` et `## Explication`, et la section `## Couverture`. Run :
`powershell -ExecutionPolicy Bypass -Command "Get-ChildItem '\\ged2\rig\Projets IA\Documentation\AI-Generated\_templates' -Filter *.md | Measure-Object | Select-Object -ExpandProperty Count"` → Expected: `3`.

---

## Task 3: Matrice de couverture `01-couverture.md`

**Files:**
- Create: `DEST\01-couverture.md`
- Script: `C:\Code RIG\gen-couverture.ps1`

- [ ] **Step 1: Écrire le script d'énumération des unités**

Créer `C:\Code RIG\gen-couverture.ps1` qui énumère les unités et produit les lignes de la
matrice. Il liste les `PROC_*` (hors `_EXE`), les services, les batchs, et concatène les
familles / flux / fiches transverses connus du spec.

```powershell
$src = 'C:\Code RIG\RigApplication-testing\Source'
$rows = New-Object System.Collections.Generic.List[string]
function Row($unite,$sp,$fiche) { $rows.Add("| $unite | $sp | $fiche | vide | — | — |") }

# PROC_* (hors _EXE), uniques
$proc = @()
foreach ($r in @("$src\RIG\DLL\Processus","$src\RIG\EXE")) {
  if (Test-Path $r) { $proc += Get-ChildItem $r -Directory -Recurse -EA SilentlyContinue |
    Where-Object { $_.Name -like 'PROC_*' -and $_.Name -notlike '*_EXE' } }
}
foreach ($p in ($proc.Name | Sort-Object -Unique)) { Row $p 'SP4/SP5' "reference/50-ou-60/proc/$($p.ToLower()).md" }

# Services
if (Test-Path "$src\Outils\Exe\SERVICES") {
  foreach ($s in (Get-ChildItem "$src\Outils\Exe\SERVICES" -Directory | Select-Object -Expand Name | Sort-Object)) {
    Row $s 'SP7' 'reference/80-services-batch/catalogue-services.md' } }

# Batchs
if (Test-Path "$src\Outils\Exe\Batch") {
  foreach ($b in (Get-ChildItem "$src\Outils\Exe\Batch" -Directory | Select-Object -Expand Name | Sort-Object)) {
    Row $b 'SP7' 'reference/80-services-batch/catalogue-batchs.md' } }

# Familles de tables (liste du spec, SP1)
foreach ($f in @('judiciaire','rcs','edi','facturation','entreprises','editions','cfe',
  'inscription-endettement','bodacc','ged','habilitations','referentiels','workflow-demande',
  'beneficiaires-effectifs','retroconversion')) {
  Row "famille:$f" 'SP1' "reference/40-modele-donnees/familles/$f.md" }

# Fiches transverses (SP9)
foreach ($t in @('facturation','editions-impression','bodacc','crystal-reports',
  'multi-tenant-drom','securite-credentials','logging')) {
  Row "transverse:$t" 'SP9' "reference/90-transverse/$t.md" }

$rows | Set-Content 'C:\Code RIG\couverture-rows.txt' -Encoding utf8
Write-Host "Lignes generees : $($rows.Count)"
```

Run: `powershell -ExecutionPolicy Bypass -File "C:\Code RIG\gen-couverture.ps1"`
Expected: `Lignes generees : <N>` avec N ≈ 350-400.

- [ ] **Step 2: Composer `01-couverture.md`**

Écrire `DEST\01-couverture.md` : un en-tête expliquant le rôle de la matrice + l'énumération
`Statut` (spec §7) + la formule de complétude, puis le tableau Markdown avec l'en-tête de
colonnes `| Unité | SP | Fiche | Statut | Dernière analyse | Reconstruction-review |` et
toutes les lignes de `C:\Code RIG\couverture-rows.txt`. Ajouter en tête une ligne de
synthèse « Complétude globale : 0 % (0 vérifié / N) ».

- [ ] **Step 3: Vérifier**

Critère d'acceptation : `01-couverture.md` contient une ligne par PROC_*/service/batch/
famille/transverse, toutes au statut `vide`, et la synthèse `0 %`. Run :
`powershell -ExecutionPolicy Bypass -Command "(Select-String -Path '\\ged2\rig\Projets IA\Documentation\AI-Generated\01-couverture.md' -Pattern '\| vide \|').Count"` → Expected: = N de l'étape 1.

---

## Task 4: `00-programme.md`

**Files:**
- Create: `DEST\00-programme.md`

- [ ] **Step 1: Rédiger `00-programme.md`**

Sections obligatoires :
1. **Objet** — ce qu'est ce paquet de reconstruction (reprendre spec §1, §2).
2. **L'étalon « 2 prompts »** — reprendre spec §1.1.
3. **Les 4 composantes** du paquet (spec §2).
4. **Le programme 10 SP** — tableau SP0→SP9 (reprendre spec §14 : dossier cible,
   livrables, dépendances) + statut de chaque SP (SP0 = en cours, SP1-9 = à faire).
5. **Ordre d'exécution** des SP (spec §19).
6. **Comment contribuer** — chaque SP fait son cycle brainstorm → spec → plan → exécution ;
   chaque fiche touchée met à jour `derniere-analyse` et `01-couverture.md`.

Sources : spec §1, §2, §14, §19.

- [ ] **Step 2: Vérifier**

Critère d'acceptation : les 6 sections présentes ; le tableau 10 SP complet ; un lecteur
comprend le découpage et l'ordre. Relecture structurée.

---

## Task 5: `02-perimetre-volumetrie.md`

**Files:**
- Create: `DEST\02-perimetre-volumetrie.md`

- [ ] **Step 1: Rédiger `02-perimetre-volumetrie.md`**

Sections obligatoires :
1. **Ce qu'est RIG** — application des greffes des tribunaux de commerce français ;
   mono-poste hosté + services batch + SQL Server (reprendre `architecture.md` §1).
2. **Inventaire chiffré** — ~1443 projets (1158 csproj, 235 VB6, 30 C++, 20 VB.NET),
   899 .sln ; ~247 dossiers PROC_* (≈165 plugins déployables) ; 35 services Windows ;
   57 batchs ; SSDT 779 tables / 204 functions / 110 procs / 61 vues ; RIG_DEV ≈ 822
   tables / ~14900 colonnes ; 6 bases. Source : phase0-final-report + comptages 2026-05-21.
3. **Les 11 greffes et le multi-tenant** — métropole + DROM (Réunion, Antilles, Polynésie).
4. **Périmètre de la doc** — legacy inclus ; migration exclue (reste dans `Audit/`).
5. **Contexte** — applicatif d'État, données RGPD, partenaire Infogreffe.

Sources : `Audit\docs\phase0-final-report.md`, `documentation/architecture.md`, les
synthèses 2026-05-21.

- [ ] **Step 2: Vérifier**

Critère d'acceptation : chiffres cohérents avec phase0 + comptages ; périmètre explicite.
Relecture.

---

## Task 6: `03-architecture-systeme.md`

**Files:**
- Create: `DEST\03-architecture-systeme.md`

- [ ] **Step 1: Rédiger `03-architecture-systeme.md`**

Réutiliser `documentation/architecture.md` comme base, **en corrigeant** les écarts
relevés par l'audit doc du 2026-05-21 :
- MSMQ : signaler qu'il est **mort depuis 2019** (ne pas le présenter comme bus actif).
- Chiffres : utiliser les chiffres précis (pas « ~1400 »).
- Référence `moteur.md` : pointer `Source\CPP\moteur.md` (pas `Source\RIG\DLL\RigAutomate\`).

Sections obligatoires :
1. **Vue d'ensemble** — schéma ASCII (poste utilisateur → host → moteur → RigMetier →
   SQL ; services batch ; pipelines DevOps).
2. **Les couches** — host, plugins, composants partagés (Etapes/Operations), moteur
   graphique, accès données, base, services, build/deploy. Renvoyer vers les dossiers
   `reference/NN-*/`.
3. **Flux types** — démarrage utilisateur, workflow métier dans un Processus, build →
   deploy → GAC, IPC inter-services via `AMIMESSAGE`.
4. **Conventions transversales** — x86, encoding, strong-naming, préfixes de tables.
5. **Caractéristiques structurantes** — build circulaire, plugin pattern, mix UI/métier.

Sources : `documentation/architecture.md`, synthèses 2026-05-21 (topologie, host, moteur,
services).

- [ ] **Step 2: Vérifier**

Critère d'acceptation : les 3 corrections d'audit appliquées ; les 9 couches renvoient
vers `reference/`. Relecture.

---

## Task 7: `04-glossaire.md`

**Files:**
- Create: `DEST\04-glossaire.md`

- [ ] **Step 1: Rédiger `04-glossaire.md`**

Deux tableaux `Terme | Définition` :
1. **Vocabulaire métier** — greffe, audience, cabinet, affaire, instance, plumitif,
   enrôlement, appel d'affaire, numéro de rôle, procédure collective (RJ/LJ/Sauvegarde),
   organes PC (ADJ/MJ/LIQ/JPC), RCS, immatriculation, K-bis, BODACC, dirigeant,
   mandataire, EDI, Infogreffe, CFE.
2. **Vocabulaire technique** — Processus (PROC_*), Etape, Operation, Ult, LeafRigControl,
   RigAutomate, moteur COM, GAC, strong-naming, build circulaire, `MODELE_*`/`M_*`,
   AMIMESSAGE, IFormAccueil, AmiResolver, greffe code, `RegistreEnBD`.

Chaque entrée : 1-2 phrases, renvoi vers la fiche `reference/` détaillée si pertinente.

Sources : `Source\CPP\vocabulary.md`, synthèses métier 2026-05-21, `architecture.md`.

- [ ] **Step 2: Vérifier**

Critère d'acceptation : ≥ 40 termes ; chaque terme métier non trivial renvoie vers une
fiche `reference/`. Relecture.

---

## Task 8: `05-ordre-reconstruction.md`

**Files:**
- Create: `DEST\05-ordre-reconstruction.md`

- [ ] **Step 1: Rédiger `05-ordre-reconstruction.md`**

Contenu :
1. **Le DAG R1→R10** — reprendre le tableau du spec §9 (étape, quoi reconstruire, specs
   consommées, artefacts instanciés).
2. **Justification de l'ordre** — pourquoi les données d'abord, le moteur avant les
   domaines, etc.
3. **DAG fin** — pour chaque étape R\<n\>, lister les dépendances inter-fiches connues à
   ce stade (sera affiné par chaque SP). Pour SP0, donner au moins le niveau étape.
4. **Convention** — chaque fiche déclare son étape via l'en-tête YAML
   `etape-reconstruction: R<n>`.

Sources : spec §9.

- [ ] **Step 2: Vérifier**

Critère d'acceptation : le tableau R1→R10 complet ; un agent comprend dans quel ordre
consommer les specs. Relecture.

---

## Task 9: `06-criteres-equivalence.md`

**Files:**
- Create: `DEST\06-criteres-equivalence.md`

- [ ] **Step 1: Rédiger `06-criteres-equivalence.md`**

Contenu (reprendre spec §10) :
1. **Définition de « fonctionnellement équivalent »** — les 5 points du spec §10.
2. **Preuve par caractérisation** — golden files, capture du comportement legacy, replay.
3. **L'oracle = le harnais RIG Testing** — décrire `Rig.Wpf.Kbis.SmokeRunner`, les xUnit
   `Rig.Rapture.Tests`, les golden files ; le harnais fait partie du paquet de
   reconstruction et le RIG reconstruit doit passer les mêmes scénarios.
4. **Scénarios d'acceptation par fiche** — convention : chaque bloc `## Spécification` se
   termine par « Critères d'acceptation » (exemples entrée→sortie testables).
5. **Définition de fini** — `01-couverture.md` à 100 % `vérifié` côté doc ; harnais de
   caractérisation vert côté reconstruction.

Sources : spec §10, et le contexte du harnais RIG Testing (smoke runner, xUnit).

- [ ] **Step 2: Vérifier**

Critère d'acceptation : les 5 points présents ; le rôle d'oracle du harnais explicite.
Relecture.

---

## Task 10: `guides/reconstruire-rig.md` (runbook) + placeholders guides

**Files:**
- Create: `DEST\guides\reconstruire-rig.md`
- Create: `DEST\guides\modifier-un-onglet.md`, `ajouter-un-processus.md`, `toucher-la-base.md`, `debugger-un-service.md`, `builder-deployer.md` (placeholders)

- [ ] **Step 1: Rédiger `guides\reconstruire-rig.md` (le runbook)**

Contenu = la procédure exécutable de « Prompt 2 » (spec §16), en 5 étapes :
1. **Isoler les specs** — parcourir l'arborescence dans l'ordre de
   `05-ordre-reconstruction.md`, extraire chaque bloc `## Spécification`.
2. **Provisionner les données** — instancier les 6 bases depuis `artifacts/sql-ddl/`,
   charger `artifacts/data-config/`.
3. **Reconstruire couche par couche** — pour chaque étape R1→R9, implémenter selon les
   blocs `## Spécification`, vérifier chaque unité contre ses « Critères d'acceptation ».
4. **Prouver l'équivalence** — étape R10 : exécuter le harnais RIG Testing.
5. **Définition de fini** — couverture 100 % `vérifié` + harnais vert.

- [ ] **Step 2: Créer les 5 guides placeholder**

Chaque fichier `guides\<nom>.md` : en-tête YAML minimal + titre + une phrase
« Guide à rédiger par le SP correspondant — voir `00-programme.md`. » Ces guides seront
remplis par les SP de couche concernés.

- [ ] **Step 3: Vérifier**

Critère d'acceptation : `reconstruire-rig.md` contient les 5 étapes ; 6 fichiers dans
`guides/`. Run : `powershell -ExecutionPolicy Bypass -Command "Get-ChildItem '\\ged2\rig\Projets IA\Documentation\AI-Generated\guides' -Filter *.md | Measure-Object | Select-Object -ExpandProperty Count"` → Expected: `6`.

---

## Task 11: `artifacts/README.md`, `95-reglementaire/index.md`, index `reference/`

**Files:**
- Create: `DEST\artifacts\README.md`
- Create: `DEST\95-reglementaire\index.md`
- Create: `DEST\reference\10-build-deploy\index.md` … `DEST\reference\90-transverse\index.md` (9 fichiers)

- [ ] **Step 1: Rédiger `artifacts\README.md` (manifeste)**

Contenu : rôle de `artifacts/` (les sources de vérité brutes embarquées) ; un tableau
`Lot | Source | Date | Procédure de rafraîchissement` avec les 4 lots `sql-ddl`,
`com-idl`, `xsd`, `data-config` (spec §8) — colonnes Date vides (rempli par SP1/SP3/SP6) ;
la note ⚠ sur `data-config` (extraction SQL, autorisation par tour, cible `RIG_REFERENCE`).

- [ ] **Step 2: Rédiger `95-reglementaire\index.md`**

Contenu : rôle de la couche réglementaire (le savoir juridique absent du code) ; les 3
fiches prévues (`contexte-juridique`, `codes-evenements`, `variations-par-greffe`) listées
comme « à rédiger par les SP de domaine » ; la convention : chaque fiche de domaine porte
une section `### Règles réglementaires` inline (spec §13).

- [ ] **Step 3: Créer les 9 `index.md` de `reference/`**

Pour chacun des 9 dossiers `reference/NN-*/`, créer un `index.md` : en-tête YAML
(`statut: vide`), titre du sous-système, une phrase de rôle, et une table « Fiches »
listant les fiches prévues (depuis le spec §6) au statut `vide`. Contenu minimal mais
structurant — ces index seront étoffés par les SP1-SP9.

- [ ] **Step 4: Vérifier**

Run : `powershell -ExecutionPolicy Bypass -Command "(Get-ChildItem '\\ged2\rig\Projets IA\Documentation\AI-Generated\reference' -Recurse -Filter index.md).Count"` → Expected: `9`.
`artifacts\README.md` et `95-reglementaire\index.md` existent.

---

## Task 12: `README.md` — le point d'entrée

**Files:**
- Create: `DEST\README.md`

À faire en avant-dernier : le README indexe tout le reste.

- [ ] **Step 1: Rédiger `README.md`**

Quatre sections (spec §15), ≤ 150 lignes :
1. **Comment utiliser ce paquet** — les 4 composantes (doc / artefacts / couverture /
   équivalence), la règle de lecture progressive, pointeur vers `guides/reconstruire-rig.md`.
2. **Index tâches** — table « j'ai besoin de… → fiche(s)/guide » : modifier un onglet,
   ajouter un processus, comprendre une table, débugger un service, builder/déployer,
   reconstruire RIG.
3. **Index couches** — table des 9 dossiers `reference/` + `95-reglementaire/`, chacun
   avec un lien vers son `index.md` et un `% couverture` (lu depuis `01-couverture.md` ;
   à ce stade 0 %).
4. **Reconstruire RIG** — pointeur explicite vers `guides/reconstruire-rig.md` et
   `05-ordre-reconstruction.md`.

- [ ] **Step 2: Vérifier**

Critère d'acceptation : les 4 sections présentes ; tous les liens internes pointent vers
des fichiers existants. Run un check de liens :
`powershell -ExecutionPolicy Bypass -Command "$d='\\ged2\rig\Projets IA\Documentation\AI-Generated'; Select-String -Path \"$d\README.md\" -Pattern '\]\(([^)]+\.md)\)' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { $rel=$_.Groups[1].Value; $p=Join-Path $d $rel; if(-not (Test-Path $p)){ Write-Host \"LIEN CASSE : $rel\" } }"`
Expected: aucune ligne `LIEN CASSE`.

---

## Task 13: Vérification SP0 (CLAUDE.md règle 15)

**Files:** aucun — vérification end-to-end.

- [ ] **Step 1: Inventaire des livrables**

Run : `powershell -ExecutionPolicy Bypass -Command "Get-ChildItem '\\ged2\rig\Projets IA\Documentation\AI-Generated' -Recurse -File -Filter *.md | Measure-Object | Select-Object -ExpandProperty Count"`
Expected : `28` fichiers `.md` — 8 racine (README + `00-programme` → `06-criteres-equivalence`)
+ 3 templates + 6 guides + 9 index `reference/` + `artifacts/README.md` + `95-reglementaire/index.md`.

- [ ] **Step 2: Test « agent neuf »**

Dispatcher un sous-agent (model sonnet) à qui on donne **uniquement** le chemin
`\\ged2\rig\Projets IA\Documentation\AI-Generated\README.md` et la consigne : « ouvre ce
README, et dis-moi : (a) où je trouve l'info pour modifier un onglet ; (b) comment on
reconstruit RIG ; (c) quel est l'état d'avancement de la doc. Réponds en < 200 mots. »
Expected : l'agent répond correctement aux 3 questions en ne lisant que le README (+ les
liens qu'il choisit de suivre). Si l'agent doit deviner ou ne trouve pas → corriger le
README et reboucler.

- [ ] **Step 3: Cohérence structurelle**

Vérifier : les 3 templates imposent `## Spécification` / `## Explication` ;
`01-couverture.md` a toutes les unités au statut `vide` ; `05-ordre-reconstruction.md`
donne le DAG R1→R10 ; `guides/reconstruire-rig.md` donne les 5 étapes. Relecture finale.

- [ ] **Step 4: Bilan**

Écrire un court bilan SP0 (dans la réponse à l'utilisateur, pas un fichier) : livrables
produits, ce qui est prêt pour SP1, et confirmer que SP1 (modèle de données) peut démarrer
— il a sa structure cible (`reference/40-modele-donnees/`), son template, sa ligne dans
la matrice.

---

## Notes d'exécution

- **Aucun `git commit`** : tous les livrables sont sur le partage réseau. Le seul commit
  envisageable serait spec + plan dans le repo — à ne faire que sur « go » explicite.
- **Scripts jetables** (`check-share.ps1`, `create-tree.ps1`, `gen-couverture.ps1`) :
  créés dans `C:\Code RIG\`, supprimables après SP0.
- **Si le partage tombe en cours** : les fichiers déjà écrits restent ; reprendre à la
  task interrompue après remontage.

# Documentation de reconstruction RIG — Design

**Date :** 2026-05-21
**Statut :** design complet, en attente de relecture utilisateur
**Destination des livrables :** `\\ged2\rig\Projets IA\Documentation\AI-Generated\`

---

## 1. Objectif et étalon de qualité

Produire la documentation du **legacy RIG** (application des greffes des tribunaux de
commerce français) à un niveau tel qu'un agent Claude puisse **rebâtir un RIG
fonctionnellement équivalent** à partir de ce seul livrable.

### 1.1 L'étalon « 2 prompts »

La qualité visée est définie par un test mental :

> **Prompt 1 — « Isole les specs ».** Un agent parcourt le livrable et en extrait la
> spécification normative complète et ordonnée.
> **Prompt 2 — « Reconstruis RIG ».** Un agent consomme cette spécification et produit
> un RIG fonctionnellement équivalent.

Cet étalon n'est pas une promesse d'exécution littérale en deux invocations (la
reconstruction réelle se découpera en nombreuses sous-tâches) — c'est une **exigence sur
le livrable** : la documentation doit être assez complète, normative et ordonnée pour que
la reconstruction soit un acte de **consommation** et non de **recherche**. Conséquence
directe et non négociable : **zéro investigation du legacy ne doit plus être nécessaire**
une fois le livrable terminé. Tout ce qu'un implémenteur pourrait avoir besoin de
chercher dans le code source RIG est, soit dans la documentation, soit dans les artefacts
embarqués.

### 1.2 Définition opérationnelle de « reconstruction-grade »

Une fiche est reconstruction-grade si un agent compétent, disposant **uniquement de cette
fiche et des artefacts qu'elle référence** (aucun accès au code source RIG), peut produire
une implémentation fonctionnellement équivalente de l'unité décrite **et** la prouver
équivalente via les critères d'acceptation de la fiche. C'est un critère **testable**
(cf. §17, reconstruction-review).

---

## 2. Le livrable : un paquet de reconstruction

Une documentation en prose seule est *lossy* : elle ne peut pas, à elle seule, restituer
un legacy de ~1443 projets. Le livrable est donc un **paquet de reconstruction** à
**quatre composantes indissociables** :

1. **Spécification** — le contenu *normatif* des fiches : contrats de données,
   règles métier exprimées comme assertions testables, signatures d'interface, workflows
   comme machines à états, critères d'acceptation. C'est ce que « Prompt 1 » isole.
2. **Explication** — le contenu *non normatif* des fiches : le pourquoi, l'histoire, le
   savoir réglementaire, les pièges, la provenance dans le code. Aide l'implémenteur,
   n'est pas le contrat.
3. **Artefacts préservés** — les sources de vérité mécaniquement exploitables, embarquées
   telles quelles : DDL SQL (779 fichiers), IDL COM, schémas XSD, et **exports de données
   de configuration** (`MODELE_*`/`M_*`, référentiels `CODE_*`). Justification : une
   grande partie de RIG n'est pas du code mais de la **donnée** — le moteur « ne lit que
   les tables compilées `M_*` » ; les 165 processus sont définis dans les tables
   `MODELE_*`. Sans ces exports, aucune reconstruction fonctionnelle.
4. **Couverture & équivalence** — `01-couverture.md` (statut de complétude par unité) et
   `06-criteres-equivalence.md` (comment prouver l'équivalence). Rendent « complet » et
   « équivalent » **mesurables**.

---

## 3. Spécification normative vs explication — comment « isoler les specs »

C'est le pivot du design. **Chaque fiche est écrite en deux couches clairement
délimitées** :

### 3.1 La couche normative — `## Spécification`

Sous un titre H2 stable `## Spécification`. Contenu : **exact, testable, buildable**.
Règles de fer :

- **Aucune référence non résolue.** Tout type, code, règle, table mentionné est défini
  dans ce bloc, dans le bloc normatif d'une autre fiche (cross-ref par chemin stable), ou
  dans un artefact. Jamais « voir le code », jamais « à peu près ».
- **Aucune inconnue.** Si un point n'est pas élucidé, il n'apparaît **pas** dans le bloc
  normatif — il est déclaré dans `## Couverture` comme lacune. Le bloc normatif ne
  contient que du certain.
- **Règles comme assertions** — format « SI \<condition\> ALORS \<effet\> » ou
  équivalent, directement transposable en test.
- **Workflows comme machines à états** — états, transitions, gardes explicites.
- **Critères d'acceptation inclus** — cf. §10.

### 3.2 La couche explicative — `## Explication`

Sous `## Explication`. Le pourquoi, l'histoire, la dette, les pièges, la provenance
(`chemin:ligne`), l'héritage de migration. Non contractuel.

### 3.3 L'extraction (« Prompt 1 »)

Parce que tous les blocs `## Spécification` sont **auto-suffisants et ordonnés** (§9),
« Isole les specs » est une opération **mécanique** : parcourir l'arborescence dans
l'ordre de `05-ordre-reconstruction.md`, concaténer chaque bloc `## Spécification` →
spécification consolidée et cohérente. La discipline « zéro référence non résolue »
garantit que cette concaténation se tient seule.

---

## 4. Périmètre

**Inclus :** le legacy RIG tel qu'il existe (architecture, données, moteur, host,
domaines métier, services, build/deploy, transverse, réglementaire).

**Exclu :** la migration cible (apification, nouveau front) — reste dans `Audit/`. Les
ADR de migration sont référencés en annexe « contexte » seulement.

**Volumétrie cadrée par l'analyse de code** (8 sous-agents, 2026-05-21) : ~1443 projets
(1158 csproj, 235 VB6, 30 C++, 20 VB.NET) ; ~165 PROC_* ; ~23 services Windows ;
57 batchs ; projet SSDT de 779 tables + 204 functions + 110 procédures + 61 vues ;
RIG_DEV ≈ 822 tables / ~14900 colonnes ; 6 bases ; 11 greffes dont DROM.

---

## 5. Public et principes de rédaction

**Public unique : des agents Claude.** Conséquences :

- **Dense, zéro remplissage** — pas d'introduction marketing, pas de redites.
- **Chemins exacts partout** — `chemin/fichier.cs:ligne` pour tout point d'entrée code.
- **Tableaux** pour toute donnée structurée.
- **Divulgation progressive** — chaque `index.md` court ; un agent ne charge que sa fiche.
- **Fiches auto-portantes mais réticulées** — chaque fiche se comprend seule, cross-linke
  ses dépendances par chemins stables.
- **Langue : français** (langue de la codebase et du métier).
- **En-tête méta YAML obligatoire** sur chaque fiche (§12).
- **Séparation normatif / explicatif** stricte (§3) — c'est ce qui rend le « 2 prompts »
  possible.

---

## 6. Arborescence complète

```
AI-Generated/
├── README.md                        # POINT D'ENTRÉE — la map (index tâches + index couches)
├── 00-programme.md                  # le programme 10 SP, philosophie paquet de reconstruction, statut
├── 01-couverture.md                 # MATRICE DE COUVERTURE — toutes les unités + statut
├── 02-perimetre-volumetrie.md       # ce qu'est RIG, inventaire chiffré, 11 greffes, contexte
├── 03-architecture-systeme.md       # vue d'ensemble : schéma, couches, flux de données, conventions
├── 04-glossaire.md                  # vocabulaire métier + technique
├── 05-ordre-reconstruction.md       # le DAG : ordre topologique de reconstruction (cible de Prompt 2)
├── 06-criteres-equivalence.md       # comment prouver le RIG reconstruit ≡ original
│
├── reference/                       # CORPS DE RÉFÉRENCE — un dossier par couche
│   ├── 10-build-deploy/             # chaine-build, gac-strong-naming, build-circulaire, deploy-runtime, devops
│   ├── 20-host-plugins/             # bootstrap, types-de-plugins, chargement, iformaccueil, config-registre, habilitations
│   ├── 30-moteur-graphique/         # rigautomate-net, moteur-com-cpp, dll-vb6, modele-vers-runtime, catalogue-ult, cycle-de-vie
│   ├── 40-modele-donnees/           # bases-et-roles, conventions-nommage, familles/*, procedures-stockees, vues, donnees-config, pii-rgpd
│   ├── 50-metier-judiciaire/        # concepts, regles-metier, plumitif, workflows/*, proc/* (1 fiche par PROC_*)
│   ├── 60-metier-rcs/               # concepts, regles-metier, kbis, workflows/*, proc/*
│   ├── 70-edi-integrations/         # architecture-edi, catalogue-flux, infogreffe, formats-xsd, archivage-annuel
│   ├── 80-services-batch/           # catalogue-services, amimessage-bus, catalogue-batchs, ordonnancement, supervision
│   └── 90-transverse/               # facturation, editions-impression, bodacc, crystal-reports, multi-tenant-drom, securite-credentials, logging
│
├── 95-reglementaire/                # SAVOIR IMPLICITE — n'existe dans aucun code
│   └── contexte-juridique, codes-evenements, variations-par-greffe
│
├── guides/                          # « COMMENT FAIRE X » — cible de l'index tâches
│   ├── reconstruire-rig.md          # LE RUNBOOK — la procédure exécutable de Prompt 2 (§16)
│   ├── modifier-un-onglet.md  ajouter-un-processus.md  toucher-la-base.md
│   └── debugger-un-service.md  builder-deployer.md
│
├── artifacts/                       # ARTEFACTS PRÉSERVÉS — sources de vérité brutes
│   ├── README.md                    # manifeste : provenance, date, procédure de rafraîchissement
│   ├── sql-ddl/                     # les 779 .sql (copie de Source\BaseDeDonnées\RigDatabase\dbo\)
│   ├── com-idl/                     # RIG_ServLoc_Metier.idl et autres IDL
│   ├── xsd/                         # schémas XSD Infogreffe / EDI / CFE
│   └── data-config/                 # exports CSV/SQL : MODELE_*, M_*, référentiels CODE_*
│
└── _templates/
    ├── fiche-sous-systeme.md   fiche-proc.md   fiche-famille-tables.md
```

Chaque dossier `reference/NN-*/` contient un `index.md` (vue du sous-système + table des
fiches) + N fiches de détail. Raison : à profondeur reconstruction, un seul `.md` par
sous-système serait ingérable.

---

## 7. Le modèle de couverture — `01-couverture.md`

Matrice unique, colonne vertébrale du programme. Une ligne par unité documentable ;
pré-remplie en SP0 avec **toutes les unités connues** (165 PROC_*, ~23 services, 57
batchs, ~15 familles de tables, flux EDI, fiches transverses).

Colonnes : `Unité` · `SP` · `Fiche` · `Statut` · `Dernière analyse` · `Reconstruction-review`.

**Énumération `Statut`** (progression) :

| Statut | Sens |
|---|---|
| `vide` | Pas démarré |
| `esquisse` | Squelette / notes, incomplet |
| `documenté` | Décrit, mais bloc normatif pas encore complet/vérifié |
| `reconstruction` | Bloc normatif complet, jugé suffisant pour reconstruire |
| `vérifié` | A passé la reconstruction-review aveugle (§17) |

`01-couverture.md` affiche un **% de complétude par SP et global** (`vérifié` / total).
La doc est complète quand la matrice atteint **100 % `vérifié`**.

---

## 8. Stratégie d'artefacts

`artifacts/` embarque ce qui est mécaniquement exploitable, avec un **manifeste**
(`artifacts/README.md`) : pour chaque lot, source exacte, date d'extraction,
commande/procédure de rafraîchissement.

| Lot | Source | Mode d'obtention |
|---|---|---|
| `sql-ddl/` | `Source\BaseDeDonnées\RigDatabase\dbo\` | Copie de fichiers (repo) |
| `com-idl/` | `Source\CPP\…\RIG_ServLoc_Metier.idl` et voisins | Copie de fichiers (repo) |
| `xsd/` | `Source\Edi\DLL\EdiClassXSD\` | Copie de fichiers (repo) |
| `data-config/` | Bases SQL (`RIG_REFERENCE` — stable) | **Extraction SQL** |

⚠ **`data-config/` requiert un accès SQL en lecture.** L'extraction du contenu des tables
`MODELE_*`/`M_*` et des référentiels `CODE_*` se fait pendant SP1, **avec autorisation
utilisateur par tour** (mémoire `feedback_no_prod_access.md`). Cible : `RIG_REFERENCE`.
Sortie : un CSV (ou script `INSERT`) par table, plus `donnees-config.md` expliquant quelles
tables sont du « code déguisé en données ».

---

## 9. Ordre de reconstruction — `05-ordre-reconstruction.md`

Le **DAG topologique** de reconstruction de RIG. C'est ce que « Prompt 2 » consomme : il
transforme « rebâtir 1443 projets » en une **séquence ordonnée de consommation de specs**.

| Étape | Quoi reconstruire | Specs consommées | Artefacts instanciés |
|---|---|---|---|
| R1 | **Infrastructure données** — les 6 bases, schéma + données de référence | `reference/40-*` | `artifacts/sql-ddl/`, `artifacts/data-config/` |
| R2 | **Socle technique** — accès données, config/registre, log, utilitaires | `reference/20-*` (config), `reference/90-*` (logging) | — |
| R3 | **Moteur graphique** — RigAutomate, compilation MODELE_*→M_*, catalogue ULT | `reference/30-*` | `artifacts/com-idl/`, `artifacts/data-config/` (MODELE_*) |
| R4 | **Host & plugins** — RigClientAccueil, chargement plugins, IFormAccueil | `reference/20-*` | — |
| R5 | **Domaines métier** — judiciaire, RCS, chaque PROC_* | `reference/50-*`, `reference/60-*` | — |
| R6 | **EDI & intégrations** | `reference/70-*` | `artifacts/xsd/` |
| R7 | **Services & batch** — backbone asynchrone | `reference/80-*` | — |
| R8 | **Transverse** — facturation, éditions, Crystal, multi-tenant | `reference/90-*` | — |
| R9 | **Build & déploiement** | `reference/10-*` | — |
| R10 | **Vérification d'équivalence** | `06-criteres-equivalence.md` | harnais RIG Testing |

Chaque fiche déclare en en-tête son étape R\<n\>. `05-ordre-reconstruction.md` donne aussi
le DAG fin (dépendances inter-fiches) pour paralléliser à l'intérieur d'une étape.

---

## 10. Critères d'équivalence — `06-criteres-equivalence.md`

« Fonctionnellement équivalent » est défini, mesurable, prouvable :

1. **Contrats de données identiques** — le schéma est *littéralement réutilisé* depuis
   `artifacts/sql-ddl/` → identité triviale.
2. **Sorties identiques** — à entrées égales, le RIG reconstruit produit les mêmes
   documents (plumitif, KBIS, messages EDI, factures) et les mêmes écritures base.
3. **Preuve par caractérisation** (Feathers, _Working Effectively with Legacy Code_) — le
   comportement du legacy est capturé en **golden files** ; rejoué contre la
   reconstruction ; diffé. Tout écart non toléré = échec.
4. **Oracle = le harnais RIG Testing** — le harnais existant (`Rig.Wpf.Kbis.SmokeRunner`,
   xUnit `Rig.Rapture.Tests`, golden files) est **référencé comme partie du paquet de
   reconstruction**. Le RIG reconstruit doit passer les mêmes scénarios.
5. **Scénarios d'acceptation par fiche** — chaque bloc `## Spécification` se termine par
   une sous-section **« Critères d'acceptation »** : des exemples concrets entrée→sortie,
   qui servent à la fois d'illustration normative et de test d'acceptation. Une unité est
   reconstruite quand ses critères d'acceptation passent.

`06-criteres-equivalence.md` (produit en SP0) définit ce cadre ; chaque SP de domaine
remplit les scénarios d'acceptation de ses fiches.

---

## 11. Les templates

### 11.1 `_templates/fiche-sous-systeme.md`

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

### 11.2 `_templates/fiche-proc.md`

Une fiche par PROC_* (≈165). En-tête + mêmes blocs, avec dans `## Spécification` :

```markdown
### Identité
- Code : <CODE>   Type : DLL | EXE | PROCVB6 | EXETAB | FONC | FONCVB6
- Formulaire : RIG.PROCESSUS.FORM_<CODE>   Onglet : tab<CODE>
- Modes / paramètres : <enum exhaustif>
### Étapes et Opérations composées   <séquence ; pour chaque Etape, ses Operations>
### Écrans                            <chaque écran : champs, validations, enable/disable>
### Workflow                          <machine à états du processus>
### Écritures base                    <tables écrites, ordre, conditions>
### Critères d'acceptation            <scénarios entrée→sortie>
```

### 11.3 `_templates/fiche-famille-tables.md`

Une fiche par famille de tables (~15). En-tête + dans `## Spécification` :

```markdown
### Tables                <tableau : table, rôle, volumétrie, lien artifacts/sql-ddl/>
### Graphe des entités    <ASCII : FK internes + vers autres familles>
### Table(s) pivot        <hub(s) et pourquoi>
### Colonnes discriminantes  <colonnes portant une sémantique de routage>
### Procédures et vues    <liens vers procedures-stockees.md / vues.md>
### Données de référence  <les CODE_* liés → artifacts/data-config/>
```

---

## 12. Conventions de fichier

- **En-tête méta YAML** obligatoire — dont `etape-reconstruction: R<n>` (pour l'ordre) et
  `statut` (pour la couverture).
- **Nommage** : dossiers `NN-nom-kebab`, fiches `nom-kebab.md`, un `index.md` par dossier
  `reference/`.
- **Liens internes** relatifs, vers des chemins numérotés stables.
- **Une fiche = une unité** — jamais de fiche fourre-tout.
- **Taille** : > ~400 lignes → scinder en sous-dossier.
- **Pas de duplication** — un fait vit à un seul endroit, les autres linkent.
- **`## Spécification` et `## Explication`** sont des titres H2 **exacts et obligatoires**
  (l'extraction « Prompt 1 » en dépend).

---

## 13. La couche réglementaire (savoir implicite)

Le savoir juridique/métier (droit commercial, sens des codes, raisons des variantes par
greffe) n'existe dans aucun code. Capté de deux façons :

1. **Inline** — section `### Règles réglementaires` obligatoire dans le bloc `## Explication`
   de chaque fiche de domaine.
2. **Consolidé** — `95-reglementaire/` indexe et regroupe ce savoir transverse.

`95-reglementaire/index.md` est créé en SP0, alimenté par chaque SP de domaine.

---

## 14. Les 10 sous-projets

Chaque SP = un cycle brainstorm → spec → plan → exécution autonome.

| SP | Dossier cible | Livrables principaux | Dépend de |
|---|---|---|---|
| **SP0** Socle | racine, `_templates/`, `artifacts/` (scaffolding) | Arborescence, README map, 00-programme, 01-couverture (pré-remplie), 02-périmètre, 03-architecture, 04-glossaire, **05-ordre-reconstruction**, **06-criteres-equivalence**, 3 templates, manifeste artefacts, `95-reglementaire/index.md`, **`guides/reconstruire-rig.md` (runbook)** | — |
| **SP1** Modèle de données | `reference/40-*`, `artifacts/sql-ddl/`, `artifacts/data-config/` | bases-et-roles, conventions-nommage, ~15 fiches `familles/`, procedures-stockees, vues, donnees-config, pii-rgpd ; copie DDL + exports SQL | SP0 |
| **SP2** Host & plugins | `reference/20-*` | bootstrap, types-de-plugins, chargement, iformaccueil, config-registre, habilitations | SP0, SP1 |
| **SP3** Moteur graphique | `reference/30-*`, `artifacts/com-idl/` | rigautomate-net, moteur-com-cpp, dll-vb6, modele-vers-runtime, catalogue-ult, cycle-de-vie ; copie IDL | SP0, SP1 |
| **SP4** Métier judiciaire | `reference/50-*` | concepts, regles-metier, plumitif, workflows/, **une fiche `proc/` par PROC_* judiciaire** | SP0, SP1, SP3 |
| **SP5** RCS | `reference/60-*` | concepts, regles-metier, kbis, workflows/, fiches `proc/` | SP0, SP1, SP3 |
| **SP6** EDI & intégrations | `reference/70-*`, `artifacts/xsd/` | architecture-edi, catalogue-flux, infogreffe, formats-xsd, archivage-annuel ; copie XSD | SP0, SP1 |
| **SP7** Services & batch | `reference/80-*` | catalogue-services, amimessage-bus, catalogue-batchs, ordonnancement, supervision | SP0, SP1 |
| **SP8** Build / deploy / infra | `reference/10-*` | chaine-build, gac-strong-naming, build-circulaire, deploy-runtime, devops-pipelines | SP0 |
| **SP9** Transverse | `reference/90-*`, alimente `95-reglementaire/` | facturation, editions-impression, bodacc, crystal-reports, multi-tenant-drom, securite-credentials, logging | SP0, SP1, SP3, SP7 |

`guides/` (index tâches) et les **scénarios d'acceptation** sont amorcés en SP0 puis
complétés par chaque SP.

---

## 15. Le point d'entrée — `README.md`

Court (≤ 150 lignes). Quatre sections : (1) **Comment utiliser ce paquet** — les 4
composantes, la lecture progressive, le pointeur vers `guides/reconstruire-rig.md` ; (2)
**Index tâches** — table « j'ai besoin de… → fiche(s) » ; (3) **Index couches** — les 9
dossiers `reference/` + `95-reglementaire/` avec leur `% couverture` ; (4) **Reconstruire
RIG** — pointeur explicite vers le runbook et l'ordre de reconstruction.

---

## 16. Le runbook — `guides/reconstruire-rig.md`

C'est la cible littérale de « Prompt 2 ». Procédure exécutable :

1. **Isoler les specs** — parcourir l'arborescence dans l'ordre de
   `05-ordre-reconstruction.md`, extraire chaque bloc `## Spécification` → spécification
   consolidée.
2. **Provisionner les données** — instancier les 6 bases depuis `artifacts/sql-ddl/`,
   charger `artifacts/data-config/`.
3. **Reconstruire couche par couche** — pour chaque étape R1→R9 de
   `05-ordre-reconstruction.md` : implémenter selon les blocs `## Spécification`, vérifier
   chaque unité contre ses « Critères d'acceptation ».
4. **Prouver l'équivalence** — étape R10 : exécuter le harnais RIG Testing contre la
   reconstruction ; tout écart non toléré (hors `06-criteres-equivalence.md`) = non-fini.
5. **Définition de fini** — `01-couverture.md` à 100 % `vérifié` côté doc ; harnais de
   caractérisation vert côté reconstruction.

---

## 17. Vérification — prouver que le livrable tient l'étalon « 2 prompts »

Trois gates, du plus fin au plus large :

1. **Self-review à l'écriture** — bloc `## Spécification` sans référence non résolue ni
   inconnue ; `## Couverture` liste honnêtement les lacunes ; tous les chemins cités
   existent ; critères d'acceptation présents.
2. **Reconstruction-review (par fiche)** — un **sous-agent frais** reçoit **uniquement la
   fiche + ses artefacts liés** (aucun accès au code RIG) et doit répondre : « peux-tu
   spécifier une réimplémentation fonctionnellement équivalente, et exécuter mentalement
   les critères d'acceptation ? Qu'est-ce qui manque ? ». Les manques remontent dans la
   fiche. Statut `vérifié` seulement après une review sans manque bloquant.
3. **Dry-run de reconstruction de bout en bout** — avant de déclarer le programme
   complet, un agent exécute réellement « Prompt 1 + Prompt 2 » **sur une tranche
   représentative** (recommandé : le domaine **KBIS**, déjà couvert par un POC .NET 8 et
   par le harnais de tests) : isoler les specs de la tranche, reconstruire, prouver
   l'équivalence via le harnais. Tout point ayant nécessité de rouvrir le code legacy est
   une lacune du livrable à corriger. C'est la preuve empirique que l'étalon est tenu.

Vérification programme : `01-couverture.md` à 100 % `vérifié` **et** dry-run KBIS réussi
sans réouverture du legacy.

---

## 18. Risques et mitigations

| Risque | Mitigation |
|---|---|
| « Complet » non mesurable | Matrice `01-couverture.md`, statut par unité, % global, gate 100 % `vérifié`. |
| Prose lossy | `artifacts/` embarque DDL/IDL/XSD bruts ; la prose ne les paraphrase pas. |
| Comportement piloté par données | `artifacts/data-config/` exporte `MODELE_*`/`M_*` + référentiels. |
| Specs avec trous invisibles | Discipline « zéro référence non résolue / zéro inconnue » dans `## Spécification` ; reconstruction-review aveugle. |
| Volume du legacy (165 PROC_*, ULTs) | 10 SP, une fiche par unité, matrice de couverture, dry-run sur tranche. |
| « 2 prompts » irréaliste pris au pied de la lettre | Étalon = exigence sur le livrable (zéro recherche résiduelle), pas une contrainte d'exécution littérale. |
| Doc qui dérive du code | En-tête `derniere-analyse` ; chaque SP touchée rafraîchit la fiche. |
| Savoir réglementaire absent du code | Section `### Règles réglementaires` obligatoire + `95-reglementaire/`. |
| Accès SQL pour `data-config` | Extraction en SP1, autorisation par tour, cible `RIG_REFERENCE`. |
| Écriture sur `\\ged2\` | Confirmer l'accès en écriture en début de SP0. |
| Incohérences doc existante (MSMQ mort, chemins faux) | Corrigées en réutilisant ; l'audit doc 2026-05-21 liste les corrections. |
| Équivalence non prouvable | `06-criteres-equivalence.md` + harnais RIG Testing comme oracle de caractérisation. |

---

## 19. Ordre d'exécution des SP

SP0 d'abord (structure + conventions + matrice + ordre + équivalence + runbook). Puis
**SP1** (fondation données). Puis SP8 (build) et SP2 (host) parallélisables. Puis SP3
(moteur). Puis les domaines SP4/SP5/SP6/SP7. SP9 en dernier. `95-reglementaire/` alimenté
en continu. Chaque SP fait son propre cycle brainstorm → spec → plan → exécution.

---

## 20. Ce que SP0 produit concrètement (livrable de la prochaine étape)

Le plan d'implémentation à écrire ensuite (writing-plans) ne couvre que **SP0** :

1. Vérifier l'accès en écriture à `\\ged2\rig\Projets IA\Documentation\AI-Generated\`.
2. Créer l'arborescence complète (§6), avec un `index.md` placeholder par dossier
   `reference/`.
3. Rédiger `README.md` (la map, §15).
4. Rédiger `00-programme.md` (programme 10 SP + philosophie paquet de reconstruction).
5. Générer `01-couverture.md` pré-rempli : **toutes** les lignes (165 PROC_*, ~23
   services, 57 batchs, ~15 familles, flux EDI, fiches transverses) au statut `vide`.
6. Rédiger `02-perimetre-volumetrie.md`, `03-architecture-systeme.md`, `04-glossaire.md`
   (en réutilisant et corrigeant `architecture.md`, `phase0-final-report.md`).
7. Rédiger `05-ordre-reconstruction.md` (le DAG, §9) et `06-criteres-equivalence.md`
   (le cadre d'équivalence, §10).
8. Écrire les 3 templates `_templates/` (§11), avec la structure normatif/explicatif.
9. Rédiger `guides/reconstruire-rig.md` (le runbook, §16).
10. Créer `artifacts/README.md` (manifeste vide + procédure de rafraîchissement) et
    `95-reglementaire/index.md`.
11. **Vérification SP0** : un agent neuf ouvre `README.md` → sait en une lecture où va
    chaque information, comment reconstruire, et quel est l'état d'avancement ; les
    templates imposent la séparation normatif/explicatif ; `05-ordre-reconstruction.md`
    donne un DAG consommable.

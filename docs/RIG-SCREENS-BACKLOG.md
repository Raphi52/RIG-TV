# RIG-SCREENS-BACKLOG — écrans NON ENCORE PROUVÉS (à mapper/prouver)

> ⚠ **Rien ici n'est prouvé live.** Ce fichier est le BACKLOG : l'inventaire de ce qui EXISTE dans RIG mais
> dont l'interaction n'a PAS encore été prouvée par un run live. La carte de confiance (interactions **prouvées
> live**) = `RIG-UI-MAP.md`. On promeut une entrée d'ici vers RIG-UI-MAP.md UNIQUEMENT après un run live qui
> passe + screenshot lu (recette §6 de la map). Objectif : TestViewer englobera tout RIG → ce backlog est la roadmap.

## A. Flux documentés mais NON prouvés live (échec/limite au run du 2026-06-22)

Ces flux ÉTAIENT décrits dans une version antérieure de la map mais n'ont PAS passé la re-preuve live → sortis
de la carte de confiance tant qu'un run ne les valide pas. L'échec est runtime/flaky (pas forcément un mauvais
mapping), mais per la règle « rien dans la map sans preuve », ils restent ici.

| Flux | Smoke | Résultat live 2026-06-22 | Pourquoi pas prouvé |
|---|---|---|---|
| **RAPTURE export JSON** (PROC_PREAUD, `BtnExportJsonPlum`) | `--legacy-rapture-export` | ❌ 6 pass / 2 fail | « Aucun dialog post-click Export JSON » + fichier non écrit. Le clic Export n'a pas produit le SaveFileDialog attendu (timing/état). À rejouer/diagnostiquer. |
| **DCADEMAT formalité / refus** (§1.9, `RefuserDemande`/`ValiderFormaliteDemat`) | `--legacy-alertes-rec-form` | ❌ 5 pass / 1 fail | Menu contextuel de la demande NON ouvert sur HDESK non-interactif (VK_APPS/RealMouseClick/WM_CONTEXTMENU/MSAA tous sans effet ce run). FLAKY (réussit souvent), pas un mur dur. |
| **RETAUD — publicités en attente** (§1.5, `ult_GroupRadioButtonFiltreAppelAffaire`, `ULTDataGridViewEveProEnAttente`) | `--drive-retaud-pubs` | ⏸ non lancé | Exige `--audience-id <N>` (fixture absente). L'audience RETAUD elle-même EST prouvée (via rapture-import). |
| **Alertes RCS — int-form** (ouverture demande) | `--legacy-alertes-int-form` | ❌ 3 pass / 2 fail | Grille PROC_DEMANDE figée 180s (« Traitement en cours » au plafond) CE run. NB : la grille + l'ouverture demande SONT prouvées par int-dca/rec-dca → le chemin est OK, ce run a flaké sur le chargement. |

⚠ **§1.8 DCADEMAT réclamation** est dans la map (prouvé) MAIS partiellement : l'action « Lancer le pool » /
réclamation est DÉCLENCHÉE (prouvé live), l'**aperçu avant impression terminal n'est PAS confirmé visuellement**
(HDESK Mode B ne compose pas l'aperçu). Confirmation visuelle à faire en Mode A/C.

## B. Inventaire des écrans NON pilotés (165 PROC .NET — aucun prouvé interactable)

> Source : `RigApplication\Documentation\reference\proc\` (fiche métier par PROC). **Aucun** de ces écrans
> n'a de détail d'interaction prouvé — c'est la liste à explorer (recette §6 : `OpenProcessus()` + dump UIA +
> screenshot, puis promotion vers RIG-UI-MAP.md). Navigation (où vit chaque écran) = `docs\rig-menu-tree.txt`
> (scan live `--dump-menu` : 6 rails / 56 sous-menus / 603 entrées).

Domaines (≈ comptes) : Affaires judiciaires cycle-de-vie (17) · Audience/plumitif (8, dont RETAUD/PREAUD pilotés) ·
Décision/signature/GED judiciaire (10) · Acteurs/mandataires (8) · EDI judiciaire (4) · RCS inscriptions (19) ·
RCS consultation/KBIS/vues (8, dont KBIS-VK/XEX pilotés) · INPI/Guichet (3) · Alertes/DCADEMAT (6, partiellement
pilotés) · Endettement (7) · Comptabilité/caisse (12) · Facturation/clients (5) · GED/documents (15) ·
EDI/intégrations (9) · Surveillance (5) · Paramétrage/admin (14) · Éditions/stats (5) · Coffre/bateaux (4) ·
Accueil/pilotage/outils-dev (~15). **Total ~165 PROC .NET** (+ 379 PROCVB6, 147 EXE, batchs — hors UI directe).

> Liste détaillée par PROC : voir `RigApplication\Documentation\reference\proc\<nom>.md` (le code/la base font foi).
> ⚠ Le menu est DATA-DRIVEN (tables SQL `MENU_ONGLET`/`MENU_SOUS_MENU`/`PROCESSUS_ET_FONCTIONALITE`) — l'arbre
> réel vit dans `docs\rig-menu-tree.txt` (régénérable : `Rig.Wpf.Kbis.SmokeRunner.exe --dump-menu`).

## C. Comment promouvoir une entrée vers la carte de confiance
1. Amener RIG sur l'écran via un smoke `--legacy-*` (ou ajouter le flag, recette §6 de RIG-UI-MAP.md).
2. Run live → screenshot LU confirmant l'état terminal réel (pas juste « passed » UIA).
3. Grep `C:\RIG\Data\Log\9995\RigClientAccueil-*.txt` pour `eLog9Crash`/`SendMail` (mail-safety).
4. Ajouter le détail (séquence + AutomationIds + ancres) dans RIG-UI-MAP.md §1/§2 + relancer `verify-rig-ui-map.ps1`.

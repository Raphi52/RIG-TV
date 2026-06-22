# RIG-UI-MAP — Carte d'interaction RigClientAccueil.exe

> **But** : référence durable pour piloter RIG (legacy WinForms x86) et ajouter des scénarios
> SANS relire les 8 000+ lignes du driver à chaque session.
>
> **Source de vérité** : `LegacyDriver.cs` (primaire) + `Program.cs`, `Interaction.cs`,
> `LegacyParsing.cs` — tous dans `Rig.Wpf.Kbis.SmokeRunner\`.
> Tout fait cité dans ce document a été vérifié ligne par ligne dans le code réel.
> En cas de divergence code / doc : **le code fait foi**.
>
> Mis à jour : 2026-06-22. Ancres = numéros de ligne LegacyDriver.cs sauf mention contraire.

---

## 1. Carte des écrans

### 1.0 Infrastructure commune

| Méthode / fait | Ancre |
|---|---|
| `Launch()` — démarre le process, poll fenêtre principale (évite WaitForInputIdle) | L139 |
| `ClickSeConnecter()` — trouve `btnOk`, clic, poll FormLogin→FormAccueil, appelle `EnsureWindowMaximized()` en headless | L320 |
| `EnsureWindowMaximized()` — poll BoundingRectangle.Width ≥ 1400, reposte SC_MAXIMIZE | L444 |
| `FindButtonWithRetry()` — filtre par ProcessId (anti-pollution parallèle) | L88 |
| Mode B (HDESK RigDesktop) : pas de vol de focus ; aperçus DWM/AcroPDF non matérialisés | mémoire projet |

**Séquence minimale pour tout scénario :**
```
Launch() → ClickSeConnecter() → [OpenProc*] → [actions]
```

---

### 1.1 Console d'accueil — rails + listes

**Écran :** `FormAccueil` — la fenêtre principale après login.

**Séquence type pour ouvrir un PROC :**

1. Trouver le bouton de rail `btn1`..`btn7` (ControlType Pane, pas Button — RigButton étend RigPanel)
2. Clic → `lstSousmenu` se repeuple (poll `WaitForListRepopulated`)
3. Sélectionner l'item dans `lstSousmenu` → `lstProcessus` se repeuple
4. Sélectionner l'item dans `lstProcessus` → `Interaction.ActivateListItem(item, lstProcessus)`

| Méthode | Ancre | Fast-path |
|---|---|---|
| `OpenProcessus()` — pattern générique utilisé par TOUS les openers PROC | L618 | oui (fastPathBtnIndex, fastPathSousmenuIndex) |
| `OpenProcKbis()` — VK exact, exclut XXKBIS/VKREJ/XEX | L488 | scan btn1..btn7 × lstSousmenu × lstProcessus |
| `OpenProcRetaud()` | L916 | fastPathBtnIndex:3, fastPathSousmenuIndex:3 |
| `OpenProcPreaud()` | L926 | fastPathBtnIndex:3, fastPathSousmenuIndex:3 |
| `OpenProcXex()` — match "XEX" exact OU "edition interne"+"kbis" | L3682 | — |

**Piège :** `btn3`..`btn7` passent hors-écran à 750×480 → les clics de rail sont ignorés.
→ `EnsureWindowMaximized()` OBLIGATOIRE avant tout clic sur un rail.

---

### 1.2 KBIS / VK

**Séquence :**
1. `OpenProcKbis()` → onglet `pagetabVK` dans `tabControl`
2. `SearchKbisSiren()` — ouvre modal Recherche, saisit SIREN dans le 3e Edit trié (Y,X), clique "Rechercher (F7)", poll `pagetabVK`
3. `VerifyKbisTabOpened()` — poll `tabControl` pour `pagetabVK`
4. `OpenKbisDocument()` — détecte demande existante OU clique "Charger ce dossier", poll process/fenêtre/PDF

| Méthode | Ancre |
|---|---|
| `SearchKbisSiren()` | L2421 |
| `VerifyKbisTabOpened()` | L2619 |
| `OpenKbisDocument()` | L2864 |

**XEX :**
- `OpenProcXex()` L3682 — PROC distinct, match "XEX" exact OU "edition interne"+"kbis"

---

### 1.3 RAPTURE — import / export JSON

**Séquence import :**
0. Pré-requis : RIG loggé (§1.0). `OpenProcRetaud()`/`OpenProcPreaud()` font TOUTE la nav §1.1 (rail btn3 > sousmenu[3] > lstProcessus) — pas besoin de naviguer à la main.
1. `OpenProcRetaud()` ou `OpenProcPreaud()` (selon le module)
2. `ClickImporterRaptureAndOpenJson(path)` — trouve `IMPORT`, `Task.Run` fire-and-forget pour clic (évite blocage OpenFileDialog synchrone), poll 30 s OpenFileDialog, cible inner Edit de ComboBoxEx `1148`, WM_KEYDOWN VK_RETURN sur hwnd dialog
3. `ClickRecapImporterAndConfirm()` — mode APPLY, clic "Importer (...)", confirme 2 popups

**Séquence export :**
1. `ClickExportJsonAndSaveTo(path)` — trouve `BtnExportJsonPlum`, SaveFileDialog via `1148` (SetText direct sur ComboBoxEx ou fallback 1er Edit), VK_RETURN, vérifie fichier sur disque

| Méthode | Ancre | Note |
|---|---|---|
| `ClickImporterRaptureAndOpenJson()` | L1076 | OpenFileDialog : inner Edit child de `1148` requis |
| `ClickRecapImporterAndConfirm()` | L1657 | APPLY mode |
| `ClickExportJsonAndSaveTo()` | L1784 | SaveFileDialog : SetText sur `1148` direct (pas inner-child) |

**Piège RAPTURE :** la feature vit sur `Feature/JUD/RaptureEdiLot3` (worktree `wt-edilot3`), jamais mergée.
Un deploy depuis une autre branche écrase le DLL (496 Ko sans / 633 Ko avec). Rebuild `safeBuild.ps1` obligatoire.

---

### 1.4 RETAUD — audiences / sélection

**Séquence sélection d'audience :**
1. `SelectFirstAudienceInRetaud()` — trouve "Valider la sélection", prend la 1re ligne non-INT avec CHAMBRE whitelistée
2. OU `SelectAudienceInRetaudByDateHeure(date, heure)` — sélection précise par date+heure, même whitelist CHAMBRE

**CHAMBRE whitelist** (L1956) : `REF`, `AU`, `CX`, `MD`, `PC`, `TC`, `CC`, `JI`, `JE`, `FT`
Raison : éviter `GetCHAMBRE()` exception → mail spam.

| Méthode | Ancre |
|---|---|
| `SelectFirstAudienceInRetaud()` | L1932 |
| `SelectAudienceInRetaudByDateHeure()` | L2170 |

---

### 1.5 RETAUD — publicités en attente

**Séquence :**
1. Après sélection audience → clic radio "Publicités en attente" dans le groupe `ult_GroupRadioButtonFiltreAppelAffaire`
2. Poll `ULTDataGridViewEveProEnAttente` OU label "Nombre de publicités" (max 30 s)

| Élément (via `DriveRetaudPubsEnAttente`, ~L8040) | Ancre |
|---|---|
| Radio "Publicités en attente" (`ult_GroupRadioButtonFiltreAppelAffaire`) | L8117 |
| Grille `ULTDataGridViewEveProEnAttente` | L8220 |

---

### 1.6 Alertes RCS — demandes

**Séquence :**
1. `OpenAlerteRcs()` — trouve `lblAlertesPage1`, itère pages (max 8), clic `lstAlertes` par Name substring, `ActivateListItem`
2. `WaitForDemandeGrid()` — poll `ultDgvResultats` → Table/DataGrid → `_dgvDemandes`
3. `FindDemandeCell()` / `FindDemandeCells()` — MSAA-based (DataGridView WinForms non UIA-Grid)
4. `CollectCellsMsaa()` — `AccessibleObjectFromWindow` + `AccessibleChildren`, lignes avec ';' dans le texte
5. `BuildOpenDemandeTrigger()` — `accSelect` + `accLocation` → double-clic si visible, sinon VK_RETURN sur grille, sinon Home+Down×index+Enter

| Méthode | Ancre |
|---|---|
| `OpenAlerteRcs()` | L3702 |
| `WaitForDemandeGrid()` | L3878 |
| `FindDemandeCell()` / `FindDemandeCells()` | L3925 |
| `CollectCellsMsaa()` | L4015 |
| `BuildOpenDemandeTrigger()` | L4200 |

**Piège DataGridView :** UIA ne voit PAS la grille WinForms comme Table/Grid pattern.
→ MSAA obligatoire via `oleacc` : `accSelect(SELFLAG_TAKEFOCUS|SELFLAG_TAKESELECTION)` scrolle la ligne en vue avant double-clic.

---

### 1.7 DCADEMAT — dépôt DCA

**Séquence :**
1. Ouvrir une demande via `BuildOpenDemandeTrigger()`
2. `ConfigurerDepotDcaEtValider()` — coche la case DCA (UIA DataItem "DCA Ligne N" + TogglePattern, fallback MSAA), clique Valider, poll n° de demande

| Méthode | Ancre |
|---|---|
| `ConfigurerDepotDcaEtValider()` | L4761 |

---

### 1.8 DCADEMAT — réclamation

**Séquence :**
1. Depuis alerte réclamation → `OpenFirstDemandeAndVerify(dcademat:true)`
2. `ReclamerDcaAvecMotif(motif, ajout)` :
   - (1) Coche case DCA si présente (non-bloquant)
   - (2) Attend fin overlay (`WaitForLoadingOverlayToClear` max 15 s)
   - (3) Sélectionne motif dans combo `cboTypeMotif`
   - (4) Tab → RIG remplit texte `ultMotifReclamation`
   - (5) Ajoute marqueur en fin de texte
   - (6) Clic "Réclamer" (Alt+R fallback)
   - (7) Vérifie : (a) PDF extrait → `ContainsMotifMarker` ; (b) signal ouverture non-visuel ; (c) RIG vivant

**Cas AlreadyReclamee :** si combo `cboTypeMotif` vide ET motif déjà posé → `AlreadyReclameeException` → converti en succès (terminal métier déjà atteint).

| Méthode | Ancre |
|---|---|
| `OpenFirstDemandeAndVerify(dcademat:true)` — ouvre la 1re demande de l'alerte (étape PARTAGÉE : aussi §1.7 dépôt + §1.9 formalité/refus). S'appuie sur §1.6 `OpenAlerteRcs()` + `BuildOpenDemandeTrigger()` | L4297 |
| `ReclamerDcaAvecMotif()` | L5209 |
| `AlreadyReclameeException` | L23 |

---

### 1.9 DCADEMAT — formalité / refus

| Méthode | Note | Ancre |
|---|---|---|
| `ValiderFormaliteDemat()` | Retourne `false` (pas exception) si bouton "Valider" absent (stade non-actionnable) | L4910 |
| `RefuserDemande()` | Throw si "Refuser" absent/désactivé. NE PAS IMPRIMER : aucun bouton Imprimer touché | L4987 |

---

## 2. Catalogue AutomationIds

> Chaque ID a été vérifié littéralement dans `LegacyDriver.cs` (grep + lecture).
> Un ID absent = non inventé.

| AutomationId | Écran / rôle | ControlType UIA | Ancre LegacyDriver |
|---|---|---|---|
| `btnOk` | Login — bouton "Se connecter" | Button | L2812 (dans `WaitForLoginButton`) |
| `btn1`..`btn7` | Console — rails de navigation | Pane (RigButton étend RigPanel) | L544, L709 |
| `lstSousmenu` | Console — liste sous-menu | List | L557, L658, L736 |
| `lstProcessus` | Console — liste des processus | List | L566, L667, L760, L774 |
| `tabControl` | PROC — conteneur d'onglets | Tab | L627, L880, L3081 |
| `pagetabVK` | KBIS — onglet "VK" | TabItem | L2577, L2606, L2642, L2875 |
| `IMPORT` | Rapture — bouton "Importer Rapture" | Button/Pane | L1101 |
| `1148` | Dialogs fichiers (Open + Save) — ComboBoxEx filename | ComboBox | L1230, L1856 |
| `BtnExportJsonPlum` | Rapture — bouton "Export JSON Plumitif" | Button/Pane | L1795 |
| `ultDgvResultats` | Alertes — grille demandes (primaire) | DataGrid/Table | L3889, L3982 |
| `_dgvDemandes` | Alertes — grille demandes (fallback) | DataGrid/Table | L3901, L3984 |
| `lblAlertesPage1` | Alertes RCS — label page 1 | Text | L3711 |
| `lstAlertes` | Alertes RCS — liste des alertes | List | L3721 |
| `cboTypeMotif` | DCADEMAT Réclamation — combo "Type de motif" | ComboBox | L5470 |
| `ultMotifReclamation` | DCADEMAT Réclamation — champ texte motif (multiline) | Edit/Document | L5682 |
| `dgvExercices` | DCADEMAT Dépôt — grille exercices (primaire) | DataGrid | L6179 |
| `ultraGridExercices` | DCADEMAT Dépôt — grille exercices (fallback 1) | DataGrid | L6181 |
| `ult_GroupRadioButtonFiltreAppelAffaire` | RETAUD Pubs — groupe radio filtre | Group/Pane | L8117 |
| `ULTDataGridViewEveProEnAttente` | RETAUD Pubs — grille EP en attente | DataGrid | L8220 |

---

## 3. Sélecteurs par Name / Label (sans AutomationId stable)

Contrôles pilotés par leur texte visible (Name UIA ou AccessibleName WinForms).

| Contrôle | Texte recherché | Usage | Méthode |
|---|---|---|---|
| Bouton "Valider la sélection" | `"Valider la sélection"` (Name exact) | RETAUD — confirmer l'audience sélectionnée | `SelectFirstAudienceInRetaud()` L1932 |
| Bouton "Réclamer" | `"réclamer"` (case-insensitive, '&' retiré) | DCADEMAT — déclenche réclamation | `FindReclamerButton()` L5773 |
| Bouton "Valider" | `"valider"` (Name prefix, exclude "sélection") | Formalité — valider formalité démat | `FindToolbarActionButton()` L5056 |
| Bouton "Refuser" | `"refuser"` (Name exact) | DCADEMAT/formalité — refus | `FindToolbarActionButton()` L5056 |
| Label page alertes | `"Nombre de publicités"` (contains) | RETAUD Pubs — signal chargement | L8221 |
| Radio "Publicités en attente" | `"Publicités en attente"` (Name exact) | RETAUD Pubs — filtre EP | L8116 |
| 3e Edit SIREN (trié Y,X) | positionnel — 3e Edit visible dans modal | KBIS — saisie SIREN | `SearchKbisSiren()` L2421 |

---

## 4. Pièges & workarounds

### P1 — RigButton ≠ ControlType.Button
`btn1`..`btn7` sont des `RigButton extends RigPanel` → UIA expose ControlType **Pane**, pas Button.
Filtrer par AutomationId, pas par ControlType.

### P2 — ComboBoxEx `1148` : OpenFileDialog ≠ SaveFileDialog
- **OpenFileDialog** : `SetValue` sur le wrapper `1148` ne propage pas. Cibler l'inner **Edit child** de `1148` (L1230).
- **SaveFileDialog** : `SetText` (ValuePattern.SetValue) directement sur `1148` fonctionne (L1856-1863). Fallback sur 1er Edit visible.

### P3 — DataGridView WinForms : UIA aveugle
`ultDgvResultats` / `_dgvDemandes` / `dgvExercices` : pas de Grid/Table UIA exploitable.
→ MSAA obligatoire : `AccessibleObjectFromWindow` + `AccessibleChildren`.
`accSelect(SELFLAG_TAKEFOCUS|SELFLAG_TAKESELECTION)` pour scroller la ligne dans la zone visible avant double-clic.

### P4 — Fenêtre 750×480 : rails hors-écran
`btn3`..`btn7` sortent de l'écran. Clics silencieusement ignorés.
→ `EnsureWindowMaximized()` avant tout clic sur un rail (poll BoundingRectangle.Width ≥ 1400).

### P5 — OpenFileDialog modal : `Task.Run` obligatoire
`Interaction.Click()` sur `IMPORT` ouvre une boîte modale. L'InvokePattern bloquerait ~60 s.
→ `Task.Run` fire-and-forget (L1076) déclenche le clic, puis le driver poll l'OpenFileDialog.

### P6 — lstSousmenu / lstProcessus : listes lazy
Après clic sur un rail btn, les listes mettent du temps à se remplir.
→ `WaitForListRepopulated()` après chaque changement de sélection (L618 fast-path + 2-pass scan).

### P7 — Combo `cboTypeMotif` lazy (ULT_COMBO_CODE_MOTIF)
Items UIA = 0 tant que le dropdown est fermé.
→ `Expand()` → poll items > 0 (max 3 s) avant énumération (L5554).
Si 0 item après Expand ET valeur courante posée → `AlreadyReclameeException` (L5621).

### P8 — Mur HDESK partie (b) : aperçu non matérialisé
Sur desktop HDESK non composé (Mode B), les aperçus avant impression (AcroPDF, composition DWM)
ne peignent PAS. Signal de vérification = fichier PDF récupérable OU process viewer apparu OU RIG vivant.
Ne jamais cibler un bouton "Imprimer".

### P9 — Mode B n'est PAS mail-safe
Si `AmiLog.cs` déclenche `RigLog.SendMail` (L181), le driver renvoie exit 0 pendant que RIG logue un crash.
→ Toujours grep `C:\RIG\Data\Log\9995\RigClientAccueil-*.txt` pour `eLog9Crash`/`SendMail` avant de déclarer "pas de mail".

### P10 — CHAMBRE whitelist : ne PAS sauter
`SelectFirstAudienceInRetaud()` filtre sur REF/AU/CX/MD/PC/TC/CC/JI/JE/FT (L1956).
Les autres CHAMBREs déclenchent `GetCHAMBRE()` exception → mail spam prod.

### P11 — `ultMotifReclamation` : ne pas gate sur SupportsValue
La garde `byId != null && SupportsValue(byId)` peut court-circuiter sur un faux négatif UIA cache
juste après un Tab → retomber sur le plus grand champ = champ READ-ONLY "Pièces qui doivent être déposées".
→ Retourner `byId` directement dès qu'il est non null ; vérifier le ValuePattern au site d'écriture (L5674).

### P12 — AutomationIds DYNAMIQUES (numériques) sur certains écrans → cibler par Name, pas par aid
Vérifié live (dump UIA 2026-06-22) : la **fenêtre de connexion BD** ("Connection à la base de données Rig")
et certains boutons exposent des AutomationIds = **handles numériques runtime** (ex. `1049208`, et le bouton
**K-bis = `1524`** avec Name `KBis` / `XML du Kbis`), souvent avec Name vide pour les conteneurs. Ces aids
CHANGENT d'un lancement à l'autre → INUTILISABLES comme sélecteur. C'est pourquoi le driver cible ces contrôles
par **Name/texte** (ou MSAA). Un script UIA naïf qui cherche un aid stable sur la fenêtre BD échoue (aucun bouton
de connexion identifiable) — le login multi-étapes (dialog BD → FormLogin `btnOk` → Console) est géré par
`Launch()`/`ClickSeConnecter()`, à ne PAS réimplémenter en script externe (cf. §7).

---

## 5. Table de couverture

| PROC / scénario | Piloté ? | Méthode principale | CLI flag (Program.cs) |
|---|---|---|---|
| Login | oui | `ClickSeConnecter()` L320 | (tous) |
| KBIS VK — recherche SIREN | oui | `SearchKbisSiren()` L2421 | `--legacy-kbis-vk` L136 |
| KBIS XEX — document | oui | `OpenProcXex()` L3682 | `--legacy-kbis-xex` L141 |
| RAPTURE — import JSON | oui | `ClickImporterRaptureAndOpenJson()` L1076 | `--legacy-rapture-import` L123 |
| RAPTURE — export JSON | oui | `ClickExportJsonAndSaveTo()` L1784 | `--legacy-rapture-export` L110 |
| RAPTURE — process complet | oui | `RunLegacyRaptureProcess` | `--legacy-rapture-process` L106 |
| RETAUD — sélection audience | oui | `SelectFirstAudienceInRetaud()` L1932 | `--drive-retaud-pubs` L116 |
| RETAUD — pubs en attente | oui | radio `ult_GroupRadioButtonFiltreAppelAffaire` L8117 | `--drive-retaud-pubs` L116 |
| Alertes RCS — ouvrir demande | oui | `OpenAlerteRcs()` L3702 | `--legacy-alertes-int-form` L145 |
| DCADEMAT — dépôt DCA | oui | `ConfigurerDepotDcaEtValider()` L4761 | `--legacy-alertes-int-dca` L146 |
| DCADEMAT — réclamation | oui | `ReclamerDcaAvecMotif()` L5209 | `--legacy-alertes-rec-dca` L152 |
| Formalité démat — valider | oui | `ValiderFormaliteDemat()` L4910 | `--legacy-dcademat-*` L154 |
| Formalité démat — refus | oui | `RefuserDemande()` L4987 | `--legacy-dcademat-*` L154 |
| DCADEMAT complet | oui | `RunLegacyDcademat` | `--legacy-dcademat-*` L154 |

**Non pilotés (écrans sans driver actuel) :**
- Impression / aperçus (garde NE PAS IMPRIMER — jamais touchés)
- Saisie manuelle dans processus de suivi MB1 post-réclamation
- Modules sans alerte RCS connue (à mapper si besoin)

---

## 6. Recette pour mapper un nouvel écran

1. **Identifier le PROC** — quel bouton rail + sous-menu + processus ? Comparer à `OpenProcessus()` L618.
2. **Inventorier les contrôles** — `DumpDescendants(_window, maxDepth: 5)` (déjà dans le driver) + `CaptureScreenshot()` en Mode A/C.
3. **Vérifier chaque AutomationId** — `FindByAutomationId("x")` dans le driver OU grep ce fichier.
4. **Identifier les contrôles UIA-aveugles** — DataGridView WinForms → MSAA (modèle `CollectCellsMsaa()` L4015).
5. **Tester en Mode B headless** — vérifier qu'aucun signal ne dépend de la composition DWM (aperçu → signal non-visuel).
6. **Ajouter un flag CLI** dans `Program.cs` (modèle L136-L154) + mettre à jour ce fichier (section 1, 2, 5).
7. **Grepper ce fichier** pour vérifier qu'aucun AutomationId n'est dupliqué ou contradictoire.

---

---

## 7. Vérification (2026-06-22)

Ce doc a été vérifié à DEUX niveaux :

**(a) Contre le code** — `verify-rig-ui-map.ps1` (dans ce dossier) : les **20 AutomationIds** du catalogue (§2)
existent tous littéralement dans `LegacyDriver.cs` (exit 0). À RELANCER après toute évolution du driver
(garde anti-dérive : un ID renommé/supprimé = détecté). ⚠ Le script vérifie la PRÉSENCE des AutomationIds,
PAS les numéros de ligne des ancres `L…` (= pointeurs indicatifs ; une refacto qui déplace des méthodes les
périme silencieusement → les ancres se mettent à jour à la main).

**(b) Contre l'app vivante** — smoke `--legacy-kbis-vk` rejoué via le driver prouvé : **8/8 steps PASS, exit 0,
mail-safe** (log RIG sans `eLog9Crash`/`SendMail`). Le driver a franchi LIVE : login → Console (onglets
Accueil/Demandes/VK, loggé VILAIN RIG_DEV-9995) → PROC_KBIS tab VK → saisie `2024B00001` → dossier chargé →
clic K-bis → viewer ouvert. Screenshot confirmant : `Desktop\JsonRapture\screenshots\<pid>\smoke-kbis-vk-OK-*.png`.
→ Les AutomationIds de navigation (`btnOk`, rail, `lstSousmenu`, `lstProcessus`, `tabControl`, `pagetabVK`)
**résolvent et fonctionnent dans l'app vivante**. ⚠ Portée : vérif live = **KBIS-VK seulement** cette session ;
RETAUD-import / DCADEMAT / Alertes = vérif statique code↔doc (walk, 0 écart constaté), NON rejoués live ici.

**Outils de découverte** (ce dossier) :
- `verify-rig-ui-map.ps1` — vérif code (à relancer après modif driver).
- `dump-rig-ui.ps1` — lance RIG + dump UIA de la fenêtre active. ⚠ Atteint la fenêtre de connexion BD mais NE
  franchit PAS le login multi-étapes (aids dynamiques, cf. P12) — c'est le rôle du driver. Usage réel : pour
  mapper un écran NEUF, lancer un smoke proche (`--legacy-*`) qui amène RIG sur l'écran via le driver, PUIS
  brancher un dump sur la fenêtre active. La vérif live de bout en bout passe par un **smoke réel**, pas par ce script seul.

---

## 8. Inventaire complet des écrans RIG (objectif : couvrir TOUTE l'app)

> But (2026-06-22, demande user) : TestViewer doit à terme englober TOUT RIG → inventaire de TOUS les écrans,
> pas seulement les ~12 pilotés. Source inventaire : `RigApplication\Documentation\reference\proc\` (183 fiches,
> **165 plugins PROC_* .NET** = écrans WinForms) — chaque PROC a sa fiche métier `reference\proc\<nom>.md`.
> Au-delà : 379 PROCVB6 (legacy COM), 147 EXE, batchs/services sans UI (hors scope pilotage UI direct).

**3 profondeurs de mapping (à ne pas confondre) :**
1. **Inventaire** (cette section) — QUELS écrans existent + domaine. ✅ FAIT ici.
2. **Navigation** (où chaque écran vit dans les menus) — ⚠ **DATA-DRIVEN** : les rails `btn1..btn7`, sous-menus
   et l'affectation PROC→menu sont en BASE (`MENU_ONGLET`, `MENU_SOUS_MENU`, `PROCESSUS_ET_FONCTIONALITE`,
   col. `PROCF_VISIBLE_DANS_MENU`/`PROCF_DOMAINE`/`PROCF_CATEGORIE`), PAS statiques. → obtenir via (a) `SELECT`
   read-only sur `RIG_DEV` (auth dev-DB requise) OU (b) dump live du menu (lancer RIG + scanner les 7 rails).
3. **Détail d'interaction** (AutomationIds/séquences par écran) — ⚠ **PAS extractible des docs** (fidélité doc
   ~8-10 %) : SEUL moyen = lancer+naviguer chaque écran via le driver + `DumpDescendants`/MSAA (recette §6).
   → INCRÉMENTAL : se construit écran par écran à mesure que TestViewer étend sa couverture.

**Légende statut** : ✅ piloté + détaillé (§1-2) · 📋 inventorié, détail UI à faire (recette §6).

### Affaires judiciaires — cycle de vie instances
`PROC_ARRIVEE` `PROC_CJUD` `PROC_MJUD` `PROC_MJUDS` `PROC_MDJUD` `PROC_CHORGANESENMASSE` `PROC_CHORGANESMASSE` `PROC_MODIF_REPER` `PROC_JOIN` `PROC_LIBAFFAIRE` `PROC_RECHAFF` `PROC_RECHENC` `PROC_PARTIES` `PROC_ACTEUR_JUD` `PROC_SUPP_ACTEUR_JUD` `PROC_RAPPACTEUR` `PROC_RECLAFF` · 📋 (17)

### Audience & plumitif
`PROC_AUDIENCE` `PROC_PREAUD`✅ `PROC_RETAUD`✅ `PROC_GESTAUDS` `PROC_RELANCEFILE` `PROC_RELANCELOT` `PROC_RETOURLRAR` `PROC_STATCA` · ✅ RETAUD (audience+import Rapture+pubs), PREAUD (export JSON) ; 📋 les 6 autres

### Décision, signature & GED judiciaire
`PROC_ORDOSIGN` `PROC_SIGNJUD` `PROC_GEDJUD` `PROC_DEPOTJUD` `PROC_SDEMATJUD` `PROC_ACTES_NUM` `PROC_REGJUD` `PROC_VJUD` `PROC_MPCS` `PROC_CLOT` · 📋 (10)

### Acteurs, mandataires, prévention
`PROC_MANDATAIRE` `PROC_MDIL` `PROC_MMA` `PROC_MESIN` `PROC_CABINET` `PROC_CJUDINFOG` `PROC_SURVBOD` `PROC_SURVMES` · 📋 (8)

### EDI / dématérialisation judiciaire
`PROC_EDI_RPJC_REN` `PROC_EDI_RPJC_TRCNC` `PROC_EDI_RPVAINS` `PROC_IA` · 📋 (4)

### RCS — inscriptions & modifications
`PROC_NPC` `PROC_NF` `PROC_MPC` `PROC_MPCS` `PROC_DCA` `PROC_DEP` `PROC_ARCHIRAD` `PROC_NUM` `PROC_SCEAU` `PROC_IPE` `PROC_IPSUIVI` `PROC_SURVIP` `PROC_INTEGCREANCE` `PROC_MODCREANCE` `PROC_CREANCE` `PROC_CREANREP` `PROC_ADDCREANCE` `PROC_DAS` `PROC_DASBEN` · 📋 (19)

### RCS — consultation / KBIS / vues
`PROC_KBIS`(VK)✅ `PROC_XEX`✅ `PROC_VDOSSIER` `PROC_VFNIG` `PROC_FNIG` `PROC_XAFF` `PROC_XXAFF` `PROC_XLIEN` · ✅ KBIS-VK (visualisation extrait), XEX (édition interne) ; 📋 les 6 autres

### RCS — INPI / Guichet unique
`PROC_GINPI` `PROC_INPIPAY` `PROC_CFENET_RET` · 📋 (3)

### Alertes RCS / DCADEMAT
`PROC_DEMANDE`✅ `PROC_ACTREJ` `PROC_ACTREJ_CMD_WEB` `PROC_ACTRET` `PROC_DOC_DEMAT`✅ `PROC_REJET` · ✅ Alertes RCS (ouverture demande), DCADEMAT (dépôt DCA + réclamation + formalité/refus) ; 📋 le reste

### Endettement (BEN / DCA)
`PROC_DBE` `PROC_DBEN` `PROC_DBEN_ALERT` `PROC_DBE_INJ` `PROC_DBE_RELANCE` `PROC_DCA_INJ` `PROC_DCA_RELANCE` · 📋 (7)

### Comptabilité / caisse
`PROC_JCAISSE` `PROC_JVENTE` `PROC_JLR` `PROC_ENCAISS` `PROC_RECHENC` `PROC_COMPTABILISATION` `PROC_FACTURE` `PROC_FACTURE_ETAPEFACTURE` `PROC_FACTURE_RECETTE` `PROC_FACTREEDIT` `PROC_VLETTRAGE` `PROC_VCHIFFRES` · 📋 (12)

### Facturation / clients
`PROC_FCLIENT` `PROC_GCLIENT` `PROC_RELCLI` `PROC_REMBANQ` `PROC_REMBANQ_EXE` · 📋 (5)

### GED / documents
`PROC_REDA` `PROC_REDAA` `PROC_REDA_MASSE` `PROC_COURRIER_DIVERS` `PROC_COURRIERM` `PROC_IMAGE` `PROC_PDF` `PROC_PDF_WRITER` `PROC_GED_HISTORIQUE` `PROC_GED_LIER_DJ` `PROC_GED_MODELE_MAIL` `PROC_GED_MODIF` `PROC_GED_NATURE_DOC` `PROC_VGED` `PROC_TRANSDOC` · 📋 (15)

### EDI / intégrations
`PROC_EDCHQEMIS` `PROC_ENV_JAL` `PROC_CORDJRCS` `PROC_CORRESPMIG` `PROC_FIBEN` `PROC_FIBEN_EXE` `PROC_CFENET_RET` `PROC_GESTLOT` `PROC_GESTBOD` · 📋 (9)

### Surveillance
`PROC_SURVBOD` `PROC_SURVIP` `PROC_SURVMES` `PROC_SRBE_RAPPEL` `PROC_DBEN_ALERT` · 📋 (5)

### Paramétrage / administration
`PROC_GTABREF` `PROC_TARIFS` `PROC_TAXESETTOURS` `PROC_ARTICLE` `PROC_GCOM` `PROC_GCONV` `PROC_GESTBANQ` `PROC_GESTDATE` `PROC_GESTEMAIL` `PROC_GESTMODEPAIEMENT` `PROC_GESTTIERSPAYEURS` `PROC_PARAM_IP` `PROC_PARAM_LISTEEXCEL` · 📋 (14)

### Éditions / impression / stats
`PROC_IMPLEG` `PROC_LISTEEXCEL` `PROC_REQUETES` `PROC_STAT` `PROC_TABLEAU_LOT3` · 📋 (5)

### Coffre-fort / dossier / bateaux
`PROC_COFFRE_FORT` `PROC_DCOFFRE` `PROC_BATEAU` `PROC_RECHERCHE_BATEAU` · 📋 (4)

### Accueil / pilotage / divers / outils dev
`PROC_ACCUEIL` `PROC_ACCUEIL_PARAM` `PROC_XPILOTAGE` `PROC_WIKI` `PROC_WEB_BROWSER` `PROC_PAPERCUT` `PROC_OPE` `PROC_DJ_OCCULTATIONS` `PROC_EDIT_MAQUETTE` `PROC_EDT_DIFF` `PROC_DEST_REDA` `PROC_TESTNLH` `PROC_DEBUG_CONTROLS` `PROC_DEMO` `PROC_DEV*` · 📋 (outils/dev, basse priorité)

**Roadmap pour compléter (profondeurs 2 et 3) :**
- **Nav (où vit chaque écran)** : `SELECT PROCF_CODE, PROCF_LIBELLE, PROCF_DOMAINE, PROCF_CATEGORIE FROM
  PROCESSUS_ET_FONCTIONALITE WHERE PROCF_VISIBLE_DANS_MENU=1` sur `RIG_DEV` (read-only) + `MENU_ONGLET`/`MENU_SOUS_MENU`
  → reconstruit les 7 rails et l'affectation des PROC. **Nécessite l'OK d'accès lecture à la base dev.** Alternative
  sans SQL : étendre SmokeRunner d'un mode `--dump-menu` (réutilise `Launch()`+login + scan `OpenProcessus`).
- **Détail UI par écran** : INCRÉMENTAL. Pour chaque PROC à piloter → `OpenProcessus()` (driver, L618) + dump UIA +
  screenshot (recette §6) → ajouter au §1/§2 + relancer `verify-rig-ui-map.ps1`. Priorisable par domaine métier.

---

*Source : `LegacyDriver.cs` ~7762 lignes · `Interaction.cs` ~570 · `LegacyParsing.cs` ~884 · `Program.cs` ~2761 — lus et vérifiés le 2026-06-22 (code + run live KBIS-VK). Inventaire §8 : `RigApplication\Documentation\reference\proc\` (165 PROC).*

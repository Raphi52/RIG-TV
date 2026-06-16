# FormRaptureImportRecap « parfaite » — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development
> ou superpowers:executing-plans. Steps en checkbox `- [ ]`.

**Goal :** rendre l'écran récap d'import Rapture (`FormRaptureImportRecap`)
utilisable : cocher met à jour le bouton, on voit POURQUOI c'est désactivé,
et l'import peut réellement écrire en base (matcher retrouve l'audience source).

**Architecture :** 3 phases. Phase 1 = corrections UI dans le plugin
PROC_RETAUD (Designer + recap.cs). Phase 2 = `RaptureAudienceMatcher` sanitise
le libellé chambre (réutilise `RaptureAudienceSanitizer`) + bandeau explicatif.
Phase 3 = build v48 + deploy + smoke + capture recap fenêtre-only (rule 15).

**Tech Stack :** WinForms .NET FW 4.8 (PROC_RETAUD), safeBuild v48,
deploy-proc-retaud.ps1, SmokeRunner/LegacyDriver (FlaUI), RIG_DEV.

**Décision actée :** Phase 2 = A (matcher sanitize) + C (bandeau). B (Creator
V2 qui crée les AppelAffaire) = évolution future hors périmètre.

**Contrainte :** zéro changement du contrat d'écriture (ApplyService/bindings) ;
`fd.NewValue/OldValue` bruts préservés (le nettoyage HTML est *affichage seul*) ;
noms de contrôles existants conservés (driver smoke en dépend).

---

## Fichiers touchés

- Modifier : `Source/RIG/DLL/Processus/PROC_RETAUD/RAPTURE_IMPORT/FormRaptureImportRecap.cs`
- Modifier : `Source/RIG/DLL/Processus/PROC_RETAUD/RAPTURE_IMPORT/FormRaptureImportRecap.Designer.cs`
- Modifier : `Source/RIG/DLL/Processus/PROC_RETAUD/RAPTURE_IMPORT/RaptureAudienceMatcher.cs`
- Modifier : `Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs` (capture recap fenêtre-only)
- Build/deploy : `Source/CompilationLivraison/deploy-proc-retaud.ps1` (existant, autorisé)

---

### Task 1 — Checkbox non éditable sur lignes non-modifiables + grisé

**Files:** `FormRaptureImportRecap.cs` (`InitData`, nouveau handler), `FormRaptureImportRecap.Designer.cs` (abonner `DataBindingComplete`).

- [ ] **Step 1.1** — Designer : abonner l'événement.
  Dans `InitializeComponent`, après la config `dgvDiff` :
  ```csharp
  this.dgvDiff.DataBindingComplete += new System.Windows.Forms.DataGridViewBindingCompleteEventHandler(this.DgvDiff_DataBindingComplete);
  ```
- [ ] **Step 1.2** — recap.cs : handler qui désactive+grise la cellule ✓ des lignes non-modifiables.
  ```csharp
  private void DgvDiff_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e) {
      foreach (DataGridViewRow row in dgvDiff.Rows) {
          DiffRow d = row.DataBoundItem as DiffRow;
          if (d == null) continue;
          var cell = row.Cells[colAccepted.Index];
          if (!d.Modifiable) {
              cell.ReadOnly = true;
              cell.Style.BackColor = System.Drawing.Color.Gainsboro;
              cell.ToolTipText = "Non modifiable via Rapture (V1) — cf. colonne Note";
          }
      }
  }
  ```
- [ ] **Step 1.3** — Build+deploy (Task 6) puis vérif : la ligne « (composition) » a sa case grisée/non cochable.

### Task 2 — Bandeau « pourquoi le bouton est désactivé »

**Files:** `FormRaptureImportRecap.Designer.cs` (label), `FormRaptureImportRecap.cs` (`UpdateImporterButton`).

- [ ] **Step 2.1** — Designer : ajouter `private System.Windows.Forms.Label lblBlocage;`
  (déclaration + `new`), dans `pnlBoutons.Controls`, ancré gauche, `ForeColor`
  `#B02A37`, `AutoEllipsis=true`, `Location (10,15)`, `Size (700,22)`.
- [ ] **Step 2.2** — recap.cs `UpdateImporterButton()` : après calcul de `n`,
  si bouton désactivé, renseigner `lblBlocage.Text` :
  ```csharp
  if (_validation.HasHeaderError)
      lblBlocage.Text = "Import bloqué : en-tête JSON en erreur (corrigez le fichier).";
  else if (n == 0) {
      int ign = _diff != null ? _diff.AffairesIgnorees.Count : 0;
      lblBlocage.Text = ign > 0
          ? string.Format("Aucune modification applicable : {0} affaire(s) du JSON non trouvée(s) dans l'audience cible (mauvaise audience ouverte ?).", ign)
          : "Aucune modification applicable (compositions non éditables en V1).";
  } else lblBlocage.Text = "";
  ```
- [ ] **Step 2.3** — vérif (Task 6) : sur audience vide, le bandeau explique « N affaires non trouvées ».

### Task 3 — Boutons « Tout cocher » / « Tout décocher » (modifiables only)

**Files:** `FormRaptureImportRecap.Designer.cs` (2 boutons), `FormRaptureImportRecap.cs` (2 handlers).

- [ ] **Step 3.1** — Designer : `btnToutCocher`/`btnToutDecocher` dans `pnlBoutons`,
  ancrés à gauche (Location ~ (10,40) zone libre), `Size (110,28)`, abonner `Click`.
  Conserver `btnImporter`/`btnAnnuler` inchangés (driver en dépend).
- [ ] **Step 3.2** — recap.cs handlers :
  ```csharp
  private void BtnToutCocher_Click(object s, EventArgs e)   { SetAllAccepted(true);  }
  private void BtnToutDecocher_Click(object s, EventArgs e)  { SetAllAccepted(false); }
  private void SetAllAccepted(bool v) {
      var rows = dgvDiff.DataSource as BindingList<DiffRow>;
      if (rows == null) return;
      foreach (var r in rows) if (r.Modifiable) r.Accepted = v;
      dgvDiff.Refresh(); UpdateImporterButton();
  }
  ```

### Task 4 — Nettoyage HTML à l'affichage (valeur brute préservée)

**Files:** `FormRaptureImportRecap.cs` (`InitData` bindings + helper).

- [ ] **Step 4.1** — Helper statique :
  ```csharp
  private static string DisplaySafe(string raw) {
      if (string.IsNullOrEmpty(raw)) return raw;
      string s = System.Text.RegularExpressions.Regex.Replace(raw, "<[^>]+>", " ");
      s = System.Net.WebUtility.HtmlDecode(s);
      s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
      return s.Length > 400 ? s.Substring(0, 400) + " […]" : s;
  }
  ```
- [ ] **Step 4.2** — `InitData` : pour `DiffRow` utiliser `ValeurAvant=DisplaySafe(fd.OldValue)`,
  `ValeurApres=DisplaySafe(fd.NewValue)`, `Note=DisplaySafe(fd.Note)` ; pour
  `PrevalRow` `Message=DisplaySafe(it.Message)`. **NE PAS** toucher `fd.NewValue`
  ni `Inner` (l'écriture via `BtnImporter_Click`/ApplyService reste sur les valeurs brutes).

### Task 5 — Phase 2A : Matcher sanitise le libellé chambre

**Files:** `RaptureAudienceMatcher.cs` (`Find`).

- [ ] **Step 5.1** — Dans `Find(dto, codeGreffe)`, remplacer
  `string chambre = dto.Chambre ?? "";` et `string section = dto.Section;` par :
  ```csharp
  string chambre = RaptureAudienceSanitizer.SanitizeChambre(dto.Chambre);
  string section = RaptureAudienceSanitizer.Truncate(dto.Section, 10);
  ```
  (mêmes transformations que `RaptureAudienceCreator`, pour que
  `AudienceCabinet.GetAudience(chambre,date,heure,section,codeGreffe)` interroge
  avec le CODE stocké en base, pas le libellé Rapture.)
- [ ] **Step 5.2** — vérif (Task 6) : import pc-v5 SANS `--create` → le matcher
  retrouve l'audience source (≈28644) → recap avec vraies lignes modifiables
  pré-cochées + bouton « Importer (N champ(s) coché(s)) » ACTIVÉ.
  ⚠ Risque à valider : `SanitizeChambre("PC : clôtures de LJ")` doit == le
  `AUDNC_CHAMBRE` stocké ("CLOT" pour 28644). Si KO → ajuster le mapping
  sanitizer (table label→code) et reboucler.

### Task 6 — Build v48 + deploy + vérif rule 15 (capture recap fenêtre-only)

**Files:** `LegacyDriver.cs` (capture), build/deploy scripts.

- [ ] **Step 6.1** — `LegacyDriver.cs` : à la détection recap, remplacer
  `CaptureScreenshot("rapture-recap-VIEW", fullScreen:true)` par une capture
  **de la fenêtre recap uniquement** : `Capture.Element(recap).ToFile(path)`
  (FlaUI), label `rapture-recap-VIEW`, fallback fullScreen si échec.
- [ ] **Step 6.2** — Build SmokeRunner Release ; build+deploy PROC_RETAUD :
  ```
  powershell -ExecutionPolicy Bypass -File Source/CompilationLivraison/deploy-proc-retaud.ps1
  ```
- [ ] **Step 6.3** — Smoke matché (écriture réelle possible) :
  `SmokeRunner --legacy-rapture-import --json <pc-v5>` (SANS --create →
  matcher trouve l'audience source). Capture recap fenêtre-only.
- [ ] **Step 6.4** — Analyser le screenshot (rule 15(d)) : (a) colonnes lisibles ;
  (b) ✓ grisée sur lignes non-modifiables, cochable sinon ; (c) lignes
  modifiables PRÉ-cochées ; (d) bouton « Importer (N) » ACTIVÉ ; (e) bandeau
  vide si OK / explicite sinon ; (f) HTML `ministere_public` lisible.
- [ ] **Step 6.5** — Non-régression : driver smoke trouve toujours recap +
  boutons Importer/Annuler (noms inchangés) ; scénario `--create` montre le
  bandeau « N affaires non trouvées ».
- [ ] **Step 6.6** — Itérer (retour Task concernée) tant que screenshot ≠ attendu.

---

## Hors périmètre (évolution future, type B)

`RaptureAudienceCreator` V2 (création des `APPEL_AFFAIRE`/`INSTANCE` depuis le
JSON pour permettre l'écriture sur audience neuve) — chantier métier séparé,
documenté ici comme TODO, non planifié.

## Risques

| Risque | Mitigation |
|---|---|
| `SanitizeChambre` ne mappe pas le libellé → matcher toujours KO | Task 5.2 vérif explicite ; ajuster table sanitizer si besoin |
| Capture FlaUI `Capture.Element(recap)` échoue (modale owned) | Fallback fullScreen conservé |
| Régression driver smoke (noms contrôles) | Conserver btnImporter/btnAnnuler/Text « Import JSON Rapture » ; Task 6.5 |
| Deploy C:\rig | deploy-proc-retaud.ps1 (autorisé, backup .bak + rollback) |

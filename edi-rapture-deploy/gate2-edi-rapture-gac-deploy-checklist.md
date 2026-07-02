# GATE 2 — Déploiement GAC pour que la phase 2 (IntegrerRig) tourne

## Le problème (démontré)
`IntegrerRig` utilise le type **`DematRapture`** (nouveau, dans `RigMetier`). Or `RigMetier` se charge
depuis le **vrai GAC Windows** (`C:\Windows\Microsoft.NET\assembly\GAC_MSIL\RigMetier\v4.0_4.0.2013.80__47a4ea39534a2d5c\`),
qui a **précédence** sur `Bin Dot Net Gac` et le bin applicatif. Tant que le GAC porte l'ancien `RigMetier`
(sans `DematRapture`) → **`TypeLoadException` → NOT-DEPLOYED**. Prouvé : avec le `RigMetier` frais au GAC,
`IntegrerRig` crée bien les lignes `DEMAT_RAPTURE` (test : 9 lignes, audience 28590).

## DLL à (re)déployer
| DLL | Cible | Pourquoi |
|---|---|---|
| `RigMetier.dll` (frais) | **vrai GAC** (gacutil) | contient `DematRapture` |
| `RigBaseGreffe.dll` (frais) | **vrai GAC** (gacutil) | contient le DAO base de `DEMAT_RAPTURE` |
| `EdiRaptureRetAud.dll` | `C:\rig\Bin Dot Net Gac\EdiRaptureRetAud\4.0.2013.80__47a4ea39534a2d5c\` (Copy-Item) | DLL EDI métier, chargée via `AmiResolver` (PAS le vrai GAC) — pas d'admin |
| `PROC_RETAUD.dll` (frais) | `C:\rig\Bin Processus\` (deploy normal) | pipeline RAPTURE_IMPORT (parser/mapper/validator) |

> Version/token inchangés (`4.0.2013.80 / 47a4ea39534a2d5c`) → l'enregistrement EDI (`CS_ASSEMBLY`) reste valide.

## Procédure (admin, sur le serveur EDI/greffe cible)
1. **Voie normale = `RigToGac.ps1`** (l'outil sanctionné du kit RIG) sur les DLL frais → il fait le `gacutil /if`.
   Sinon, manuel :
2. **Tuer** les process qui lockent les DLL : `RigClientAccueil`, `RigBatch`, `RigService*`, les exes EDI.
3. **Backup** (pour rollback) : copier les `RigMetier.dll` / `RigBaseGreffe.dll` **actuels** du GAC
   (`GAC_MSIL\<Nom>\v4.0_4.0.2013.80__47a4ea39534a2d5c\<Nom>.dll`) vers un dossier sûr.
4. **Installer** avec le **gacutil NETFX 4.8** (⚠ PAS le 4.0 de `C:\RIG\BinC` qui refuse « runtime plus récent ») :
   `"C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\gacutil.exe" /if <RigMetier.dll frais>`
   puis idem pour `RigBaseGreffe.dll`.
5. **Vérifier** : `gacutil /l RigMetier` (présent) + relancer le scénario `rapture-edi-import.ps1` →
   doit passer **PASS** (lignes `DEMAT_RAPTURE` créées, plus de NOT-DEPLOYED).
6. **Copier** `EdiRaptureRetAud.dll` dans `Bin Dot Net Gac\EdiRaptureRetAud\4.0.2013.80__47a4ea39534a2d5c\`
   (Copy-Item, pas d'admin) + copie flat `Bin Dot Net Gac\EdiRaptureRetAud.dll`.

## Rollback
`gacutil /if <RigMetier.dll backup>` + `<RigBaseGreffe.dll backup>` → remet l'état antérieur.

## ⚠ Prérequis amont
Ces DLL frais doivent venir d'un **build de la branche mergée** (`Feature/JUD/RaptureEdiLot3` → `Development`),
pas d'un worktree local. Le déploiement machine-wide de code **non mergé** n'est acceptable qu'en **test local
temporaire** (avec restore), pas en prod.

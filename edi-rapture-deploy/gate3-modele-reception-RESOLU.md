# GATE 3 — Modèle de réception : RÉSOLU (plus de question canal)

## Décision (confirmée)
- **Pas de canal** (ni CFT, ni Extelia). Les fichiers sont **déposés dans un dossier** (chemin **à définir**).
- La **réception** = un scan périodique du dossier qui, pour chaque fichier, appelle
  `EdiEntrantRecevoirFichier.exe PathFichier=<fichier>;CodeEdi=RAPTURE_RETAUD` → crée le `FICHIER_ENTRANT`.
  (Nativement supporté : l'exe prend un chemin **local** + un `CodeEdi` **explicite**, sans canal — cf. ses
  exemples internes `LIASSE_CFE_PAPIER` / `INSEE_AVISIR`.) `RecevoirFichier` supprime le fichier source après réception OK.
- Le **greffe est lu dans le CONTENU** du JSON (`dto.CodeGreffe`, en-tête audience) en phase 2 — **notre choix
  Option A est donc le bon**. **Aucune convention de nommage de fichier n'est requise.**

## Conséquences
- `EDI_ENTRANT.EDIENT_CFT_IDF` = **NULL** (fait, dev) ; pas de champs Extelia.
- La chaîne est **3 batchs** : Réception (scan dossier → RecevoirFichier) → TraiterFichier → IntegrerRig
  (cf. `gate1a-*` + `gate1b-*`).
- ⚠ Précision : `TraiterFichier` ne scanne PAS de dossier — il traite les `FICHIER_ENTRANT` déjà créés.
  C'est la **Réception** (gate1a) qui fait dossier → `FICHIER_ENTRANT`.

## Seule chose encore à définir
- **Le chemin du dossier de dépôt** (`$DropFolder`) : à fixer (local serveur EDI ou UNC), puis le passer
  à `gate1a`/`gate1b`. Décision de config, pas de dépendance externe.

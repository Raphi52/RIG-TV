# =============================================================================
# GATE 1a — RECEPTION : scanne le dossier de depot et pousse chaque fichier dans l'EDI.
# -----------------------------------------------------------------------------
# Modele "depot dossier, pas de canal" (confirme) : pour chaque fichier du dossier,
# appelle EdiEntrantRecevoirFichier.exe qui cree un FICHIER_ENTRANT (etat ReceptionSucces)
# pour RAPTURE_RETAUD. RecevoirFichier supprime le fichier source apres reception OK.
# Le greffe n'est PAS lu ici (ni du nom, ni d'un canal) : il est lu du CONTENU en phase 2
# (TraiterFichier -> dto.CodeGreffe). Donc AUCUNE convention de nommage requise.
#
# A planifier (cf. gate1b) toutes les N min sur le serveur EDI. ModeDebug=d en dev/recette.
# =============================================================================
param(
    [Parameter(Mandatory)] [string] $DropFolder,             # dossier de depot (A DEFINIR)
    [string] $ExeDir  = 'C:\rig\exe\EdiLot3',
    [string] $CodeEdi = 'RAPTURE_RETAUD',
    [string] $Filter  = '*.json',
    [switch] $Dev                                            # -Dev => ModeDebug=d (base EDI DEVEDIGREFFE)
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $ExeDir 'EdiEntrantRecevoirFichier.exe'
if (-not (Test-Path $exe)) { throw "Exe absent : $exe" }
if (-not (Test-Path $DropFolder)) { throw "Dossier de depot absent : $DropFolder" }
$modeDebug = if ($Dev) { 'ModeDebug=d;' } else { '' }

$files = Get-ChildItem -Path $DropFolder -Filter $Filter -File -ErrorAction SilentlyContinue
if (-not $files) { Write-Host "[receive] aucun fichier ($Filter) dans $DropFolder"; exit 0 }

$ok = 0; $ko = 0
foreach ($f in $files) {
    # GARDE (audit judge) : RIG parse les args par ';' (AmiSystem.ListeParametre). Un ';' dans le
    # chemin/nom tronquerait silencieusement PathFichier -> on ecarte ces fichiers explicitement.
    if ($f.FullName -match ';') {
        $ko++; Write-Host "[receive] SKIP (nom/chemin contient ';' incompatible arg RIG) : $($f.Name)"; continue
    }
    # RecevoirFichier prend un chemin LOCAL + le CodeEdi EXPLICITE (aucun canal/nommage requis).
    & $exe "${modeDebug}PathFichier=$($f.FullName);CodeEdi=$CodeEdi"
    if ($LASTEXITCODE -eq 0) { $ok++; Write-Host "[receive] OK  $($f.Name)" }
    else { $ko++; Write-Host "[receive] KO ($LASTEXITCODE)  $($f.Name)" }
}
Write-Host "[receive] termine : $ok OK / $ko KO (RecevoirFichier supprime les sources recues)."
# NB : les FICHIER_ENTRANT crees ici seront ensuite transformes par TraiterFichier (gate1b tache 2)
#      puis integres en DEMAT_RAPTURE par IntegrerRig (gate1b tache 3).

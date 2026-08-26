# =============================================================================
# rapture-edi-demo-live.ps1 — Demo LIVE du flux EDI Rapture sur une VRAIE audience.
# -----------------------------------------------------------------------------
# Depose un JSON Rapture dans le dossier EDI, deroule les 3 etapes de production
# (reception -> traitement -> integration) et LAISSE les donnees stagees dans
# DEMAT_RAPTURE pour que tu puisses les MONTRER dans RIG (RAPTUVAL / l'audience).
#
# Chaine : depot JSON -> gate1a (EdiEntrantRecevoirFichier -> FICHIER_ENTRANT)
#          -> EdiEntrantTraiterFichier (-> FLUX_ENTRANT)
#          -> EdiEntrantIntegrerRig    (-> DEMAT_RAPTURE, avec RAPTU_FLUX).
#
# Idempotent : nettoie le staging existant de l'audience AVANT (re-jouable a volonte).
# Par defaut : NE nettoie PAS a la fin (donnees visibles pour la demo).
#   -Rehearse    : nettoie tout a la fin (test net-zero, pour repeter sans laisser de trace).
#   -CleanupOnly : nettoie seulement le staging de l'audience puis sort (a lancer APRES la demo).
#
# ATTENTION : lance les VRAIS exes batch (C:\rig\exe\EdiLot3). Base EDI = DEVEDIGREFFE
#   (ModeDebug=d, JAMAIS la prod). Ecrit ~2 lignes d'audit EDI_EXECUTION dans COMMON
#   (COMMUN_RIG partage, NON nettoyable) : benin (audit technique), a lancer sciemment.
#
# Usage demo (garde les donnees a montrer) :
#   powershell -ExecutionPolicy Bypass -File rapture-edi-demo-live.ps1
# Autre audience / fixture :
#   powershell -ExecutionPolicy Bypass -File rapture-edi-demo-live.ps1 -Aud 28596 -Fixture cas-a-pc-ouvertures-16-affaires.json
# Repetition net-zero :
#   powershell -ExecutionPolicy Bypass -File rapture-edi-demo-live.ps1 -Rehearse
# Nettoyage post-demo :
#   powershell -ExecutionPolicy Bypass -File rapture-edi-demo-live.ps1 -CleanupOnly
# =============================================================================
param(
    [int]    $Aud        = 28590,
    [string] $Fixture    = 'cas-a-contentieux-10-affaires.json',
    [string] $Greffe     = '9995',
    [string] $CodeEdi    = 'RAPTURE_RETAUD',
    [string] $DropFolder = 'C:\rig\edi-in\rapture',
    [string] $ExeDir     = 'C:\rig\exe\EdiLot3',
    [switch] $Rehearse,
    [switch] $CleanupOnly
)
$ErrorActionPreference = 'Stop'
$gate1a  = Join-Path $PSScriptRoot 'edi-rapture-deploy\gate1a-edi-rapture-receive-folder.ps1'
$srcFix  = Join-Path $PSScriptRoot ("Rig.Wpf.Kbis.TestViewer\RaptureScenarios\{0}" -f $Fixture)
$jsonOut = Join-Path $DropFolder ("demo-{0}.json" -f $Aud)

function SqlDev($q){ (& sqlcmd -S 'SQL-DEV\DEV' -d RIG_DEV -E -C -W -h -1 -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; $q" 2>&1) -join "`n" }
function SqlEdi($q){ (& sqlcmd -S 'DEVBD5' -d DEV_EDI_GREFFE -E -C -W -h -1 -Q "SET NOCOUNT ON; $q" 2>&1) -join "`n" }
# poll-until : l'exe batch commit affaire-par-affaire -> on attend un compte STABLE (cap 25s).
function WaitCount([scriptblock]$q,[int]$min,[int]$cap=25){ $dl=(Get-Date).AddSeconds($cap); $prev=-1; do{ $n=[int](& $q).Trim(); if($n -ge $min -and $n -eq $prev){return $n}; $prev=$n; Start-Sleep -Milliseconds 500 }while((Get-Date) -lt $dl); return $n }  # sleep-ok: frequence de poll <=500ms
function Clean(){ SqlDev "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud" | Out-Null
                 SqlEdi "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null }

if ($CleanupOnly) {
    Write-Host "== CLEANUP-ONLY (audience $Aud + $CodeEdi) =="
    Clean
    "  DEMAT_RAPTURE(audience $Aud) restant = " + (SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud").Trim()
    Write-Host "Nettoye. (l'audit EDI_EXECUTION en COMMON reste, benin.)"
    exit 0
}

foreach ($f in @($gate1a,(Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe'),(Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe'),$srcFix)) {
    if (-not (Test-Path $f)) { throw "Prerequis absent : $f" }
}

Write-Host "===================================================================="
Write-Host (" DEMO EDI RAPTURE  ->  audience REELLE {0} (greffe {1}), fixture {2}" -f $Aud,$Greffe,$Fixture)
Write-Host (" Mode : {0}" -f $(if($Rehearse){'REHEARSE (net-zero a la fin)'}else{'LIVE (garde les donnees a montrer)'}))
Write-Host "===================================================================="

Write-Host "== [1] depot du JSON dans $DropFolder (id_audience=$Aud injecte) =="
Clean   # part propre : efface tout staging precedent de cette audience
if (-not (Test-Path $DropFolder)) { New-Item -ItemType Directory -Force -Path $DropFolder | Out-Null }
$json = [System.IO.File]::ReadAllText($srcFix)
if ($json -notmatch '"id_audience"') { $json = $json -replace '("codeGreffe"\s*:\s*"[^"]*"\s*,)', ('$1'+"`r`n    "+('"id_audience": {0},' -f $Aud)) }
[System.IO.File]::WriteAllText($jsonOut, $json, (New-Object System.Text.UTF8Encoding($true)))
"  depose : $jsonOut"

Write-Host "== [2] RECEPTION (gate1a -> FICHIER_ENTRANT) =="
& $gate1a -DropFolder $DropFolder -CodeEdi $CodeEdi -Dev 2>&1 | ForEach-Object { '  ' + $_ }
$nbFic = [int](SqlEdi "SELECT COUNT(*) FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi'").Trim()
"  FICHIER_ENTRANT = $nbFic"
if ($nbFic -lt 1) { throw "RECEPTION KO" }

Write-Host "== [3] TRAITEMENT (EdiEntrantTraiterFichier -> FLUX_ENTRANT) =="
Push-Location $ExeDir; $eap=$ErrorActionPreference; $ErrorActionPreference='Continue'
& (Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe') "ModeDebug=d;CodeEdi=$CodeEdi" *> $null
$ErrorActionPreference=$eap; Pop-Location
$nbFlux = WaitCount { SqlEdi "SELECT COUNT(*) FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'" } 1
"  FLUX_ENTRANT = $nbFlux"
if ($nbFlux -lt 1) { throw "TRAITEMENT KO" }

Write-Host "== [4] INTEGRATION (EdiEntrantIntegrerRig -> DEMAT_RAPTURE) =="
Push-Location $ExeDir; $eap=$ErrorActionPreference; $ErrorActionPreference='Continue'
& (Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe') "ModeDebug=d;CodeEdi=$CodeEdi" *> $null
$ErrorActionPreference=$eap; Pop-Location
$staged   = WaitCount { SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud" } 1
$avecFlux = [int](SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud AND DATALENGTH(RAPTU_FLUX)>0").Trim()
"  DEMAT_RAPTURE (audience $Aud) = $staged affaire(s), dont $avecFlux avec RAPTU_FLUX"
if ($staged -lt 1 -or $staged -ne $avecFlux) { throw "INTEGRATION KO (staged=$staged avecFlux=$avecFlux)" }

if ($Rehearse) {
    Write-Host "== [5] REHEARSE : cleanup net-zero =="
    Clean
    "  etat final DEMAT_RAPTURE(audience $Aud) = " + (SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud").Trim()
    Write-Host "REHEARSE OK : flux complet valide, rien laisse (sauf audit EDI_EXECUTION en COMMON)."
} else {
    Write-Host "===================================================================="
    Write-Host (" PASS : {0} affaire(s) Rapture stagee(s) sur l'audience {1}." -f $staged,$Aud) -ForegroundColor Green
    Write-Host " -> Va MONTRER dans RIG : ouvre RAPTUVAL (validation Rapture) sur l'audience $Aud du greffe $Greffe."
    Write-Host "    (verif SQL directe : SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud  -> $staged)"
    Write-Host " -> APRES la demo, nettoie : rapture-edi-demo-live.ps1 -Aud $Aud -CleanupOnly"
    Write-Host "===================================================================="
}

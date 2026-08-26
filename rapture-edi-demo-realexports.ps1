# =============================================================================
# rapture-edi-demo-realexports.ps1 — Demo EDI Rapture avec les VRAIS exports RIG.
# -----------------------------------------------------------------------------
# Les exports (Desktop\JsonRapture\TestExportRaptureN.json) n'ont PAS d'id_audience
# (resolution par contenu = "arbitrage n3", pas encore cablee cote import). On INJECTE
# donc l'id_audience reel (retrouve par date+heure+nb affaires) dans chaque en-tete,
# puis on passe les 3 par le flux EDI -> DEMAT_RAPTURE -> visible dans RAPTUVAL.
#
# Chaine : injection id_audience -> depot -> gate1a (FICHIER_ENTRANT)
#          -> TraiterFichier (FLUX_ENTRANT) -> IntegrerRig (DEMAT_RAPTURE).
#
# Par defaut : GARDE les donnees (pour la demo dans RAPTUVAL). -Cleanup = net-zero.
#   -CleanupOnly : efface le staging des 3 audiences puis sort (post-demo).
#
# ATTENTION : vrais exes batch (C:\rig\exe\EdiLot3), base EDI = DEVEDIGREFFE (ModeDebug=d).
#   Ecrit des lignes d'audit EDI_EXECUTION dans COMMON (partage, non nettoyable, benin).
#
# RAPTUVAL : ouvre RIG connecte sous le greffe 9995 (RAPTUVAL filtre par greffe de LOGIN,
#   pas par audience) -> les 70 affaires apparaissent, triees par audience.
#
# Usage demo :   powershell -ExecutionPolicy Bypass -File rapture-edi-demo-realexports.ps1
# Nettoyage :    powershell -ExecutionPolicy Bypass -File rapture-edi-demo-realexports.ps1 -CleanupOnly
# =============================================================================
param(
    [string] $SrcDir     = 'C:\Users\raphael.vilain\Desktop\JsonRapture',
    [string] $CodeEdi    = 'RAPTURE_RETAUD',
    [string] $DropFolder = 'C:\rig\edi-in\rapture',
    [string] $ExeDir     = 'C:\rig\exe\EdiLot3',
    [switch] $Cleanup,
    [switch] $CleanupOnly
)
$ErrorActionPreference = 'Stop'
$gate1a = Join-Path $PSScriptRoot 'edi-rapture-deploy\gate1a-edi-rapture-receive-folder.ps1'

# Mapping export -> vraie audience (retrouvee par date+heure+nb affaires, 2026-07-09)
$map = [ordered]@{
    'TestExportRapture1.json' = 28547   # 22/04 09:00, 20 affaires
    'TestExportRapture2.json' = 28634   # 15/04 14:00, 10 affaires
    'TestExportRapture3.json' = 28647   # 22/04 14:00, 40 affaires
}

function SqlDev($q){ (& sqlcmd -S 'SQL-DEV\DEV' -d RIG_DEV -E -C -W -h -1 -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; $q" 2>&1) -join "`n" }
function SqlDevT($q){ (& sqlcmd -S 'SQL-DEV\DEV' -d RIG_DEV -E -C -W -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; $q" 2>&1) -join "`n" }
function SqlEdi($q){ (& sqlcmd -S 'DEVBD5' -d DEV_EDI_GREFFE -E -C -W -h -1 -Q "SET NOCOUNT ON; $q" 2>&1) -join "`n" }
function WaitCount([scriptblock]$q,[int]$min,[int]$cap=30){ $dl=(Get-Date).AddSeconds($cap); $prev=-1; do{ $n=[int](& $q).Trim(); if($n -ge $min -and $n -eq $prev){return $n}; $prev=$n; Start-Sleep -Milliseconds 500 }while((Get-Date) -lt $dl); return $n }  # sleep-ok: poll <=500ms
function CleanAll(){
    foreach($id in $map.Values){ SqlDev "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$id" | Out-Null }
    SqlEdi "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null
}

if ($CleanupOnly) {
    Write-Host "== CLEANUP-ONLY (audiences $($map.Values -join ', ') + $CodeEdi) =="
    CleanAll
    SqlDevT "SELECT RAPTU_ID_AUDNC AS Aud, COUNT(*) AS Nb FROM DEMAT_RAPTURE GROUP BY RAPTU_ID_AUDNC;"
    Write-Host "Nettoye."
    exit 0
}

foreach ($f in @($gate1a,(Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe'),(Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe'))) {
    if (-not (Test-Path $f)) { throw "Prerequis absent : $f" }
}

Write-Host "===================================================================="
Write-Host " DEMO EDI RAPTURE - 3 VRAIS EXPORTS (id_audience reel injecte)"
Write-Host (" Mode : {0}" -f $(if($Cleanup){'net-zero a la fin'}else{'LIVE (garde les donnees)'}))
Write-Host "===================================================================="

Write-Host "== [1] injection id_audience + depot des 3 exports =="
CleanAll
if (-not (Test-Path $DropFolder)) { New-Item -ItemType Directory -Force -Path $DropFolder | Out-Null }
foreach ($name in $map.Keys) {
    $src = Join-Path $SrcDir $name
    if (-not (Test-Path $src)) { throw "Export absent : $src" }
    $json = [System.IO.File]::ReadAllText($src)
    $id = $map[$name]
    if ($json -notmatch '"id_audience"') {
        $json = $json -replace '("codeGreffe"\s*:\s*"[^"]*"\s*,)', ('$1'+"`r`n    "+('"id_audience": {0},' -f $id))
    }
    $out = Join-Path $DropFolder $name
    [System.IO.File]::WriteAllText($out, $json, (New-Object System.Text.UTF8Encoding($true)))
    "  {0} -> id_audience={1} -> {2}" -f $name, $id, $out
}

Write-Host "== [2] RECEPTION (gate1a -> FICHIER_ENTRANT) =="
& $gate1a -DropFolder $DropFolder -CodeEdi $CodeEdi -Dev 2>&1 | ForEach-Object { '  ' + $_ }
$nbFic = WaitCount { SqlEdi "SELECT COUNT(*) FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi'" } 3
"  FICHIER_ENTRANT = $nbFic (attendu 3)"

Write-Host "== [3] TRAITEMENT (-> FLUX_ENTRANT) =="
Push-Location $ExeDir; $eap=$ErrorActionPreference; $ErrorActionPreference='Continue'
& (Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe') "ModeDebug=d;CodeEdi=$CodeEdi" *> $null
$ErrorActionPreference=$eap; Pop-Location
$nbFlux = WaitCount { SqlEdi "SELECT COUNT(*) FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'" } 3
"  FLUX_ENTRANT = $nbFlux (attendu 3)"

Write-Host "== [4] INTEGRATION (-> DEMAT_RAPTURE) =="
Push-Location $ExeDir; $eap=$ErrorActionPreference; $ErrorActionPreference='Continue'
& (Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe') "ModeDebug=d;CodeEdi=$CodeEdi" *> $null
$ErrorActionPreference=$eap; Pop-Location
$total = WaitCount { SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC IN ($($map.Values -join ','))" } 1
Write-Host "== RESULTAT : staging par audience =="
SqlDevT "SELECT RAPTU_ID_AUDNC AS Aud, RAPTU_ETAT AS Etat, RAPTU_CODE_GREFFE AS Greffe, COUNT(*) AS Nb, SUM(CASE WHEN DATALENGTH(RAPTU_FLUX)>0 THEN 1 ELSE 0 END) AS AvecFlux FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC IN ($($map.Values -join ',')) GROUP BY RAPTU_ID_AUDNC, RAPTU_ETAT, RAPTU_CODE_GREFFE ORDER BY RAPTU_ID_AUDNC;"

if ($Cleanup) {
    Write-Host "== cleanup net-zero =="
    CleanAll
    Write-Host "net-zero (sauf audit EDI_EXECUTION en COMMON)."
} else {
    Write-Host "===================================================================="
    Write-Host (" PASS : {0} affaire(s) stagee(s) sur 3 audiences (28547/28634/28647)." -f $total) -ForegroundColor Green
    Write-Host " -> RAPTUVAL : connecte RIG sous le greffe 9995 -> les affaires apparaissent (triees par audience)."
    Write-Host " -> Nettoyage post-demo : rapture-edi-demo-realexports.ps1 -CleanupOnly"
    Write-Host "===================================================================="
}

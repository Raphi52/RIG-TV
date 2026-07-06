# =============================================================================
# Scenario RIG-TV : flux EDI Rapture COMPLET par depot dossier (les VRAIS exes).
# -----------------------------------------------------------------------------
# Rejoue la chaine de production entiere :
#   depot JSON dans un dossier -> gate1a (EdiEntrantRecevoirFichier -> FICHIER_ENTRANT)
#   -> EdiEntrantTraiterFichier (-> FLUX_ENTRANT) -> EdiEntrantIntegrerRig (-> DEMAT_RAPTURE).
# Assertions a chaque etage + net-zero final (audience jetable).
#
# ⚠ DIFFERENCE avec rapture-edi-import.ps1 (console dev) : ce scenario lance les VRAIS
#   exes batch (C:\rig\exe\EdiLot3), qui s'enveloppent dans EdiExecution -> ECRIVENT
#   ~1 ligne d'audit EDI_EXECUTION par exe dans COMMON (COMMUN_RIG = partage, non nettoyable
#   par ce script). Benin (audit technique), mais a lancer SCIEMMENT, pas en batterie.
#   ModeDebug=d est TOUJOURS passe -> la base EDI ciblee est DEVEDIGREFFE (jamais la prod).
#
# Usage : powershell -ExecutionPolicy Bypass -File rapture-edi-folder-flow.ps1
# Exit 0 = flux complet PASS ; exit 1 = un etage KO.
# =============================================================================
param(
    [int]    $Aud = 88888,
    [string] $Greffe = '9995',
    [string] $CodeEdi = 'RAPTURE_RETAUD',
    [string] $DropFolder = 'C:\rig\edi-in\rapture',
    [string] $ExeDir = 'C:\rig\exe\EdiLot3',
    [string] $Gate1a = "$PSScriptRoot\edi-rapture-deploy\gate1a-edi-rapture-receive-folder.ps1"
)
$ErrorActionPreference = 'Stop'
$srcFix = Join-Path $PSScriptRoot 'Rig.Wpf.Kbis.TestViewer\RaptureScenarios\cas-a-contentieux-10-affaires.json'
$jsonOut = Join-Path $DropFolder ("flux-test-{0}.json" -f $Aud)
function SqlDev($q){ (& sqlcmd -S 'SQL-DEV\DEV' -d RIG_DEV -E -C -W -h -1 -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; $q" 2>&1) -join "`n" }
function SqlEdi($q){ (& sqlcmd -S 'DEVBD5' -d DEV_EDI_GREFFE -E -C -W -h -1 -Q "SET NOCOUNT ON; $q" 2>&1) -join "`n" }
# poll-until : l'exe batch commit affaire-par-affaire -> on attend un compte STABLE (>=min et
# 2 lectures consecutives egales), sinon on lit une valeur mi-ecriture (cap 25s).
function WaitCount([scriptblock]$query, [int]$min, [int]$capSec = 25) {
    $deadline = (Get-Date).AddSeconds($capSec)
    $prev = -1
    do {
        $n = [int](& $query).Trim()
        if ($n -ge $min -and $n -eq $prev) { return $n }   # stable + au-dessus du plancher
        $prev = $n
        Start-Sleep -Milliseconds 500   # sleep-ok: frequence de poll (<=500ms), pas une attente fixe
    } while ((Get-Date) -lt $deadline)
    return $n
}

$fail = $false
try {
    foreach ($f in @($Gate1a, (Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe'), (Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe'), $srcFix)) {
        if (-not (Test-Path $f)) { throw "Prerequis absent : $f" }
    }
    Write-Host "==== [0] clean slate ===="
    SqlDev "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud" | Out-Null
    SqlEdi "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null

    Write-Host "==== [1] depot du JSON dans $DropFolder ===="
    if (-not (Test-Path $DropFolder)) { New-Item -ItemType Directory -Force -Path $DropFolder | Out-Null }
    $json = [System.IO.File]::ReadAllText($srcFix)
    if ($json -notmatch '"id_audience"') { $json = $json -replace '("codeGreffe"\s*:\s*"[^"]*"\s*,)', ('$1'+"`r`n    "+('"id_audience": {0},' -f $Aud)) }
    [System.IO.File]::WriteAllText($jsonOut, $json, (New-Object System.Text.UTF8Encoding($true)))

    Write-Host "==== [2] RECEPTION (gate1a -> FICHIER_ENTRANT) ===="
    & $Gate1a -DropFolder $DropFolder -CodeEdi $CodeEdi -Dev | Out-String | Write-Host
    $nbFic = [int](SqlEdi "SELECT COUNT(*) FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi'").Trim()
    $srcGone = -not (Test-Path $jsonOut)
    Write-Host ("  [check] FICHIER_ENTRANT={0} sourceConsommee={1}" -f $nbFic, $srcGone)
    if ($nbFic -lt 1 -or -not $srcGone) { $fail = $true; throw "RECEPTION KO" }

    Write-Host "==== [3] TRAITEMENT (TraiterFichier -> FLUX_ENTRANT) ===="
    Push-Location $ExeDir
    $eap=$ErrorActionPreference; $ErrorActionPreference='Continue'
    & (Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe') "ModeDebug=d;CodeEdi=$CodeEdi" *> $null
    $ErrorActionPreference=$eap
    Pop-Location
    $nbFlux = WaitCount { SqlEdi "SELECT COUNT(*) FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'" } 1
    Write-Host ("  [check] FLUX_ENTRANT={0}" -f $nbFlux)
    if ($nbFlux -lt 1) { $fail = $true; throw "TRAITEMENT KO" }

    Write-Host "==== [4] INTEGRATION (IntegrerRig -> DEMAT_RAPTURE) ===="
    Push-Location $ExeDir
    $eap=$ErrorActionPreference; $ErrorActionPreference='Continue'
    & (Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe') "ModeDebug=d;CodeEdi=$CodeEdi" *> $null
    $ErrorActionPreference=$eap
    Pop-Location
    $staged = WaitCount { SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud" } 1
    $avecFlux = [int](SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud AND DATALENGTH(RAPTU_FLUX)>0").Trim()
    Write-Host ("  [check] staged={0} avecFlux={1}" -f $staged, $avecFlux)
    if ($staged -lt 1 -or $staged -ne $avecFlux) { $fail = $true; throw "INTEGRATION KO (staged=$staged avecFlux=$avecFlux)" }

    Write-Host ("PASS : flux dossier complet -> {0} affaire(s) stagee(s), toutes avec RAPTU_FLUX." -f $staged) -ForegroundColor Green
} catch {
    $fail = $true
    Write-Host ("FAIL : {0}" -f $_.Exception.Message) -ForegroundColor Red
} finally {
    try {
        SqlDev "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$Aud" | Out-Null
        SqlEdi "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null
        Remove-Item $jsonOut -ErrorAction SilentlyContinue
        Write-Host "[cleanup] net-zero (staging + FICHIER/FLUX + depot). L'audit EDI_EXECUTION en COMMON reste (voir en-tete)."
    } catch { Write-Host "[cleanup] WARN: $_" -ForegroundColor Yellow }
}
if ($fail) { exit 1 } else { exit 0 }

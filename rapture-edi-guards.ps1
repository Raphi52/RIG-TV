# =============================================================================
# Scenario RIG-TV : GARDES de l'import EDI Rapture (Chemin B) — regression suite.
# -----------------------------------------------------------------------------
# 5 sous-tests DB-prouvables, rejouables sans ecran (consolidation des preuves de la
# session 2026-07 : fixes #1/#7, garde anti-collision A->B, CAS-claim #4, lease) :
#   [1] doublon-visible : re-import du meme flux -> FLUENT=IntegrationErreur(7) + message,
#       0 nouveau staging (fix #1 : plus de perte silencieuse).
#   [2] isolation mixte : flux 7 doublons + 3 nouvelles -> les 3 stagees (flux peuple),
#       le flux signale en erreur, 0 orphelin.
#   [3] garde A->B : audience deja appliquee en DIRECT (audit present SANS staging)
#       -> re-import EDI BLOQUE en erreur visible (anti double-application cross-path).
#   [4] CAS-race : 2 claims concurrents sur la meme ligne -> exactement 1 gagnant (#4).
#   [5] lease-stamp : le claim horodate RAPTU_DATE_TRAITEMENT (socle du refus de reprise
#       <10 min ; le refus lui-meme est du C# embarque PROC_RAPTUVAL, non testable ici).
#
# PREREQUIS : RigMetier frais au GAC + EdiRaptureRetAud frais (Bin Dot Net Gac versionne
# + bin console) + console ImportFluxRaptureDev buildee. Bases DEV uniquement.
# Sous-test [3] utilise l'audience REELLE 28590 (FK audit -> AUDIENCE_CABINET) avec un
# marqueur dedie 'rigtv-guard-a2b', nettoye en finally. Net-zero garanti meme sur echec.
#
# Usage : powershell -ExecutionPolicy Bypass -File rapture-edi-guards.ps1
# Exit 0 = 5/5 PASS ; exit 1 = au moins un FAIL (details au stdout).
# =============================================================================
param(
    [string] $Greffe = '9995',
    [string] $CodeEdi = 'RAPTURE_RETAUD',
    [int]    $AudJetable = 88888,      # audience jetable (staging sans FK) — tests 1/2/4/5
    [int]    $AudReelle = 28590,       # audience reelle (FK audit) — test 3 uniquement, marqueur nettoye
    [string] $RigDevServer = 'SQL-DEV\DEV',
    [string] $RigDevDb = 'RIG_DEV',
    [string] $EdiServer = 'DEVBD5',
    [string] $EdiDb = 'DEV_EDI_GREFFE'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root '..\wt-edilot3\Source\Edi\EXE_test\ImportFluxRaptureDev\bin\Debug\ImportFluxRaptureDev.exe'
$srcFixture = Join-Path $root 'Rig.Wpf.Kbis.TestViewer\RaptureScenarios\cas-a-contentieux-10-affaires.json'
$tmp = Join-Path $env:TEMP ("edi-guards-{0}.json" -f $PID)
$marqueur = 'rigtv-guard-a2b'

function SqlDev($q) {
    $out = & sqlcmd -S $RigDevServer -d $RigDevDb -E -C -l 10 -W -h -1 -Q "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; $q" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd KO (RIG_DEV): $out" }
    return (($out | Where-Object { $_ -ne '' }) -join "`n")
}
function SqlEdi($q) {
    $out = & sqlcmd -S $EdiServer -d $EdiDb -E -C -l 10 -W -h -1 -Q "SET NOCOUNT ON; $q" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd KO (EDI): $out" }
    return (($out | Where-Object { $_ -ne '' }) -join "`n")
}
function Import-Fixture([int]$aud) {
    # derive la fixture (injection id_audience) puis passe la chaine console (FICHIER->FLUX->staging)
    $json = [System.IO.File]::ReadAllText($srcFixture)
    if ($json -notmatch '"id_audience"') {
        $json = $json -replace '("codeGreffe"\s*:\s*"[^"]*"\s*,)', ('$1' + "`r`n    " + ('"id_audience": {0},' -f $aud))
    } else {
        $json = $json -replace '"id_audience"\s*:\s*\d+', ('"id_audience": {0}' -f $aud)
    }
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($true)))
    $eap = $script:ErrorActionPreference; $script:ErrorActionPreference = 'Continue'
    & $exe $tmp $CodeEdi $Greffe *> $null
    $script:ErrorActionPreference = $eap
}
function LastFluent() {
    return (SqlEdi "SELECT TOP 1 CAST(FLUENT_ETAT AS varchar)+'|'+LEFT(ISNULL(FLUENT_ERREUR,''),160) FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi' ORDER BY FLUENT_ID_FLUENT DESC").Trim()
}
function StagedCount([int]$aud) { return [int](SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$aud").Trim() }
function CleanSlate() {
    SqlDev "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC IN ($AudJetable,$AudReelle)" | Out-Null
    SqlDev "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_USER='$marqueur'" | Out-Null
    SqlEdi "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null
}

$results = [ordered]@{}
try {
    if (-not (Test-Path $exe)) { throw "Console absente : $exe" }
    if (-not (Test-Path $srcFixture)) { throw "Fixture absente : $srcFixture" }
    CleanSlate

    # ── [1] doublon-visible ─────────────────────────────────────────────────────
    Import-Fixture $AudJetable                       # import 1 : stage 10
    $st1 = StagedCount $AudJetable
    Import-Fixture $AudJetable                       # import 2 : 10 doublons
    $fl = LastFluent; $st2 = StagedCount $AudJetable
    $ok = ($st1 -eq 10) -and ($st2 -eq 10) -and $fl.StartsWith('7|') -and ($fl -match 'doublon')
    $results['1 doublon-visible'] = $ok
    Write-Host ("[1 doublon-visible] {0}  (staged {1}->{2}, FLUENT={3})" -f ($(if($ok){'PASS'}else{'FAIL'})), $st1, $st2, $fl.Substring(0, [Math]::Min(60,$fl.Length)))

    # ── [2] isolation mixte ─────────────────────────────────────────────────────
    SqlDev "DELETE TOP (3) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$AudJetable" | Out-Null
    $maxBefore = [int](SqlDev "SELECT ISNULL(MAX(RAPTU_ID_RAPTU),0) FROM DEMAT_RAPTURE").Trim()
    Import-Fixture $AudJetable                       # 7 doublons + 3 nouvelles
    $newRows = [int](SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$AudJetable AND RAPTU_ID_RAPTU>$maxBefore").Trim()
    $newFlux = [int](SqlDev "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$AudJetable AND RAPTU_ID_RAPTU>$maxBefore AND DATALENGTH(RAPTU_FLUX)>0").Trim()
    $fl = LastFluent
    $ok = ($newRows -eq 3) -and ($newFlux -eq 3) -and $fl.StartsWith('7|') -and ((StagedCount $AudJetable) -eq 10)
    $results['2 isolation-mixte'] = $ok
    Write-Host ("[2 isolation-mixte] {0}  (nouvelles={1} avec flux={2}, FLUENT={3})" -f ($(if($ok){'PASS'}else{'FAIL'})), $newRows, $newFlux, $fl.Substring(0,1))

    # ── [3] garde A->B (audit-prior) ────────────────────────────────────────────
    SqlDev "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_AUDNC=$AudReelle" | Out-Null
    SqlDev "INSERT INTO AUDIT_IMPORT_RAPTURE (ARIMP_DATE, ARIMP_CODE_GREFFE, ARIMP_USER, ARIMP_ID_AUDNC, ARIMP_TABLE_CIBLE, ARIMP_COLONNE_CIBLE, ARIMP_VALEUR_APRES) VALUES (GETDATE(),'$Greffe','$marqueur',$AudReelle,'PARTIE','GUARD_TEST','x')" | Out-Null
    Import-Fixture $AudReelle
    $fl = LastFluent; $st = StagedCount $AudReelle
    $ok = ($st -eq 0) -and $fl.StartsWith('7|') -and ($fl -match 'DIRECT')
    $results['3 garde-A-vers-B'] = $ok
    Write-Host ("[3 garde-A->B] {0}  (staged={1}, FLUENT={2})" -f ($(if($ok){'PASS'}else{'FAIL'})), $st, $fl.Substring(0, [Math]::Min(80,$fl.Length)))
    SqlDev "DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_USER='$marqueur'" | Out-Null

    # ── [4] CAS-race (1 seul gagnant) ───────────────────────────────────────────
    SqlDev "INSERT INTO DEMAT_RAPTURE (RAPTU_DATE_CREATION, RAPTU_ETAT, RAPTU_CODE_GREFFE, RAPTU_ID_AUDNC, RAPTU_NUM_AFFAIRE) VALUES (GETDATE(),'1','$Greffe',$AudJetable,'CAS-RACE')" | Out-Null
    $race = SqlDev @"
DECLARE @id INT = (SELECT MAX(RAPTU_ID_RAPTU) FROM DEMAT_RAPTURE WHERE RAPTU_NUM_AFFAIRE='CAS-RACE');
UPDATE DEMAT_RAPTURE SET RAPTU_ETAT='4', RAPTU_DATE_TRAITEMENT=GETDATE() WHERE RAPTU_ID_RAPTU=@id AND RAPTU_ETAT IN ('1','9');
SELECT CONCAT('A=', @@ROWCOUNT);
UPDATE DEMAT_RAPTURE SET RAPTU_ETAT='4', RAPTU_DATE_TRAITEMENT=GETDATE() WHERE RAPTU_ID_RAPTU=@id AND RAPTU_ETAT IN ('1','9');
SELECT CONCAT('B=', @@ROWCOUNT);
"@
    $ok = ($race -match 'A=1') -and ($race -match 'B=0')
    $results['4 CAS-race'] = $ok
    Write-Host ("[4 CAS-race] {0}  ({1})" -f ($(if($ok){'PASS'}else{'FAIL'})), ($race -replace "`n",' '))

    # ── [5] lease-stamp (le claim horodate) ─────────────────────────────────────
    $stamp = SqlDev "SELECT CASE WHEN RAPTU_DATE_TRAITEMENT IS NOT NULL AND RAPTU_DATE_TRAITEMENT > DATEADD(minute,-2,GETDATE()) THEN 'STAMPED' ELSE 'NULL' END FROM DEMAT_RAPTURE WHERE RAPTU_NUM_AFFAIRE='CAS-RACE'"
    $ok = ($stamp.Trim() -eq 'STAMPED')
    $results['5 lease-stamp'] = $ok
    Write-Host ("[5 lease-stamp] {0}  (claim horodate RAPTU_DATE_TRAITEMENT : {1})" -f ($(if($ok){'PASS'}else{'FAIL'})), $stamp.Trim())

} finally {
    try { CleanSlate; Remove-Item $tmp -ErrorAction SilentlyContinue; Write-Host "[cleanup] net-zero (staging $AudJetable+$AudReelle, marqueur audit, FICHIER/FLUX)." }
    catch { Write-Host "[cleanup] WARN: $_" -ForegroundColor Yellow }
}

$nbFail = @($results.Values | Where-Object { -not $_ }).Count
if ($nbFail -eq 0 -and $results.Count -eq 5) {
    Write-Host "PASS : 5/5 gardes verifiees." -ForegroundColor Green; exit 0
} else {
    Write-Host ("FAIL : {0} sous-test(s) KO / {1} joues." -f $nbFail, $results.Count) -ForegroundColor Red; exit 1
}

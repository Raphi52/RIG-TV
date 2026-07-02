# =============================================================================
# Scenario RIG-TV : import RAPTURE par EDI (Chemin B) de bout en bout, en DEV, net-zero.
# -----------------------------------------------------------------------------
# Rejoue le flux automatique EDI SANS l'exe batch (donc sans audit prod) via la
# console dev ImportFluxRaptureDev (wt-edilot3) :
#   fixture JSON -> FICHIER_ENTRANT -> TraiterFichier -> FLUX_ENTRANT -> IntegrerRig -> DEMAT_RAPTURE
# Assertion = des lignes DEMAT_RAPTURE (staging, etat ATraiter) sont creees en RIG_DEV.
# Cleanup systematique (net-zero) : DELETE des lignes staging + FICHIER/FLUX de test.
#
# PREREQUIS pour PASS : le RigMetier FRAIS (avec le type DematRapture) doit etre au
# VRAI GAC Windows (deploy post-merge / RigToGac admin). Sinon la phase 2 (IntegrerRig)
# leve TypeLoadException(DematRapture) -> 0 ligne creee -> le scenario le signale
# explicitement (NOT-DEPLOYED) au lieu d'un echec obscur.
#
# Usage : powershell -ExecutionPolicy Bypass -File rapture-edi-import.ps1
# =============================================================================
param(
    [string] $Greffe = '9995',
    [string] $CodeEdi = 'RAPTURE_RETAUD',
    [int]    $IdAudience = 28590,
    [string] $RigDevServer = 'SQL-DEV\DEV',
    [string] $RigDevDb = 'RIG_DEV',
    [string] $EdiServer = 'DEVBD5',
    [string] $EdiDb = 'DEV_EDI_GREFFE'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root '..\wt-edilot3\Source\Edi\EXE_test\ImportFluxRaptureDev\bin\Debug\ImportFluxRaptureDev.exe'
$srcFixture = Join-Path $root 'Rig.Wpf.Kbis.TestViewer\RaptureScenarios\cas-a-contentieux-10-affaires.json'
$tmp = Join-Path $env:TEMP ("edi-rapture-import-{0}.json" -f $PID)

function Sql($server, $db, $query) {
    $out = & sqlcmd -S $server -d $db -E -C -l 10 -W -h -1 -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd KO ($server/$db): $out" }
    return ($out | Where-Object { $_ -ne '' })
}
function SqlInt($server, $db, $query) {
    $r = "$((Sql $server $db $query) | Select-Object -Last 1)".Trim()
    if ($r -notmatch '^\d+$') { throw "Reponse SQL inattendue (attendu un entier) : '$r'" }
    return [int]$r
}

$fail = $false
try {
    if (-not (Test-Path $exe)) { throw "Console absente : $exe (builder ImportFluxRaptureDev.csproj en Debug)" }
    if (-not (Test-Path $srcFixture)) { throw "Fixture source absente : $srcFixture" }

    # 1. Deriver la fixture EDI = fixture Chemin A + injection du pivot id_audience (absent des fixtures A).
    $json = [System.IO.File]::ReadAllText($srcFixture)
    if ($json -notmatch '"id_audience"') {
        $json = $json -replace '("codeGreffe"\s*:\s*"[^"]*"\s*,)', ('$1' + "`r`n    " + ('"id_audience": {0},' -f $IdAudience))
    }
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "[fixture] $tmp (id_audience=$IdAudience)"

    # 2. Snapshot DEMAT_RAPTURE (pour isoler les lignes creees par CE run).
    $maxBefore = SqlInt $RigDevServer $RigDevDb "SELECT ISNULL(MAX(RAPTU_ID_RAPTU),0) FROM DEMAT_RAPTURE"
    Write-Host "[snapshot] DEMAT_RAPTURE max id avant = $maxBefore"

    # 3. Lancer l'import EDI (console dev-safe).
    #    ErrorActionPreference=Continue localement : en PS5.1 la stderr d'un exe natif (ex. le
    #    TypeLoadException attendu si non deploye) devient sinon une erreur TERMINANTE. On la capture.
    $eapPrev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $stdout = (& $exe $tmp $CodeEdi $Greffe 2>&1 | Out-String)
    $ErrorActionPreference = $eapPrev
    Write-Host "[console]`n$stdout"

    # 4. Assertion : des lignes staging ont ete creees.
    $created = SqlInt $RigDevServer $RigDevDb "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE RAPTU_ID_RAPTU > $maxBefore AND RAPTU_ID_AUDNC = $IdAudience"
    Write-Host "[assert] lignes DEMAT_RAPTURE creees = $created"

    if ($created -gt 0) {
        Write-Host "PASS : import EDI Rapture -> $created ligne(s) staging (audience $IdAudience, etat ATraiter)." -ForegroundColor Green
    } else {
        $fail = $true
        if (("$stdout" -match 'TypeLoadException') -and ("$stdout" -match 'DematRapture')) {
            Write-Host "NOT-DEPLOYED : phase 2 (IntegrerRig) bloquee = RigMetier frais (DematRapture) PAS au vrai GAC." -ForegroundColor Yellow
            Write-Host "  -> deployer le RigMetier frais au GAC (post-merge / RigToGac admin) pour que ce scenario passe." -ForegroundColor Yellow
        } else {
            Write-Host "FAIL : aucune ligne staging creee (voir sortie console)." -ForegroundColor Red
        }
    }
} finally {
    # 5. Cleanup net-zero (toujours) : lignes staging de CE run + FICHIER/FLUX de test + temp.
    try {
        if ($null -ne $maxBefore) {
            # Scope IDENTIQUE a l'assertion (id>snapshot ET audience) : ne JAMAIS toucher les lignes
            # d'un autre process/audience sur la base dev PARTAGEE (bug corrige suite audit judge).
            Sql $RigDevServer $RigDevDb "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_RAPTU > $maxBefore AND RAPTU_ID_AUDNC = $IdAudience" | Out-Null
        }
        Sql $EdiServer $EdiDb "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null
        Remove-Item $tmp -ErrorAction SilentlyContinue
        Write-Host "[cleanup] net-zero : staging + FICHIER/FLUX de test supprimes, fixture temp retiree."
    } catch { Write-Host "[cleanup] WARN: $_" -ForegroundColor Yellow }
}
if ($fail) { exit 1 } else { exit 0 }

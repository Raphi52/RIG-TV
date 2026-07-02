# =============================================================================
# Scenario RIG-TV : import RAPTURE par EDI (Chemin B) de bout en bout, en DEV.
# -----------------------------------------------------------------------------
# Rejoue le flux automatique EDI SANS l'exe batch (donc sans audit prod) via la
# console dev ImportFluxRaptureDev (wt-edilot3) :
#   fixture JSON -> FICHIER_ENTRANT -> TraiterFichier -> FLUX_ENTRANT -> IntegrerRig -> DEMAT_RAPTURE
# Assertions : (a) des lignes DEMAT_RAPTURE (staging, etat ATraiter) sont creees,
#              (b) RAPTU_FLUX est PEUPLE sur chacune (sinon la Validation greffier
#                  echouerait "Flux JSON introuvable" -> garde-fou de non-regression).
# Cleanup net-zero par defaut (DELETE staging + FICHIER/FLUX de test) ; -KeepData
# laisse les lignes en base pour montrer le cockpit RAPTUVAL (demo).
#
# PREREQUIS pour PASS : le RigMetier FRAIS (avec DematRapture) doit etre au VRAI GAC
# Windows (deploy post-merge / RigToGac admin). Sinon phase 2 (IntegrerRig) leve
# TypeLoadException(DematRapture) -> 0 ligne -> NOT-DEPLOYED (signale, pas d'echec obscur).
#
# Usage :
#   powershell -ExecutionPolicy Bypass -File rapture-edi-import.ps1
#   ... -KeepData                 # garde les lignes staging (demo cockpit) au lieu de nettoyer
#   ... -Fixture <chemin.json>    # fixture EDI custom (ex. alignee sur une vraie audience -> apply reel)
# =============================================================================
param(
    [string] $Greffe = '9995',
    [string] $CodeEdi = 'RAPTURE_RETAUD',
    [int]    $IdAudience = 28590,
    [string] $Fixture = '',          # fixture EDI custom (chemin) ; vide = derivee de cas-a-contentieux-10
    [switch] $KeepData,              # ne PAS nettoyer (laisse le staging visible au cockpit pour une demo)
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
    if ($r -notmatch '^-?\d+$') { throw "Reponse SQL inattendue (attendu un entier) : '$r'" }
    return [int]$r
}

$fail = $false
$derivedTmp = $false   # true si on a genere une fixture temporaire a nettoyer
try {
    if (-not (Test-Path $exe)) { throw "Console absente : $exe (builder ImportFluxRaptureDev.csproj en Debug)" }

    # 1. Fixture : soit fournie (-Fixture, ex. alignee sur une vraie audience -> apply reel),
    #    soit derivee de cas-a-contentieux-10 (+ injection du pivot id_audience absent des fixtures A).
    if ($Fixture -ne '') {
        if (-not (Test-Path $Fixture)) { throw "Fixture fournie introuvable : $Fixture" }
        $fixToUse = $Fixture
        Write-Host "[fixture] (fournie) $Fixture"
    } else {
        if (-not (Test-Path $srcFixture)) { throw "Fixture source absente : $srcFixture" }
        $json = [System.IO.File]::ReadAllText($srcFixture)
        if ($json -notmatch '"id_audience"') {
            $json = $json -replace '("codeGreffe"\s*:\s*"[^"]*"\s*,)', ('$1' + "`r`n    " + ('"id_audience": {0},' -f $IdAudience))
        }
        [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($true)))
        $fixToUse = $tmp; $derivedTmp = $true
        Write-Host "[fixture] (derivee cas-a-contentieux-10, id_audience=$IdAudience) $tmp"
    }

    # 2. Snapshot DEMAT_RAPTURE (pour isoler les lignes creees par CE run).
    $maxBefore = SqlInt $RigDevServer $RigDevDb "SELECT ISNULL(MAX(RAPTU_ID_RAPTU),0) FROM DEMAT_RAPTURE"
    Write-Host "[snapshot] DEMAT_RAPTURE max id avant = $maxBefore"

    # 3. Lancer l'import EDI (console dev-safe).
    #    ErrorActionPreference=Continue localement : en PS5.1 la stderr d'un exe natif (ex. le
    #    TypeLoadException attendu si non deploye) devient sinon une erreur TERMINANTE. On la capture.
    $eapPrev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $stdout = (& $exe $fixToUse $CodeEdi $Greffe 2>&1 | Out-String)
    $ErrorActionPreference = $eapPrev
    Write-Host "[console]`n$stdout"

    # 4. Assertions : lignes creees + RAPTU_FLUX peuple (garde-fou du gate "Flux JSON introuvable").
    $scope = "RAPTU_ID_RAPTU > $maxBefore AND RAPTU_ID_AUDNC = $IdAudience"
    $created = SqlInt $RigDevServer $RigDevDb "SELECT COUNT(*) FROM DEMAT_RAPTURE WHERE $scope"
    Write-Host "[assert] lignes DEMAT_RAPTURE creees = $created"

    if ($created -gt 0) {
        $minFlux = SqlInt $RigDevServer $RigDevDb "SELECT ISNULL(MIN(DATALENGTH(RAPTU_FLUX)),0) FROM DEMAT_RAPTURE WHERE $scope"
        Write-Host "[assert] RAPTU_FLUX min (octets) = $minFlux"
        Write-Host "[detail] affaires importees (numero | etat | flux octets) :"
        Sql $RigDevServer $RigDevDb "SELECT RAPTU_NUM_AFFAIRE, RAPTU_ETAT, DATALENGTH(RAPTU_FLUX) FROM DEMAT_RAPTURE WHERE $scope ORDER BY RAPTU_ID_RAPTU" | ForEach-Object { Write-Host "  $_" }
        if ($minFlux -gt 0) {
            Write-Host "PASS : import EDI -> $created ligne(s) staging, RAPTU_FLUX peuple (Validation greffier possible)." -ForegroundColor Green
        } else {
            $fail = $true
            Write-Host "FAIL (REGRESSION) : lignes creees mais RAPTU_FLUX VIDE -> la Validation greffier echouera 'Flux JSON introuvable'." -ForegroundColor Red
        }
    } else {
        $fail = $true
        if (("$stdout" -match 'TypeLoadException') -and ("$stdout" -match 'DematRapture')) {
            Write-Host "NOT-DEPLOYED : phase 2 (IntegrerRig) bloquee = RigMetier frais (DematRapture) PAS au vrai GAC." -ForegroundColor Yellow
            Write-Host "  -> deployer le RigMetier frais au GAC (post-merge / RigToGac admin) pour que ce scenario passe." -ForegroundColor Yellow
        } else {
            Write-Host "FAIL : aucune ligne staging creee (voir sortie console)." -ForegroundColor Red
        }
    }

    if ($KeepData -and $created -gt 0) {
        Write-Host "[keep-data] lignes staging CONSERVEES (audience $IdAudience) pour la demo cockpit RAPTUVAL." -ForegroundColor Cyan
        Write-Host "  -> cleanup manuel ensuite : DELETE FROM DEMAT_RAPTURE WHERE $scope ; + FICHIER/FLUX CodeEdi='$CodeEdi'." -ForegroundColor Cyan
    }
} finally {
    if ($KeepData) {
        if ($derivedTmp) { Remove-Item $tmp -ErrorAction SilentlyContinue }  # la fixture temp seulement
        Write-Host "[cleanup] -KeepData : staging CONSERVE (pas de net-zero)."
    } else {
        # Cleanup net-zero : lignes staging de CE run + FICHIER/FLUX de test + fixture temp.
        try {
            if ($null -ne $maxBefore) {
                # Scope IDENTIQUE a l'assertion (id>snapshot ET audience) : ne JAMAIS toucher les lignes
                # d'un autre process/audience sur la base dev PARTAGEE (bug corrige suite audit judge).
                Sql $RigDevServer $RigDevDb "DELETE FROM DEMAT_RAPTURE WHERE RAPTU_ID_RAPTU > $maxBefore AND RAPTU_ID_AUDNC = $IdAudience" | Out-Null
            }
            Sql $EdiServer $EdiDb "DELETE FROM FLUX_ENTRANT WHERE FLUENT_CODE_EDI='$CodeEdi'; DELETE FROM FICHIER_ENTRANT WHERE FICENT_CODE_EDI='$CodeEdi';" | Out-Null
            if ($derivedTmp) { Remove-Item $tmp -ErrorAction SilentlyContinue }
            Write-Host "[cleanup] net-zero : staging + FICHIER/FLUX de test supprimes."
        } catch { Write-Host "[cleanup] WARN: $_" -ForegroundColor Yellow }
    }
}
if ($fail) { exit 1 } else { exit 0 }

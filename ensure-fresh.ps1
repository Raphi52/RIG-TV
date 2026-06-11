# Verifie que le binaire d'un projet .NET est plus recent que ses sources.
# Si stale -> rebuild auto. Si build fail -> throw (le pilote ne lance pas un binaire stale).
# Usage : powershell -File ensure-fresh.ps1 -Project TestViewer
#         powershell -File ensure-fresh.ps1 -Project SmokeRunner
#         powershell -File ensure-fresh.ps1 -Project TestViewer -Config Release

param(
    [Parameter(Mandatory)] [string] $Project,
    [ValidateSet('Debug','Release')] [string] $Config = 'Debug'
)
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot   # repo root (RIG-TV) ; auto-correct dans chaque worktree
$projectDir = Join-Path $repoRoot ("Rig.Wpf.Kbis.{0}" -f $Project)
$csproj = Join-Path $projectDir ("Rig.Wpf.Kbis.{0}.csproj" -f $Project)
$exe = Join-Path $projectDir ("bin\{0}\net48\Rig.Wpf.Kbis.{1}.exe" -f $Config, $Project)

if (-not (Test-Path $csproj)) { throw ("csproj introuvable : {0}" -f $csproj) }

# Collecte les sources du projet cible (hors bin/obj).
$sources = Get-ChildItem $projectDir -Recurse -Include *.cs,*.xaml,*.csproj -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

# FIX dependance transitoire : quand on cible TestViewer, son .csproj reference SmokeRunner
# (ProjectReference). dotnet build rebuildera aussi SmokeRunner. Si une source SmokeRunner
# est plus recente que l'exe TestViewer, on doit declarer TestViewer STALE.
if ($Project -eq 'TestViewer') {
    $smokeDir = Join-Path $repoRoot 'Rig.Wpf.Kbis.SmokeRunner'
    if (Test-Path $smokeDir) {
        $smokeSrc = Get-ChildItem $smokeDir -Recurse -Include *.cs,*.csproj -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
        if ($smokeSrc) {
            # Fusionner les deux collections pour le calcul du max
            $sources = @($sources) + @($smokeSrc)
        }
    }
}

$stale = $false
if (-not (Test-Path $exe)) {
    Write-Host ("[{0}] binaire absent - build initial" -f $Project) -ForegroundColor Yellow
    $stale = $true
} else {
    $exeTime = (Get-Item $exe).LastWriteTime
    if (-not $sources) {
        Write-Host ("[{0}] aucune source detectee - skip check" -f $Project) -ForegroundColor Yellow
    } else {
        $maxSrc = ($sources | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        $stale = $maxSrc.LastWriteTime -gt $exeTime
        if ($stale) {
            Write-Host ("[{0}] STALE : exe={1:yyyy-MM-dd HH:mm:ss} src={2:yyyy-MM-dd HH:mm:ss} ({3})" -f $Project, $exeTime, $maxSrc.LastWriteTime, $maxSrc.Name) -ForegroundColor Yellow
        } else {
            Write-Host ("[{0}] fresh : exe={1:yyyy-MM-dd HH:mm:ss} (max src={2:yyyy-MM-dd HH:mm:ss})" -f $Project, $exeTime, $maxSrc.LastWriteTime) -ForegroundColor Green
        }
    }
}

if ($stale) {
    Write-Host ("[{0}] dotnet build -c {1} -v minimal ..." -f $Project, $Config)
    $buildOutput = & dotnet build $csproj -c $Config -v minimal 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $buildOutput | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
        throw ("[{0}] BUILD FAIL (exit={1}). Pilote stoppe - fix les erreurs avant de relancer." -f $Project, $exitCode)
    }
    if (-not (Test-Path $exe)) { throw ("[{0}] build OK mais exe absent : {1}" -f $Project, $exe) }
    $newExeTime = (Get-Item $exe).LastWriteTime
    Write-Host ("[{0}] BUILD OK : exe={1:yyyy-MM-dd HH:mm:ss}" -f $Project, $newExeTime) -ForegroundColor Green
}

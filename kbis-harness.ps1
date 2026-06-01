# ─────────────────────────────────────────────────────────────────────────────
# kbis-harness.ps1 — Bibliotheque de fonctions VETTEES pour piloter TestViewer /
# SmokeRunner KBIS. Dot-source : . "C:\Code RIG\kbis-harness.ps1" puis appeler les fonctions.
#
# Pourquoi : eviter de re-ecrire des scripts inline qui re-introduisent les MEMES bugs PS 5.1
# (incident 2026-05-29, cf. OPERATIONS.md "Pieges PowerShell harnais") :
#   - tiret cadratin (em-dash) dans une string double-quote => parse error. ICI : ASCII only.
#   - operateur ternaire ?: inexistant en PS 5.1 => if/else partout.
#   - capture stdout via Register-ObjectEvent : flush non fiable => Start-Process -RedirectStandardOutput.
#   - "premature capture" : matcher d'anciens marqueurs du log journalier / un vieux run-dir =>
#     toujours BASELINER un compteur puis attendre l'INCREMENT (Wait-MarkerCountAbove).
# ─────────────────────────────────────────────────────────────────────────────

$ErrorActionPreference = 'Stop'

$global:KbisRepo   = 'C:\Code RIG\RigApplication-testing\Source\Wpf'
$global:KbisTvDir  = "$env:LOCALAPPDATA\rig-wpf-testviewer"
$global:KbisCapPs1 = 'C:\Code RIG\Audit\screenshots-loop\kbis-tvdrive-B2\capture-tv.ps1'

function Get-KbisExe {
    param([ValidateSet('TestViewer','SmokeRunner')] [string]$Project, [ValidateSet('Debug','Release')] [string]$Config = 'Debug')
    return Join-Path $global:KbisRepo "Rig.Wpf.Kbis.$Project\bin\$Config\net48\Rig.Wpf.Kbis.$Project.exe"
}

function Kill-RigProcs {
    # Tue tout l'ecosysteme (RIG legacy + workers + viewers PDF + TV). A appeler entre 2 runs.
    Get-Process | Where-Object {
        $_.ProcessName -match 'Rig.Wpf.Kbis.TestViewer|RigClientAccueil|Rig.Wpf.Kbis.SmokeRunner|RigAffichageDoc|Acrobat|AcroRd'
    } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

function Get-MarkerCount {
    # Nombre d'occurrences d'un marqueur (regex) dans un fichier log. 0 si absent/illisible.
    param([Parameter(Mandatory)] [string]$Log, [Parameter(Mandatory)] [string]$Marker)
    if (-not (Test-Path $Log)) { return 0 }
    return (Get-Content $Log -ErrorAction SilentlyContinue | Select-String $Marker | Measure-Object).Count
}

function Wait-MarkerCountAbove {
    # Attend que le nb d'occurrences du marqueur DEPASSE $Baseline (= nouvelle occurrence apparue).
    # ROBUSTE au log journalier qui contient deja d'anciens marqueurs : on baseline AVANT l'action.
    param(
        [Parameter(Mandatory)] [string]$Log,
        [Parameter(Mandatory)] [string]$Marker,
        [Parameter(Mandatory)] [int]$Baseline,
        [int]$TimeoutSec = 240,
        [int]$PollMs = 1500
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Get-MarkerCount -Log $Log -Marker $Marker) -gt $Baseline) { return $true }
        Start-Sleep -Milliseconds $PollMs
    }
    return $false
}

function Launch-TV {
    # Lance la TestViewer, attend 'InitializeAsync done', retourne @{Pid; Log; Stamp}.
    param([string]$Module = 'KBIS', [ValidateSet('Debug','Release')] [string]$Config = 'Debug')
    $exe = Get-KbisExe -Project TestViewer -Config $Config
    if (-not (Test-Path $exe)) { throw "TestViewer exe absent : $exe (build d'abord)" }
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $env:RIG_DRIVER_HEADLESS = '1'
    $env:RIG_TV_MODULE = $Module
    $env:RIG_RUN_STAMP = $stamp
    $logBefore = Get-ChildItem $global:KbisTvDir -Filter 'testviewer-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $baseInit = 0
    if ($logBefore) { $baseInit = Get-MarkerCount -Log $logBefore.FullName -Marker 'InitializeAsync done' }
    $p = Start-Process -FilePath $exe -PassThru
    $log = $null
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl -and -not $log) {
        Start-Sleep -Milliseconds 400
        $log = (Get-ChildItem $global:KbisTvDir -Filter 'testviewer-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    if ($log) { [void](Wait-MarkerCountAbove -Log $log -Marker 'InitializeAsync done' -Baseline $baseInit -TimeoutSec 30 -PollMs 400) }
    return @{ Pid = $p.Id; Log = $log; Stamp = $stamp }
}

function Capture-TV {
    # Screenshot (PrintWindow) de la fenetre TV vers un PNG.
    param([Parameter(Mandatory)] [int]$TvPid, [Parameter(Mandatory)] [string]$OutPng)
    & powershell -ExecutionPolicy Bypass -File $global:KbisCapPs1 $TvPid $OutPng 2>$null
    return $OutPng
}

function Invoke-TileButton {
    # Invoke (UIA) un bouton de la fenetre TV par AutomationId (regex), SCOPE A LA FENETRE
    # (pas RootElement : enumeration desktop-wide = lente + jette ElementNotAvailable).
    param([Parameter(Mandatory)] [int]$TvPid, [Parameter(Mandatory)] [string]$AidRegex, [int]$TimeoutSec = 15)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $proc = Get-Process -Id $TvPid -ErrorAction SilentlyContinue
    if (-not $proc) { throw "TV PID $TvPid introuvable" }
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $dl = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $dl) {
        try {
            foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
                if ($b.Current.AutomationId -match $AidRegex) {
                    $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    return $b.Current.AutomationId
                }
            }
        } catch { }
        Start-Sleep -Milliseconds 400
    }
    throw "Bouton AutomationId ~ '$AidRegex' introuvable dans la fenetre TV"
}

function Invoke-RailModule {
    # Selectionne un module du rail gauche par LIBELLE (les ToggleButton du rail n'ont pas d'AutomationId).
    # Cherche un Button dont le Name OU un descendant Text contient $ModuleName, puis Toggle (fallback Invoke).
    param([Parameter(Mandatory)] [int]$TvPid, [Parameter(Mandatory)] [string]$ModuleName, [int]$TimeoutSec = 15)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $proc = Get-Process -Id $TvPid -ErrorAction SilentlyContinue
    if (-not $proc) { throw "TV PID $TvPid introuvable" }
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $txtCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    if (-not ([System.Management.Automation.PSTypeName]'Kbis.RailClick').Type) {
        Add-Type -Namespace Kbis -Name RailClick -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, System.IntPtr e);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
public static void Front(System.IntPtr h){ SetForegroundWindow(h); }
public static void Click(int x, int y){ SetCursorPos(x,y); mouse_event(0x0002,0,0,0,System.IntPtr.Zero); mouse_event(0x0004,0,0,0,System.IntPtr.Zero); }
'@
    }
    $dl = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $dl) {
        try {
            # Le libelle du rail est un Text ; on prend le PLUS A GAUCHE (= rail, pas le contenu du module).
            $best = $null
            foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $txtCond)) {
                if ($t.Current.Name -match $ModuleName) {
                    $r = $t.Current.BoundingRectangle
                    # X >= 0 : ignore les textes du module COLLAPSED (X tres negatif = offscreen) ; on veut
                    # le libelle RAIL (le plus a gauche PARMI les textes a l'ecran).
                    if ($r.Width -gt 0 -and $r.X -ge 0 -and ($null -eq $best -or $r.X -lt $best.X)) {
                        $best = [pscustomobject]@{ X = $r.X; CX = [int]($r.X + $r.Width / 2); CY = [int]($r.Y + $r.Height / 2) }
                    }
                }
            }
            if ($best) {
                # Le Command du rail se declenche sur un CLIC reel (pas Toggle/DoDefaultAction).
                [Kbis.RailClick]::Front($proc.MainWindowHandle)
                Start-Sleep -Milliseconds 250
                [Kbis.RailClick]::Click($best.CX, $best.CY)
                return "clic rail @ $($best.CX),$($best.CY)"
            }
        } catch { }
        Start-Sleep -Milliseconds 400
    }
    throw "Module rail '$ModuleName' introuvable dans la fenetre TV"
}

function Run-Scenario {
    # Lance un scenario SmokeRunner en CLI, stdout REDIRIGE VERS FICHIER (fiable, vs events).
    # Retourne @{Exit; Log}. NumGestion/Headless overridables.
    param(
        [Parameter(Mandatory)] [string]$Arg,           # ex. '--legacy-kbis-vk'
        [Parameter(Mandatory)] [string]$OutLog,
        [string]$NumGestion = '2024B00001',
        [int]$TimeoutSec = 180,
        [ValidateSet('Debug','Release')] [string]$Config = 'Debug'
    )
    $exe = Get-KbisExe -Project SmokeRunner -Config $Config
    if (-not (Test-Path $exe)) { throw "SmokeRunner exe absent : $exe" }
    $env:RIG_DRIVER_HEADLESS = '1'
    $env:RIG_LEGACY_NUM_GESTION = $NumGestion
    $env:RIG_RUN_STAMP = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $p = Start-Process -FilePath $exe -ArgumentList $Arg -RedirectStandardOutput $OutLog -NoNewWindow -PassThru
    $p.WaitForExit($TimeoutSec * 1000) | Out-Null
    if (-not $p.HasExited) { $p.Kill() }
    return @{ Exit = $p.ExitCode; Log = $OutLog }
}

Write-Host "kbis-harness.ps1 charge. Fonctions : Kill-RigProcs, Launch-TV, Capture-TV, Invoke-TileButton, Run-Scenario, Wait-MarkerCountAbove, Get-MarkerCount."

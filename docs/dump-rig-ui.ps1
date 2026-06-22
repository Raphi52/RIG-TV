<#
.SYNOPSIS
  Lance RigClientAccueil -> tente le login -> DUMP UIA de l'arbre de la fenetre active (AutomationId/Type/Name).
  Outil de DECOUVERTE d'un ecran : capture les controles UIA reels d'une fenetre RIG.
  LIMITE (verifie 2026-06-22) : atteint la fenetre de connexion BD mais ne franchit PAS le login multi-etapes
  (la fenetre BD n'expose que des aids numeriques dynamiques + Name vide, cf. RIG-UI-MAP.md P12) -> le login
  robuste est le role de LegacyDriver.Launch()/ClickSeConnecter(). Pour mapper un ecran NEUF en pratique :
  amener RIG sur l'ecran via un smoke (--legacy-*) qui utilise le driver, PUIS dumper la fenetre active.
  La verif LIVE de bout en bout = un smoke reel (ex. --legacy-kbis-vk), pas ce script seul. Mail-safe (login seul).
  ASCII-only (PS5.1).
.PARAMETER DumpOut  Fichier de sortie du dump (defaut Audit\rig-ui-dump.txt).
.PARAMETER NoLogin  Ne clique pas "Se connecter" (dump l'ecran de login seulement).
.OUTPUTS  exit 0 = console atteinte + dump ecrit ; exit 1 = echec lancement/login/console.
#>
[CmdletBinding()]
param(
    [string]$Exe = 'C:\rig\exe\RigClientAccueil.exe',
    [string]$DumpOut = 'C:\Code RIG\Audit\rig-ui-dump.txt',
    [switch]$NoLogin
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { Write-Host "ROUGE: exe introuvable ($Exe)" -ForegroundColor Red; exit 1 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$AE = [System.Windows.Automation.AutomationElement]
$AID = $AE::AutomationIdProperty

function Find-ById($root, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AID, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-LoginButton($root) {
    # btnOk d'abord, sinon un controle dont le Name ressemble a un bouton de connexion/login.
    $b = Find-ById $root 'btnOk'; if ($b) { return $b }
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true)))
    foreach ($e in $all) {
        $n = ''; try { $n = $e.Current.Name } catch {}
        if ($n -match '(?i)se connecter|connexion|connecter|^ok$|valider') { return $e }
    }
    return $null
}

# Kill instances residuelles (evite de dumper la mauvaise fenetre).
Get-Process RigClientAccueil -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$proc = $null
try {
    $proc = Start-Process $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
    Write-Host "Lance (pid=$($proc.Id)). Attente fenetre (boot COM/GAC ~lent)..."

    # (1) Attente fenetre principale (boot lourd).
    $win = $null; $dl = (Get-Date).AddSeconds(70)
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 600
        $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $p) { Write-Host "ROUGE: process sorti pendant le boot (crash COM/dependance ?)." -ForegroundColor Red; exit 1 }
        if ($p.MainWindowHandle -ne 0) { $win = $AE::FromHandle($p.MainWindowHandle); break }
    }
    if ($null -eq $win) { Write-Host "ROUGE: pas de fenetre apres 70s." -ForegroundColor Red; exit 1 }
    Start-Sleep -Seconds 2  # sleep-ok: settle WinForms post-affichage fenetre, aucun signal de fin a poller

    # (2+3) Login multi-etapes (dialog BD -> FormLogin -> Console) : a chaque tour, si console atteinte -> stop ;
    #       sinon clique le bouton de connexion/login disponible (btnOk OU Name se connecter/connexion/ok/valider).
    #       Mail-safe : aucune navigation PROC / audience / import.
    if (-not $NoLogin) {
        $reached = $false; $dl2 = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $dl2) {
            Start-Sleep -Milliseconds 800
            $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
            if (-not $p) { Write-Host "ROUGE: process sorti pendant le login." -ForegroundColor Red; exit 1 }
            if ($p.MainWindowHandle -eq 0) { continue }
            $cand = $AE::FromHandle($p.MainWindowHandle)
            if ((Find-ById $cand 'tabControl') -or (Find-ById $cand 'btn1') -or (Find-ById $cand 'lstProcessus')) { $win = $cand; $reached = $true; break }
            $lb = Find-LoginButton $cand
            if ($lb) {
                $bn = ''; try { $bn = $lb.Current.Name } catch {}
                try { $lb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Host "Clic login/connexion ('$bn')." } catch {}
            }
        }
        if ($reached) { Write-Host "Console d'accueil atteinte." -ForegroundColor Green }
        else { Write-Host "WARN: console non atteinte en 60s - dump de la fenetre courante." -ForegroundColor Yellow }
    }

    # (4) Dump l'arbre (AutomationId/ControlType/Name), cap profondeur+volume.
    $sb = New-Object System.Text.StringBuilder
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $count = 0
    function Walk($el, $depth) {
        if ($null -eq $el -or $script:count -gt 4000 -or $depth -gt 14) { return }
        try {
            $aid = $el.Current.AutomationId; $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.',''
            $nm = $el.Current.Name; if ($nm.Length -gt 60) { $nm = $nm.Substring(0,60) }
            if ($aid -or $nm) { [void]$sb.AppendLine(('  ' * $depth) + "[$ct] aid='$aid' name='$nm'") }
        } catch {}
        $script:count++
        $child = $walker.GetFirstChild($el)
        while ($null -ne $child) { Walk $child ($depth+1); $child = $walker.GetNextSibling($child) }
    }
    Walk $win 0
    $dumpDir = Split-Path $DumpOut; if (-not (Test-Path $dumpDir)) { New-Item -ItemType Directory -Force -Path $dumpDir | Out-Null }
    [System.IO.File]::WriteAllText($DumpOut, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Dump ecrit : $DumpOut ($count noeuds)."

    # (5) Verif live des IDs de navigation documentes.
    $navIds = @('btn1','btn2','btn3','lstSousmenu','lstProcessus','tabControl')
    $dump = [System.IO.File]::ReadAllText($DumpOut)
    Write-Host "--- Verif live AutomationIds navigation ---"
    $ok = $true
    foreach ($id in $navIds) {
        if ($dump -match [regex]::Escape("aid='$id'")) { Write-Host "LIVE-OK   $id" -ForegroundColor Green }
        else { Write-Host "LIVE-ABS  $id" -ForegroundColor Yellow; $ok = $false }
    }
    if ($ok) { Write-Host "Tous les IDs nav presents dans l'app vivante." -ForegroundColor Green }
    else { Write-Host "Certains IDs nav absents du dump (ecran courant != console, ou ID dynamique)." -ForegroundColor Yellow }
    exit 0
}
finally {
    if ($proc) { Get-Process RigClientAccueil -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
}

# =============================================================================
# GATE 1b — 3 taches planifiees = chaine EDI RAPTURE automatique (modele depot-dossier).
# -----------------------------------------------------------------------------
# A EXECUTER SUR LE SERVEUR EDI, EN ADMIN, APRES VALIDATION. NE PAS lancer sans :
#   (a) definir $DropFolder (le dossier de depot),
#   (b) aligner compte de service + frequence sur une chaine EDI existante (ex. JAL_JUD),
#   (c) autorisation.
#
# Chaine (chacune toutes les N min) :
#   1. RECEPTION  : gate1a-edi-rapture-receive-folder.ps1  (dossier -> FICHIER_ENTRANT via RecevoirFichier)
#   2. TRAITEMENT : EdiEntrantTraiterFichier.exe CodeEdi=RAPTURE_RETAUD  (FICHIER_ENTRANT -> FLUX_ENTRANT)
#   3. INTEGRATION: EdiEntrantIntegrerRig.exe    CodeEdi=RAPTURE_RETAUD  (FLUX_ENTRANT   -> DEMAT_RAPTURE)
#
# DEV/RECETTE : -Dev => ModeDebug=d (base EDI DEVEDIGREFFE). PROD : NE PAS mettre -Dev.
# ⚠ Sans ModeDebug=d, les exes ciblent la base EDI PROD (EDIGREFFE).
# =============================================================================
param(
    [Parameter(Mandatory)] [string] $DropFolder,               # A DEFINIR (dossier de depot des JSON)
    [string] $ExeDir        = 'C:\rig\exe\EdiLot3',
    [string] $ReceiveScript = "$PSScriptRoot\gate1a-edi-rapture-receive-folder.ps1",
    [string] $CodeEdi       = 'RAPTURE_RETAUD',
    [int]    $IntervalMin   = 5,                                # TODO aligner sur les taches EDI existantes
    [string] $ServiceUser   = 'NT AUTHORITY\SYSTEM',            # TODO aligner sur le compte des taches EDI
    [switch] $Dev
)
$ErrorActionPreference = 'Stop'
$md = if ($Dev) { 'ModeDebug=d;' } else { '' }
$devFlag = if ($Dev) { ' -Dev' } else { '' }
$psh = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

$tasks = @(
    @{ Name='EDI - RAPTURE - 1 Reception'; Exe=$psh;
       Args="-NoProfile -ExecutionPolicy Bypass -File `"$ReceiveScript`" -DropFolder `"$DropFolder`" -CodeEdi $CodeEdi$devFlag"; Wd=$PSScriptRoot },
    @{ Name='EDI - RAPTURE - 2 TraiterFichier'; Exe=(Join-Path $ExeDir 'EdiEntrantTraiterFichier.exe');
       Args="${md}CodeEdi=$CodeEdi"; Wd=$ExeDir },
    @{ Name='EDI - RAPTURE - 3 IntegrerRig'; Exe=(Join-Path $ExeDir 'EdiEntrantIntegrerRig.exe');
       Args="${md}CodeEdi=$CodeEdi"; Wd=$ExeDir }
)

foreach ($t in $tasks) {
    Unregister-ScheduledTask -TaskName $t.Name -Confirm:$false -ErrorAction SilentlyContinue
    $action  = New-ScheduledTaskAction -Execute $t.Exe -Argument $t.Args -WorkingDirectory $t.Wd
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $IntervalMin)
    $principal = New-ScheduledTaskPrincipal -UserId $ServiceUser -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask -TaskName $t.Name -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description "EDI RAPTURE ($CodeEdi) - chaine folder-drop. Genere par le kit RIG-TV."
    Write-Host "[OK] $($t.Name)  ->  $(Split-Path $t.Exe -Leaf) $($t.Args)  (toutes les $IntervalMin min)"
}
Write-Host "NB reception : le greffe est lu du CONTENU (dto.CodeGreffe) en phase 2 -> aucune convention de nommage requise."
# REVERT : Unregister-ScheduledTask -TaskName 'EDI - RAPTURE - 1 Reception' -Confirm:$false ; idem '2 TraiterFichier' / '3 IntegrerRig'.

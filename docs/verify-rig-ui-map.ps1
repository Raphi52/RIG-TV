<#
.SYNOPSIS
  Verifie que chaque AutomationId catalogue dans RIG-UI-MAP.md existe REELLEMENT dans LegacyDriver.cs.
  = garde anti-derive : un ID fabrique / renomme / supprime du driver est detecte (la map mentirait).
  A relancer apres toute evolution de LegacyDriver.cs. ASCII-only (PS5.1).
.OUTPUTS  exit 0 = tous presents ; exit 1 = au moins un absent (liste).
#>
$ErrorActionPreference = 'Stop'
$map    = 'C:\Code RIG\RIG-TV\docs\RIG-UI-MAP.md'
$driver = 'C:\Code RIG\RIG-TV\Rig.Wpf.Kbis.SmokeRunner\LegacyDriver.cs'
if (-not (Test-Path $map))    { Write-Host "ROUGE: map introuvable ($map)" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $driver)) { Write-Host "ROUGE: driver introuvable ($driver)" -ForegroundColor Red; exit 1 }

$drv   = Get-Content $driver -Raw
$lines = Get-Content $map

# Extrait les tokens entre backticks de la 1re colonne du tableau de la section "## 2. Catalogue AutomationIds".
$inCat = $false
$ids   = New-Object System.Collections.Generic.List[string]
foreach ($l in $lines) {
    if ($l -match '^##\s*2\.')            { $inCat = $true;  continue }
    if ($inCat -and $l -match '^##\s*3\.'){ break }
    if ($inCat -and $l -match '^\|\s*`') {
        $cell = ($l -split '\|')[1]
        foreach ($m in [regex]::Matches($cell, '`([^`]+)`')) { $ids.Add($m.Groups[1].Value) }
    }
}

if ($ids.Count -eq 0) { Write-Host "ROUGE: aucun AutomationId extrait du catalogue (format du tableau change ?)" -ForegroundColor Red; exit 1 }

$missing = New-Object System.Collections.Generic.List[string]
$ok = 0
foreach ($id in ($ids | Select-Object -Unique)) {
    # Present comme literal de chaine OU bare (les btnN peuvent etre construits dynamiquement $"btn{i}").
    $found = $drv.Contains('"' + $id + '"') -or $drv.Contains($id)
    if ($found) { Write-Host ("VERT     {0}" -f $id) -ForegroundColor Green; $ok++ }
    else        { Write-Host ("MANQUANT {0}" -f $id) -ForegroundColor Red; $missing.Add($id) }
}

Write-Host ""
if ($missing.Count -gt 0) {
    Write-Host ("ECHEC: {0} AutomationId(s) absent(s) de LegacyDriver.cs : {1}" -f $missing.Count, ($missing -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host ("OK: les {0} AutomationIds du catalogue existent tous dans LegacyDriver.cs." -f $ok) -ForegroundColor Green
exit 0

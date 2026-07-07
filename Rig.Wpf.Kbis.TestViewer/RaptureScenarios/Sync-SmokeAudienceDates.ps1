# ============================================================================
# Sync-SmokeAudienceDates.ps1
# ----------------------------------------------------------------------------
# Corrige le "data-drift fenetre RETAUD" : les audiences smoke (28590/28625/28644)
# ont des dates FIXES qui finissent par sortir de la fenetre RETAUD active a
# mesure que les jours reels passent. Quand une audience est hors fenetre, le
# driver ne la trouve pas dans la grille et tombe en fallback Cas C (creation)
# au lieu du Cas A attendu.
#
# Ce script REALIGNE les 3 audiences sur des dates recentes (today-2/-3/-4) ET
# reecrit le champ dateAudience des 3 JSON correspondants, pour que les deux
# cotes (DB + JSON) restent coherents -> Cas A direct garanti.
#
# Les AppelAffaire restent lies (on ne touche QUE AUDNC_DATE), donc les
# idInstance des JSON continuent de matcher.
#
# USAGE :
#   Dry-run (defaut, n'ecrit rien) :
#     powershell -ExecutionPolicy Bypass -File Sync-SmokeAudienceDates.ps1
#   Application reelle (UPDATE SQL + reecriture JSON) :
#     powershell -ExecutionPolicy Bypass -File Sync-SmokeAudienceDates.ps1 -Apply
#
# AUTORISATION : en mode -Apply, fait des UPDATE sur AUDIENCE_CABINET (SQL-DEV\DEV).
# A lancer manuellement apres revue. Idempotent : rejouable sans danger.
# ============================================================================
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$cs = 'Server=SQL-DEV\DEV;Database=RIG_DEV;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15;'
$scenDir = $PSScriptRoot

# Mapping : AUDNC_ID -> (JSON file, offset jours depuis aujourd'hui)
$map = @(
    @{ Id = 28590; Json = 'cas-a-contentieux-10-affaires.json'; Offset = -2 },
    @{ Id = 28625; Json = 'cas-a-mise-en-etat-72-affaires.json'; Offset = -3 },
    @{ Id = 28644; Json = 'cas-a-pc-clotures-44-affaires.json';  Offset = -4 },
    @{ Id = 28596; Json = 'cas-a-pc-ouvertures-16-affaires.json'; Offset = -5 }
)

$today = [datetime]::Today
Write-Host "=== Sync-SmokeAudienceDates ($(if($Apply){'APPLY'}else{'DRY-RUN'})) - today=$($today.ToString('yyyy-MM-dd')) ==="
Write-Host ""

$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()
try {
    foreach ($m in $map) {
        $targetDate = $today.AddDays($m.Offset)
        $targetIso = $targetDate.ToString('yyyy-MM-dd')

        # Lire l'etat actuel
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT AUDNC_DATE, AUDNC_HEURE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @id"
        $cmd.Parameters.AddWithValue('@id', $m.Id) | Out-Null
        $r = $cmd.ExecuteReader()
        if (-not $r.Read()) {
            Write-Host "  [SKIP] Audience $($m.Id) introuvable en base"
            $r.Close()
            continue
        }
        $curDate = ([datetime]$r['AUDNC_DATE']).ToString('yyyy-MM-dd')
        $curHeure = [string]$r['AUDNC_HEURE']
        $r.Close()

        Write-Host "  Audience $($m.Id) ($($m.Json))"
        Write-Host "    DB   : $curDate $curHeure  ->  cible $targetIso"

        if ($Apply) {
            $up = $conn.CreateCommand()
            $up.CommandText = "UPDATE AUDIENCE_CABINET SET AUDNC_DATE = @d WHERE AUDNC_ID_ADNC = @id"
            $up.Parameters.AddWithValue('@d', $targetDate) | Out-Null
            $up.Parameters.AddWithValue('@id', $m.Id) | Out-Null
            $n = $up.ExecuteNonQuery()
            Write-Host "    DB   : UPDATE applique ($n row)"
        }

        # JSON
        $jsonPath = Join-Path $scenDir $m.Json
        if (-not (Test-Path $jsonPath)) {
            Write-Host "    JSON : INTROUVABLE ($jsonPath)"
            continue
        }
        $raw = Get-Content $jsonPath -Raw
        $obj = $raw | ConvertFrom-Json
        $oldJsonDate = $obj.dateAudience
        Write-Host "    JSON : dateAudience=$oldJsonDate  ->  $targetIso"
        if ($Apply) {
            # Remplacement cible du seul champ dateAudience (1ere occurrence en tete)
            $newRaw = $raw -replace '("dateAudience"\s*:\s*")[^"]*(")', "`${1}$targetIso`${2}"
            Set-Content -Path $jsonPath -Value $newRaw -Encoding UTF8 -NoNewline
            Write-Host "    JSON : reecrit"
        }
        Write-Host ""
    }

    # ── Mur 1 (2026-07-07) : 28590 AUDNC_SECTION doit rester NULL. Sinon RIG crashe a l'ouverture
    #    (ChargerAudienceCabinet -> GetSECTION sur "audience interactive" tronquee -> invalide) en mode VISIBLE.
    #    Idempotent (ne touche que si non-null).
    if ($Apply) {
        $secCmd = $conn.CreateCommand()
        $secCmd.CommandText = "UPDATE AUDIENCE_CABINET SET AUDNC_SECTION = NULL WHERE AUDNC_ID_ADNC = 28590 AND AUDNC_SECTION IS NOT NULL"
        $secN = $secCmd.ExecuteNonQuery()
        Write-Host "  [Mur1] 28590 AUDNC_SECTION -> NULL ($secN row)"
    } else {
        Write-Host "  [Mur1] 28590 AUDNC_SECTION -> NULL (dry-run)"
    }

    # ── Mur 2 (2026-07-07) : les cas-err JSON tracent la date de 28590 (offset -2) pour single-match
    #    (sinon leur date stale matche plusieurs audiences -> popup "Choisir l'audience cible" -> pas de recap).
    $iso28590 = $today.AddDays(-2).ToString('yyyy-MM-dd')
    $casErrJsons = @(
        'cas-err-affaire-id-instance-manquant.json','cas-err-affaires-null.json','cas-err-affaires-vide.json',
        'cas-err-bloc-affaire-null.json','cas-err-greffe-inconnu.json','cas-err-greffe-manquant.json',
        'cas-err-idinstance-negatif.json','cas-err-idinstance-zero.json','cas-err-ouverture-pc-sans-bloc.json',
        'cas-err-pc-date-cessation-invalide.json','cas-err-pc-type-manquant.json','cas-err-type-decision-inconnu.json',
        'cas-diff-affaire-non-trouvee.json','cas-diff-sans-changement.json'
    )
    Write-Host "  [Mur2] cas-err JSON -> dateAudience $iso28590 (tracent 28590 ; json-malforme EXCLU, reste casse)"
    foreach ($cej in $casErrJsons) {
        $cp = Join-Path $scenDir $cej
        if (-not (Test-Path $cp)) { Write-Host "    [SKIP] $cej introuvable"; continue }
        if ($Apply) {
            $cr = Get-Content $cp -Raw
            $cr = $cr -replace '("dateAudience"\s*:\s*")[^"]*(")', "`${1}$iso28590`${2}"
            Set-Content -Path $cp -Value $cr -Encoding UTF8 -NoNewline
        }
    }
    Write-Host ""
}
finally {
    $conn.Close()
}

Write-Host "=== Termine ==="
if (-not $Apply) {
    Write-Host "DRY-RUN : rien n'a ete modifie. Relance avec -Apply pour appliquer."
} else {
    Write-Host "APPLY termine. Relance ensuite 'All scenarios' : les Cas A doivent router Cas A direct."
}

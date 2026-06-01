-- ============================================================================
-- Setup pour scénario cas-b-multi-match
-- ============================================================================
-- Crée un doublon de l'audience 28590 (contentieux 2026-05-22 14:30 greffe 9995)
-- → 2 audiences matchent désormais le header JSON cas-a-contentieux-10-affaires.json
-- → l'Orchestrator détecte multi-match → popup "Plusieurs audiences correspondent
--    au JSON. La 1ère est utilisée."
-- → le driver doit savoir cliquer OK puis vérifier qu'il a bien sélectionné la 1ère.
--
-- Le doublon hérite de TOUS les attributs (date, heure, chambre, section, type, état)
-- mais reçoit un nouvel AUDNC_ID_ADNC auto-assigné par identity.
--
-- L'AUDNC_ID du doublon est retourné par SELECT final → à conserver pour le
-- teardown (cas-b-multi-match.teardown.sql).
--
-- AUTORISATION : ce script INSERT sur SQL-DEV\DEV (RIG_DEV), partagé entre devs.
-- Le SmokeRunner exécute ce script AVANT le scénario (mode --cas-b-auto-setup)
-- via System.Data.SqlClient (PAS de GO, PAS de USE — SqlCommand single-batch).
-- ============================================================================

DECLARE @sourceId INT = 28590;

INSERT INTO AUDIENCE_CABINET (
    AUDNC_AUDIENCE,
    AUDNC_TYPE_AUD_CAB,
    AUDNC_TYPE_CLASSEMENT_ADMIS,
    AUDNC_DATE,
    AUDNC_HEURE,
    AUDNC_NOMBRE_APPELS,
    AUDNC_NUM_APPEL_ATTRIB,
    AUDNC_PUBLIQUE,
    AUDNC_CHAMBRE,
    AUDNC_SECTION,
    AUDNC_ETAT_AUDIENCE,
    AUDNC_DEBUT_REEL,
    AUDNC_FIN_REELLE,
    AUDNC_ID_JUG,
    AUDNC_ID_PRQTR,
    AUDNC_ID_GRASS,
    AUDNC_CODE_CABINET,
    AUDNC_ETAT_CABINET,
    AUDNC_TOP_DIFFUSION,
    AUDNC_DATE_DIFFUSION,
    AUDNC_NON_DISPONIBLE_WEB
)
SELECT
    AUDNC_AUDIENCE,
    AUDNC_TYPE_AUD_CAB,
    AUDNC_TYPE_CLASSEMENT_ADMIS,
    AUDNC_DATE,
    AUDNC_HEURE,
    AUDNC_NOMBRE_APPELS,
    AUDNC_NUM_APPEL_ATTRIB,
    AUDNC_PUBLIQUE,
    AUDNC_CHAMBRE,
    AUDNC_SECTION,
    AUDNC_ETAT_AUDIENCE,
    AUDNC_DEBUT_REEL,
    AUDNC_FIN_REELLE,
    AUDNC_ID_JUG,
    AUDNC_ID_PRQTR,
    AUDNC_ID_GRASS,
    AUDNC_CODE_CABINET,
    AUDNC_ETAT_CABINET,
    AUDNC_TOP_DIFFUSION,
    AUDNC_DATE_DIFFUSION,
    AUDNC_NON_DISPONIBLE_WEB
FROM AUDIENCE_CABINET
WHERE AUDNC_ID_ADNC = @sourceId;

DECLARE @newId INT = SCOPE_IDENTITY();
-- SELECT final = valeur lue par SmokeRunner.RunCasBSetupSql via ExecuteScalar
SELECT @newId AS DoublonId;

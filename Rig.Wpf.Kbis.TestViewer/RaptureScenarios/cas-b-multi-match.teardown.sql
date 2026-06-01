-- ============================================================================
-- Teardown pour scénario cas-b-multi-match
-- ============================================================================
-- Supprime le doublon créé par cas-b-multi-match.setup.sql.
-- Remplace @doublonId par l'ID retourné par le setup (PRINT 'AUDNC_ID doublon = N').
--
-- ⚠ SUPPRESSION DIRECTE sur audience — vérifier qu'on cible bien le doublon et
-- pas l'audience source 28590. Le doublon a date=2026-05-22 14:30 ET un ID > 28590.
-- ============================================================================

USE RIG_DEV;
GO

DECLARE @doublonId INT = /* REPLACE */ NULL;

IF @doublonId IS NULL
BEGIN
    PRINT '⚠ Renseigne @doublonId avec l''AUDNC_ID retourné par le setup.';
    RETURN;
END

-- Sanity check : ne pas DELETE l'audience source par erreur
IF @doublonId = 28590
BEGIN
    PRINT '🛑 Refus : @doublonId = 28590 (audience source contentieux). Vérifier l''ID.';
    RETURN;
END

-- Sanity check : vérifier que c'est bien une audience clonée à la date attendue
DECLARE @dt DATETIME, @h VARCHAR(5);
SELECT @dt = AUDNC_DATE, @h = AUDNC_HEURE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @doublonId;
IF @dt IS NULL
BEGIN
    PRINT '🛑 Audience #' + CAST(@doublonId AS VARCHAR) + ' introuvable.';
    RETURN;
END

PRINT 'Suppression audience #' + CAST(@doublonId AS VARCHAR) + ' (date=' + CONVERT(VARCHAR, @dt, 120) + ' heure=' + ISNULL(@h, 'NULL') + ')';

-- Cleanup éventuel des AUDIT_IMPORT_RAPTURE liés (en V1 il ne devrait y en avoir car
-- la création d'audience par Rapture est un autre chemin, mais sait-on jamais).
DELETE FROM AUDIT_IMPORT_RAPTURE WHERE ARIMP_ID_AUDNC = @doublonId;
PRINT 'AUDIT_IMPORT_RAPTURE : ' + CAST(@@ROWCOUNT AS VARCHAR) + ' rows cleaned';

DELETE FROM AUDIENCE_CABINET WHERE AUDNC_ID_ADNC = @doublonId;
PRINT 'AUDIENCE_CABINET    : ' + CAST(@@ROWCOUNT AS VARCHAR) + ' rows deleted';

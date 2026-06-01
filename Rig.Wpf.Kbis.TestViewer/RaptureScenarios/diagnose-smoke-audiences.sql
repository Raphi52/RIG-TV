-- ============================================================================
-- Diagnostic READ-ONLY : etat des audiences smoke vs fenetre RETAUD active
-- ============================================================================
-- A executer le matin pour voir si les scenarios "Cas A" du manifest vont
-- reellement router Cas A (audience trouvee) ou tomber en data-drift Cas C
-- (audience hors fenetre -> fallback creation).
--
-- Les scenarios manifest referencent ces AUDNC_ID :
--   28590  cas-a-contentieux-10, cas-diff-*, cas-err-* (date JSON 2026-05-22)
--   28625  cas-a-mise-en-etat, cas-a-subset, cas-diff-replacement, cas-valid-* (date JSON 2026-05-15)
--   28644  cas-a-pc-clotures (date JSON 2026-03-11)
--
-- AUTORISATION : SELECT seul sur SQL-DEV\DEV. Aucune ecriture.
-- ============================================================================

DECLARE @today DATE = CAST(GETDATE() AS DATE);

SELECT
    a.AUDNC_ID_ADNC                                   AS AudienceId,
    CONVERT(VARCHAR(10), a.AUDNC_DATE, 120)           AS DateAudience,
    a.AUDNC_HEURE                                     AS Heure,
    a.AUDNC_CHAMBRE                                   AS Chambre,
    DATEDIFF(DAY, @today, a.AUDNC_DATE)               AS JoursDepuisAujourdhui,
    (SELECT COUNT(*) FROM APPEL_AFFAIRE x
       WHERE x.APPAF_ID_ADNC = a.AUDNC_ID_ADNC)       AS NbAppelAffaire,
    CASE
        WHEN DATEDIFF(DAY, @today, a.AUDNC_DATE) BETWEEN -7 AND 14
            THEN 'DANS FENETRE probable (Cas A OK)'
        ELSE 'HORS FENETRE -> data-drift Cas C'
    END                                               AS Verdict
FROM AUDIENCE_CABINET a
WHERE a.AUDNC_ID_ADNC IN (28590, 28625, 28644)
ORDER BY a.AUDNC_ID_ADNC;

-- Bonus : liste des audiences CX/PC reellement visibles autour d'aujourd'hui
-- (sert a choisir des AUDNC_ID de remplacement si les 3 ci-dessus ont derive).
SELECT TOP 30
    a.AUDNC_ID_ADNC                          AS AudienceId,
    CONVERT(VARCHAR(10), a.AUDNC_DATE, 120)  AS DateAudience,
    a.AUDNC_HEURE                            AS Heure,
    a.AUDNC_CHAMBRE                          AS Chambre,
    (SELECT COUNT(*) FROM APPEL_AFFAIRE x
       WHERE x.APPAF_ID_ADNC = a.AUDNC_ID_ADNC) AS NbAppelAffaire
FROM AUDIENCE_CABINET a
WHERE a.AUDNC_DATE BETWEEN DATEADD(DAY, -7, @today) AND DATEADD(DAY, 14, @today)
  AND (SELECT COUNT(*) FROM APPEL_AFFAIRE x WHERE x.APPAF_ID_ADNC = a.AUDNC_ID_ADNC) > 0
ORDER BY a.AUDNC_DATE, a.AUDNC_HEURE;

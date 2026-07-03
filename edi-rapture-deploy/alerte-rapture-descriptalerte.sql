-- =============================================================================
-- Alerte dynamique « Retours Rapture a valider » — bloc Alertes de l'accueil RIG.
-- Cible : DESCRIPTION_ALERTE en base COMMON (COMMUN_RIG) = COMMON VIVANT sur SQL-PROD\PROD.
--   (COMMON est PARTAGE dev+prod ; c'est la meme table pour les 2. Le perimetre par greffe se
--    regle via la colonne DESCALRT_GREFFE, PAS via la requete-compteur.)
--
-- Droits : INSERT/UPDATE refuse au WinAuth INTRANET\raphael.vilain (SELECT only, HAS_PERMS INSERT=0).
--   A executer par un compte ayant l'ecriture (login SQL applicatif RIG / SSMS / DBA).
--
-- Effet : badge = COUNT(retours a traiter) par greffe ; clic -> PROC RAPTUVAL (onglet 3 = JUD) ; disparait a 0.
-- Le service execute "SELECT COUNT(*) AS NB " + DESCALRT_REQUETE_SQL_COMPTEUR dans la base de CHAQUE greffe
--   accepte (connexion par greffe -> pas de filtre greffe dans la requete).
-- Perimetre (DESCALRT_GREFFE) : vide/NULL = TOUS les greffes ; sinon liste separee par ';' (ex '9995;1234').
--   Split(';') confirme dans GestionnaireAlertes.ChargeAlertesEnMemoireCache.
--
-- APRES ecriture : le service Windows ServiceAlertes doit recharger son cache (redemarrage / refresh periodique).
--
-- ⚠ UNE SEULE LIGNE : index UNIQUE sur DESCALRT_CODE_ALERTE ('RAPTUVAL') -> un 2e INSERT meme code = REJETE.
--   Le passage dev->prod se fait donc par UPDATE du perimetre (DESCALRT_GREFFE), PAS par une 2e ligne.
--   Rappel : perimetre NULL = tous greffes (auto-actif au deploiement, mais logs d'erreur sur greffes
--   sans DEMAT_RAPTURE) ; '9995' = dev seul ; '9995;<prod>' = liste des greffes deployes (propre).
-- =============================================================================

USE COMMUN_RIG;
GO

-- 0) MODELE — verifier l'onglet + colonnes optionnelles sur une alerte existante.
SELECT TOP 5 DESCALRT_CODE_ALERTE, DESCALRT_NUMERO_ONGLET, DESCALRT_PROCESSUS, DESCALRT_GREFFE,
             DESCALRT_NOM, LEFT(DESCALRT_REQUETE_SQL_COMPTEUR,70) AS cpt
FROM DESCRIPTION_ALERTE WHERE DESCALRT_PROCESSUS IS NOT NULL AND DESCALRT_PROCESSUS<>''
ORDER BY DESCALRT_ID;
GO

-- =============================================================================
-- INSERT UNIQUE — scope NULL = TOUS les greffes (set-and-forget, choix retenu).
--   Marche sur dev (9995) tout de suite, ET s'active AUTO sur chaque greffe prod des que la feature
--   Rapture (DEMAT_RAPTURE + procs) y est deployee. AUCUNE action ulterieure (pas d'UPDATE au deploiement).
--   ⚠ TRADEOFF ASSUME : sur un greffe SANS DEMAT_RAPTURE, la requete-compteur erreur -> compteur 0
--     (pas de badge) + 1 erreur loggee par cycle du service. Ca disparait quand la feature y est deployee.
-- =============================================================================
INSERT INTO DESCRIPTION_ALERTE
    ( DESCALRT_NOM, DESCALRT_PROCESSUS, DESCALRT_NUMERO_ONGLET, DESCALRT_GREFFE,
      DESCALRT_REQUETE_SQL_COMPTEUR, DESCALRT_REQUETE_SQL_WHERE, DESCALRT_REQUETE_SQL,
      DESCALRT_VALEURS_DEFAUT, DESCALRT_CODE_ALERTE, DESCALRT_COMMENTAIRE )
VALUES
    ( 'Retours Rapture a valider', 'RAPTUVAL', 3, NULL,   -- NULL = TOUS les greffes (set-and-forget)
      'FROM DEMAT_RAPTURE with (nolock) WHERE RAPTU_ETAT IN (''1'',''9'')',
      NULL, NULL, NULL, 'RAPTUVAL',
      'Retours d''audience Rapture en attente de validation greffier.' );
GO

-- =============================================================================
-- FALLBACK (optionnel) — SI le bruit de logs (greffes sans DEMAT_RAPTURE) devient genant AVANT
--   le deploiement complet : restreindre temporairement le perimetre aux greffes deployes (';' = separateur) :
--     UPDATE DESCRIPTION_ALERTE SET DESCALRT_GREFFE = '9995'            -- ou '9995;1234;5678'
--       WHERE DESCALRT_CODE_ALERTE = 'RAPTUVAL';
--   ... puis remettre NULL (tous) quand la feature est partout :
--     UPDATE DESCRIPTION_ALERTE SET DESCALRT_GREFFE = NULL WHERE DESCALRT_CODE_ALERTE = 'RAPTUVAL';
--   (Une seule ligne — index UNIQUE sur DESCALRT_CODE_ALERTE : jamais de 2e ligne 'RAPTUVAL'.)
-- =============================================================================

-- Controle requete-compteur (cote base GREFFE, ex RIG_DEV) :  dev -> renvoyait 9
--   SELECT COUNT(*) AS NB FROM DEMAT_RAPTURE WHERE RAPTU_ETAT IN ('1','9');
-- Verif de la ligne :
--   SELECT * FROM DESCRIPTION_ALERTE WHERE DESCALRT_CODE_ALERTE = 'RAPTUVAL';
-- Rollback :
--   DELETE FROM DESCRIPTION_ALERTE WHERE DESCALRT_CODE_ALERTE = 'RAPTUVAL';

-- NB etats comptes : '1'=A traiter, '9'=En erreur de rapprochement. (Exclut '2'=Validee, '3'=Ecartee,
--   et '4'=EnCours transitoire. Pour refleter exactement la grille cockpit -> IN ('1','4','9').)

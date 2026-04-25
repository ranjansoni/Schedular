-- =============================================================================
-- JMScheduler — Soft-Delete ALL Duplicate Shifts (All Types, All Time)
-- Created: 2026-02-17
--
-- SCOPE:   ALL schedule types, ALL dates — only shifts with ModalId > 0
-- ACTION:  SET IsActive = 0  (soft delete — no rows are removed)
-- RULE:    Same ModalId + Client_id + datetimein + datetimeout = keep ONE (lowest Id)
-- SAFETY:
--   - Skips any shift with a timecard (Employeetimecard_id IS NOT NULL and <> 0)
--   - Skips ModalId = 0 and ModalId IS NULL (one-time shifts created manually;
--     the grouping key is not reliable without a real model reference)
--
-- EXPECTED IMPACT:
--   ~7,700 rows set to IsActive=0  (recurring-model duplicates only)
--   ModalId=0 / NULL shifts are NOT touched — they may be legitimate one-time shifts
-- =============================================================================


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 0: PRE-CHECK  (read-only — run this first to verify counts)
-- ─────────────────────────────────────────────────────────────────────────────

-- Total active duplicate excess (ModalId > 0 only):
SELECT 'Total active duplicate excess (ModalId > 0)' AS Label,
       IFNULL(SUM(cnt - 1), 0) AS Cnt
FROM (
    SELECT COUNT(*) AS cnt
    FROM   clientscheduleshift
    WHERE  IsActive = 1
      AND  ModalId > 0
    GROUP BY ModalId, Client_id, datetimein, datetimeout
    HAVING COUNT(*) > 1
) t;

-- Eligible for soft delete (no timecard, ModalId > 0):
SELECT 'Eligible for soft delete' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
WHERE  css.IsActive = 1
  AND  css.ModalId > 0
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
  AND  css.Id NOT IN (
         SELECT MinId FROM (
           SELECT MIN(Id) AS MinId
           FROM   clientscheduleshift
           WHERE  IsActive = 1
             AND  ModalId > 0
           GROUP BY ModalId, Client_id, datetimein, datetimeout
         ) keeper
       );

-- Protected by timecard (will NOT be touched):
SELECT 'Has timecard (untouched)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
WHERE  css.IsActive = 1
  AND  css.ModalId > 0
  AND  css.Employeetimecard_id IS NOT NULL
  AND  css.Employeetimecard_id <> 0
  AND  css.Id NOT IN (
         SELECT MinId FROM (
           SELECT MIN(Id) AS MinId
           FROM   clientscheduleshift
           WHERE  IsActive = 1
             AND  ModalId > 0
           GROUP BY ModalId, Client_id, datetimein, datetimeout
         ) keeper
       );

-- Skipped: ModalId = 0 or NULL (one-time / manual shifts):
SELECT 'ModalId = 0 or NULL (skipped entirely)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift
WHERE  IsActive = 1
  AND  (ModalId = 0 OR ModalId IS NULL);


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 1: BUILD KEEPER TABLE  (one ID per unique group — the original)
--         Only includes ModalId > 0 shifts.
-- ─────────────────────────────────────────────────────────────────────────────

DROP TEMPORARY TABLE IF EXISTS tmp_keeper_ids;

CREATE TEMPORARY TABLE tmp_keeper_ids (
    Id BIGINT PRIMARY KEY
) ENGINE=MEMORY;

INSERT INTO tmp_keeper_ids (Id)
SELECT MIN(Id)
FROM   clientscheduleshift
WHERE  IsActive = 1
  AND  ModalId > 0
GROUP BY ModalId, Client_id, datetimein, datetimeout;

-- Verify keeper count:
SELECT 'Keeper IDs loaded' AS Label, COUNT(*) AS Cnt FROM tmp_keeper_ids;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 2: SOFT-DELETE IN BATCHES OF 5,000
--         Repeat this UPDATE until it returns "0 rows affected".
-- ─────────────────────────────────────────────────────────────────────────────

UPDATE clientscheduleshift css
SET    css.IsActive = 0,
       css.DeactivationDate = NOW()
WHERE  css.IsActive = 1
  AND  css.ModalId > 0
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
  AND  css.Id NOT IN (SELECT Id FROM tmp_keeper_ids)
LIMIT  5000;

-- ^^^ Run the UPDATE above repeatedly until "0 rows affected".
-- Expected: ~2 batches


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 3: POST-CHECK  (verify results)
-- ─────────────────────────────────────────────────────────────────────────────

-- How many were soft-deleted today?
SELECT 'Soft-deleted today' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift
WHERE  IsActive = 0
  AND  DeactivationDate >= CURDATE();

-- Active duplicates remaining with ModalId > 0 (should only be timecard-protected):
SELECT 'Active duplicates remaining (ModalId > 0)' AS Label,
       IFNULL(SUM(cnt - 1), 0) AS Cnt
FROM (
    SELECT COUNT(*) AS cnt
    FROM   clientscheduleshift
    WHERE  IsActive = 1
      AND  ModalId > 0
    GROUP BY ModalId, Client_id, datetimein, datetimeout
    HAVING COUNT(*) > 1
) t;

-- Zero-dupes check (ModalId > 0, no timecard — should return 0):
SELECT 'Non-timecard active duplicates (SHOULD BE 0)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
WHERE  css.IsActive = 1
  AND  css.ModalId > 0
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
  AND  css.Id NOT IN (
         SELECT MinId FROM (
           SELECT MIN(Id) AS MinId
           FROM   clientscheduleshift
           WHERE  IsActive = 1
             AND  ModalId > 0
           GROUP BY ModalId, Client_id, datetimein, datetimeout
         ) keeper
       );


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 4: CLEANUP
-- ─────────────────────────────────────────────────────────────────────────────

DROP TEMPORARY TABLE IF EXISTS tmp_keeper_ids;


-- ─────────────────────────────────────────────────────────────────────────────
-- FUTURE: HARD DELETE  (run only after several days with no issues)
-- ─────────────────────────────────────────────────────────────────────────────

-- When ready to permanently remove soft-deleted rows (run in batches):
--
-- DELETE FROM clientscheduleshift
-- WHERE  IsActive = 0
--   AND  DeactivationDate IS NOT NULL
--   AND  (Employeetimecard_id IS NULL OR Employeetimecard_id = 0)
-- LIMIT 5000;
--
-- Also clean orphaned child rows:
--   DELETE sd FROM scheduleshiftscandetail sd
--     LEFT JOIN clientscheduleshift css ON css.Id = sd.ShiftId
--     WHERE css.Id IS NULL LIMIT 5000;
--
--   DELETE td FROM scheduleshiftscantaskdetail td
--     LEFT JOIN scheduleshiftscandetail sd ON sd.Id = td.ScheduleShiftScanId
--     WHERE sd.Id IS NULL LIMIT 5000;
--
--   DELETE esc FROM employeescheduleshiftclaim esc
--     LEFT JOIN clientscheduleshift css ON css.Id = esc.ClientScheduleShiftID
--     WHERE css.Id IS NULL LIMIT 5000;

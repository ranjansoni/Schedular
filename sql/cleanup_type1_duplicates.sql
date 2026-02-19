-- =============================================================================
-- JMScheduler — Soft-Delete ScheduleType=1 Duplicate Shifts
-- Created: 2026-02-17
-- 
-- SCOPE:   ScheduleType=1 (Open for All) duplicates only
-- ACTION:  SET IsActive = 0  (soft delete — no rows are removed)
-- SAFETY:  Skips any shift with a timecard (Employeetimecard_id IS NOT NULL)
-- RULE:    Same ModalId + Client_id + datetimein + datetimeout = keep the FIRST
--          (lowest Id), soft-delete the rest.
--
-- EXPECTED IMPACT: ~90,301 rows set to IsActive=0
-- 
-- RUN THIS IN BATCHES to avoid long-running transactions on production.
-- =============================================================================

-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 0: PRE-CHECK (run this first to verify counts before making changes)
-- ─────────────────────────────────────────────────────────────────────────────

-- Count of rows that WILL be soft-deleted:
SELECT 'Rows to soft-delete' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
  AND  css.IsActive = 1
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
  AND  css.Id NOT IN (
         SELECT MinId FROM (
           SELECT MIN(Id) AS MinId
           FROM   clientscheduleshift
           WHERE  CreateDate >= '2026-02-11'
           GROUP BY ModalId, Client_id, datetimein, datetimeout
         ) keeper
       );

-- Count of rows that will be KEPT (originals):
SELECT 'Rows to keep (originals)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
  AND  css.Id IN (
         SELECT MinId FROM (
           SELECT MIN(Id) AS MinId
           FROM   clientscheduleshift
           WHERE  CreateDate >= '2026-02-11'
           GROUP BY ModalId, Client_id, datetimein, datetimeout
         ) keeper
       );

-- Rows with timecards that will NOT be touched:
SELECT 'Rows with timecards (untouched)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
  AND  css.Employeetimecard_id IS NOT NULL
  AND  css.Employeetimecard_id <> 0;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 1: CREATE A TEMP TABLE OF IDs TO KEEP (one per duplicate group)
-- ─────────────────────────────────────────────────────────────────────────────

DROP TEMPORARY TABLE IF EXISTS tmp_keeper_ids;

CREATE TEMPORARY TABLE tmp_keeper_ids (
    Id BIGINT PRIMARY KEY
) ENGINE=MEMORY;

INSERT INTO tmp_keeper_ids (Id)
SELECT MIN(css.Id)
FROM   clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
GROUP BY css.ModalId, css.Client_id, css.datetimein, css.datetimeout;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 2: SOFT-DELETE IN BATCHES OF 5,000
--         Run each batch one at a time. Repeat until "0 rows affected".
-- ─────────────────────────────────────────────────────────────────────────────

-- Batch 1 (and repeat until 0 rows affected):
UPDATE clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
SET    css.IsActive = 0,
       css.DeactivationDate = NOW()
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
  AND  css.IsActive = 1
  AND  (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
  AND  css.Id NOT IN (SELECT Id FROM tmp_keeper_ids)
LIMIT  5000;

-- Keep running the UPDATE above until it returns "0 rows affected".
-- Each batch will process up to 5,000 rows.
-- Expected: ~18 batches (90,301 / 5,000 ≈ 19)


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 3: POST-CHECK (verify the results)
-- ─────────────────────────────────────────────────────────────────────────────

-- How many were soft-deleted?
SELECT 'Soft-deleted (IsActive=0)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
  AND  css.IsActive = 0
  AND  css.DeactivationDate >= CURDATE();

-- How many remain active?
SELECT 'Still active (IsActive=1)' AS Label, COUNT(*) AS Cnt
FROM   clientscheduleshift css
JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
WHERE  css.CreateDate >= '2026-02-11'
  AND  csm.ScheduleType = 1
  AND  css.IsActive = 1;

-- Verify no active duplicates remain (should return 0):
SELECT 'Remaining active duplicates (should be 0)' AS Label, COUNT(*) AS Cnt
FROM (
    SELECT 1
    FROM   clientscheduleshift css
    JOIN   clientschedulemodel csm ON csm.Id = css.ModalId
    WHERE  css.CreateDate >= '2026-02-11'
      AND  csm.ScheduleType = 1
      AND  css.IsActive = 1
    GROUP BY css.ModalId, css.Client_id, css.datetimein, css.datetimeout
    HAVING COUNT(*) > 1
) t;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 4: CLEANUP TEMP TABLE
-- ─────────────────────────────────────────────────────────────────────────────

DROP TEMPORARY TABLE IF EXISTS tmp_keeper_ids;


-- ─────────────────────────────────────────────────────────────────────────────
-- FUTURE: HARD DELETE (run this only after a few days with no issues reported)
-- ─────────────────────────────────────────────────────────────────────────────

-- When you're ready to permanently remove the soft-deleted rows:
--
-- DELETE css FROM clientscheduleshift css
-- JOIN clientschedulemodel csm ON csm.Id = css.ModalId
-- WHERE css.CreateDate >= '2026-02-11'
--   AND csm.ScheduleType = 1
--   AND css.IsActive = 0
--   AND css.DeactivationDate IS NOT NULL
--   AND (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
-- LIMIT 5000;
--
-- Also clean up orphaned child rows at that time:
--   - scheduleshiftscandetail WHERE ShiftId IN (deleted ids)
--   - scheduleshiftscantaskdetail WHERE ScheduleShiftScanId IN (deleted scan ids)
--   - employeescheduleshiftclaim WHERE ClientScheduleShiftID IN (deleted ids)

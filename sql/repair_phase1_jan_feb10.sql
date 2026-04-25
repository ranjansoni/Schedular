-- =============================================================================
-- REPAIR PHASE 1: Restore timecard data for Jan 1 - Feb 10, 2026
-- =============================================================================
-- SOURCE OF TRUTH: Old database snapshot (e.g. Feb 11 snapshot)
-- TARGET:          Latest database (Feb 17 / production)
--
-- Strategy:
--   1. Extract shifts with timecard data from OLD DB into a staging table
--   2. Export that table (mysqldump)
--   3. Import into NEW DB
--   4. Match old → new shifts by composite key (employeeid, Client_id,
--      datetimein, datetimeout) and copy the actual punch values
-- =============================================================================


-- ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓
-- PART A: RUN ON OLD (SOURCE OF TRUTH) DATABASE
-- ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓

-- STEP A1: Preview what we're extracting
SELECT 'Old DB: Shifts with TC data (Jan 1 - Feb 10)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND Employeetimecard_id IS NOT NULL
  AND Employeetimecard_id > 0;

SELECT 'Old DB: Shifts WITHOUT TC data (Jan 1 - Feb 10)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);


-- STEP A2: Create the extraction table
DROP TABLE IF EXISTS repair_source_jan_feb10;

CREATE TABLE repair_source_jan_feb10 AS
SELECT
    employeeid,
    Client_id,
    CompanyID,
    datetimein,
    datetimeout,
    fromdate,
    Employeetimecard_id,
    actualtimein,
    actualtimeout,
    actualduration,
    IsLateIn,
    IsEarlyOut,
    IsInMissing,
    IsOutMissing
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND Employeetimecard_id IS NOT NULL
  AND Employeetimecard_id > 0;

SELECT 'Rows extracted into repair_source_jan_feb10' AS Label,
       COUNT(*) AS Cnt
FROM repair_source_jan_feb10;

-- STEP A3: Add index for faster lookups after import
ALTER TABLE repair_source_jan_feb10
  ADD INDEX idx_composite (employeeid, Client_id, datetimein, datetimeout);


-- =============================================================================
-- NOW EXPORT THIS TABLE:
--
--   mysqldump --single-transaction -h <OLD_HOST> -u jmadmin -p \
--       janitorialmgr repair_source_jan_feb10 > repair_source_jan_feb10.sql
--
-- THEN IMPORT INTO THE NEW (LATEST) DATABASE:
--
--   mysql -h <NEW_HOST> -u jmadmin -p janitorialmgr < repair_source_jan_feb10.sql
--
-- =============================================================================


-- ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓
-- PART B: RUN ON NEW (LATEST) DATABASE — after importing the table above
-- ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP B0: VERIFY IMPORT
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Imported rows in repair_source_jan_feb10' AS Label,
       COUNT(*) AS Cnt
FROM repair_source_jan_feb10;

SELECT 'New DB: Unlinked active shifts (Jan 1 - Feb 10)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);

SELECT 'New DB: Already linked shifts (Jan 1 - Feb 10)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND Employeetimecard_id > 0;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP B1: BUILD MAPPING — match old shifts to new shifts by composite key
-- ─────────────────────────────────────────────────────────────────────────────

DROP TABLE IF EXISTS tmp_phase1_mapping;

CREATE TABLE tmp_phase1_mapping AS
SELECT
    css.Id          AS new_shift_id,
    src.Employeetimecard_id,
    src.actualtimein,
    src.actualtimeout,
    src.actualduration,
    src.IsLateIn,
    src.IsEarlyOut,
    src.IsInMissing,
    src.IsOutMissing,
    ROW_NUMBER() OVER (
        PARTITION BY css.Id
        ORDER BY ABS(TIMESTAMPDIFF(SECOND, css.datetimein, src.datetimein)) ASC
    ) AS rn
FROM clientscheduleshift css
INNER JOIN repair_source_jan_feb10 src
    ON  src.employeeid  = css.employeeid
    AND src.Client_id   = css.Client_id
    AND src.datetimein   = css.datetimein
    AND src.datetimeout  = css.datetimeout
WHERE css.IsActive = 1
  AND DATE(css.datetimein) >= '2026-01-01'
  AND DATE(css.datetimein) <= '2026-02-10'
  AND (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0);

ALTER TABLE tmp_phase1_mapping ADD INDEX idx_newshift (new_shift_id);

-- Keep only the best match per new shift (rn=1)
DELETE FROM tmp_phase1_mapping WHERE rn > 1;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP B2: DRY RUN — review before applying
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'New shifts matched to old source' AS Label, COUNT(*) AS Cnt
FROM tmp_phase1_mapping;

SELECT 'Unlinked shifts that could NOT be matched' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift css
LEFT JOIN tmp_phase1_mapping m ON m.new_shift_id = css.Id
WHERE css.IsActive = 1
  AND DATE(css.datetimein) >= '2026-01-01'
  AND DATE(css.datetimein) <= '2026-02-10'
  AND (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0)
  AND m.new_shift_id IS NULL;

-- Verify Employeetimecard_id still exists in employeetimecarddetail
SELECT 'Mapped TCs that still exist in employeetimecarddetail' AS Label,
       COUNT(*) AS Cnt
FROM tmp_phase1_mapping m
INNER JOIN employeetimecarddetail etcd ON etcd.Id = m.Employeetimecard_id;

SELECT 'Mapped TCs that are MISSING from employeetimecarddetail (orphaned)' AS Label,
       COUNT(*) AS Cnt
FROM tmp_phase1_mapping m
LEFT JOIN employeetimecarddetail etcd ON etcd.Id = m.Employeetimecard_id
WHERE etcd.Id IS NULL;

-- Check for duplicate TC assignments (one TC mapped to multiple new shifts)
SELECT 'TCs mapped to multiple new shifts (review if > 0)' AS Label,
       COUNT(*) AS Cnt
FROM (
    SELECT Employeetimecard_id, COUNT(*) AS ShiftCnt
    FROM tmp_phase1_mapping
    GROUP BY Employeetimecard_id
    HAVING ShiftCnt > 1
) dupes;

-- Sample preview of what will be updated
SELECT
    m.new_shift_id,
    css.employeeid,
    css.Client_id,
    css.CompanyID,
    css.datetimein       AS scheduled_in,
    css.datetimeout      AS scheduled_out,
    m.Employeetimecard_id,
    m.actualtimein,
    m.actualtimeout,
    m.actualduration,
    m.IsLateIn,
    m.IsEarlyOut,
    m.IsInMissing,
    m.IsOutMissing
FROM tmp_phase1_mapping m
INNER JOIN clientscheduleshift css ON css.Id = m.new_shift_id
LIMIT 20;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP B3: APPLY THE UPDATE
--          *** UNCOMMENT AFTER REVIEWING DRY RUN ***
-- ─────────────────────────────────────────────────────────────────────────────

/*
UPDATE clientscheduleshift css
INNER JOIN tmp_phase1_mapping m ON m.new_shift_id = css.Id
SET
    css.Employeetimecard_id = m.Employeetimecard_id,
    css.actualtimein        = m.actualtimein,
    css.actualtimeout       = m.actualtimeout,
    css.actualduration      = m.actualduration,
    css.IsLateIn            = m.IsLateIn,
    css.IsEarlyOut          = m.IsEarlyOut,
    css.IsInMissing         = m.IsInMissing,
    css.IsOutMissing        = m.IsOutMissing;
*/


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP B4: POST-CHECK
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Still unlinked after repair (Jan 1 - Feb 10)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);

SELECT 'Now linked (Jan 1 - Feb 10)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-01-01'
  AND DATE(datetimein) <= '2026-02-10'
  AND Employeetimecard_id > 0;

-- Spot check: compare a sample of updated shifts
SELECT css.Id, css.employeeid, css.Client_id,
       css.datetimein, css.datetimeout,
       css.actualtimein, css.actualtimeout, css.actualduration,
       css.Employeetimecard_id, css.IsLateIn, css.IsEarlyOut,
       css.IsInMissing, css.IsOutMissing
FROM clientscheduleshift css
WHERE css.IsActive = 1
  AND DATE(css.datetimein) >= '2026-01-01'
  AND DATE(css.datetimein) <= '2026-02-10'
  AND css.Employeetimecard_id > 0
  AND css.actualtimein IS NOT NULL
ORDER BY css.Id DESC
LIMIT 10;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP B5: CLEANUP
-- ─────────────────────────────────────────────────────────────────────────────
DROP TABLE IF EXISTS tmp_phase1_mapping;
-- Keep repair_source_jan_feb10 for audit; drop when no longer needed:
-- DROP TABLE IF EXISTS repair_source_jan_feb10;

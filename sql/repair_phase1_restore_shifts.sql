-- =============================================================================
-- REPAIR PHASE 1: Restore missing shifts from old snapshot (Jan 1 - Feb 10)
-- =============================================================================
--
-- PREREQUISITES:
--   This script runs on the Feb 17 snapshot (or production).
--   You need BOTH databases accessible from the same MySQL session,
--   OR you export the missing shifts from the old DB and import them.
--
-- APPROACH:
--   Option A: If both DBs are on the same Aurora cluster (linked via DB name)
--   Option B: Export from old → Import to new (mysqldump / CSV)
--
-- Below we provide BOTH approaches.
-- =============================================================================


-- =============================================================================
-- OPTION A: Both databases accessible from same session
--           (e.g., old DB is 'janitorialmgr_old', new is 'janitorialmgr')
-- =============================================================================

-- STEP 1: Find shift IDs that exist in old but not in new (Jan 1 - Feb 10)
--          Run this to get the count first:

/*
-- Adjust database names as needed
SELECT COUNT(*) AS MissingShifts
FROM janitorialmgr_old.clientscheduleshift old_css
LEFT JOIN janitorialmgr.clientscheduleshift new_css ON new_css.Id = old_css.Id
WHERE new_css.Id IS NULL
  AND DATE(old_css.datetimein) >= '2026-01-01'
  AND DATE(old_css.datetimein) <= '2026-02-10';
*/


-- =============================================================================
-- OPTION B: Export → Import approach (RECOMMENDED)
-- =============================================================================
-- Run this on the OLD database to export missing shifts:
-- Then import into the new database.

-- STEP 1: On the OLD database, run this query to identify missing shifts.
--          Replace {NEW_DB_HOST} with the Feb 17 snapshot endpoint.
--
-- Since you can't cross-query Aurora clusters directly,
-- use this approach:
--
--   1. Export ALL shift IDs from the Feb 17 DB into a file
--   2. Load them into a temp table on the old DB
--   3. Find the diff
--   4. Export the missing shifts
--   5. Import into the Feb 17 DB


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 1: Run on FEB 17 SNAPSHOT — Export current shift IDs for Jan-Feb 10
-- ─────────────────────────────────────────────────────────────────────────────
-- Save this output to a file:

-- SELECT Id FROM clientscheduleshift
-- WHERE DATE(datetimein) >= '2026-01-01' AND DATE(datetimein) <= '2026-02-10'
-- INTO OUTFILE '/tmp/feb17_shift_ids.csv';

-- Or use: mysql -h feb-17-cluster... -e "SELECT Id FROM ..." > feb17_ids.txt


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 2: Run on OLD SNAPSHOT — Find and export missing shifts
-- ─────────────────────────────────────────────────────────────────────────────

-- Load Feb 17 IDs into temp table:
-- CREATE TEMPORARY TABLE tmp_existing_ids (Id BIGINT PRIMARY KEY);
-- LOAD DATA LOCAL INFILE '/tmp/feb17_shift_ids.csv' INTO TABLE tmp_existing_ids;

-- Export missing shifts (all 71 columns):
-- SELECT old.* FROM clientscheduleshift old
-- LEFT JOIN tmp_existing_ids t ON t.Id = old.Id
-- WHERE t.Id IS NULL
--   AND DATE(old.datetimein) >= '2026-01-01'
--   AND DATE(old.datetimein) <= '2026-02-10'
-- INTO OUTFILE '/tmp/missing_shifts.csv'
-- FIELDS TERMINATED BY ',' ENCLOSED BY '"' LINES TERMINATED BY '\n';


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 3: Run on FEB 17 SNAPSHOT — Import missing shifts
-- ─────────────────────────────────────────────────────────────────────────────

-- LOAD DATA LOCAL INFILE '/tmp/missing_shifts.csv'
-- INTO TABLE clientscheduleshift
-- FIELDS TERMINATED BY ',' ENCLOSED BY '"' LINES TERMINATED BY '\n';


-- =============================================================================
-- ALTERNATIVE: Use mysqldump with WHERE clause (simplest approach)
-- =============================================================================
--
-- 1. Get list of IDs that need restoring:
--    Run the C# diag script or SQL to compute the diff, save IDs to a file
--
-- 2. On OLD DB, dump those specific rows:
--    mysqldump -h db-only-for-investigation-cluster... \
--      --no-create-info --complete-insert --single-transaction \
--      janitorialmgr clientscheduleshift \
--      --where="Id IN (11930629,11936270,...)" > missing_shifts.sql
--
-- 3. Import into Feb 17:
--    mysql -h feb-17-cluster... janitorialmgr < missing_shifts.sql
--
-- For large numbers of IDs, use a temp table approach.


-- =============================================================================
-- AFTER IMPORT: Run Phase 2 script to re-link timecards
--               Then verify with the checks below
-- =============================================================================

-- POST-IMPORT VERIFICATION:

-- Check restored shift count
-- SELECT 'Restored shifts (Jan 1 - Feb 10)' AS Label,
--        COUNT(*) AS Cnt
-- FROM clientscheduleshift
-- WHERE IsActive = 1
--   AND DATE(datetimein) >= '2026-01-01'
--   AND DATE(datetimein) <= '2026-02-10';

-- Check for any duplicate shifts after import
-- (same ModalId + Client_id + datetimein + datetimeout, more than 1 active)
-- SELECT 'Duplicate check after import' AS Label,
--        COUNT(*) AS DuplicateGroups
-- FROM (
--     SELECT ModalId, Client_id, datetimein, datetimeout, COUNT(*) AS cnt
--     FROM clientscheduleshift
--     WHERE IsActive = 1
--       AND ModalId > 0
--       AND DATE(datetimein) >= '2026-01-01'
--       AND DATE(datetimein) <= '2026-02-10'
--     GROUP BY ModalId, Client_id, datetimein, datetimeout
--     HAVING cnt > 1
-- ) dupes;

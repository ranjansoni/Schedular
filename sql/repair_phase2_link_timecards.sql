-- =============================================================================
-- REPAIR PHASE 2: Reconstruct timecard links for Feb 11 - Feb 16, 2026
-- =============================================================================
-- 
-- WHAT THIS DOES:
--   For shifts where the C# job created a NEW shift (replacing the old one),
--   the employeetimecarddetail record still exists with the clock-in/out data.
--   This script re-links those timecards to the new shift IDs.
--
-- FIELDS UPDATED ON clientscheduleshift:
--   Employeetimecard_id  = employeetimecarddetail.Id
--   actualtimein         = employeetimecarddetail.StartTime
--   actualtimeout        = employeetimecarddetail.EndTime
--   actualduration       = employeetimecarddetail.HoursWorked
--   IsInMissing          = 0  (employee did clock in)
--   IsOutMissing         = 0  (if they clocked out)
--   IsLateIn             = calculated (StartTime > datetimein + LateInDuration)
--   IsEarlyOut           = calculated (EndTime < datetimeout - LateOutDuration)
--
-- SAFETY:
--   - Only touches shifts with Employeetimecard_id IS NULL or 0
--   - Only touches active shifts and active timecards
--   - Handles 1:many by picking the timecard closest in time to the shift
--   - DRY RUN first, then UPDATE in batches
--
-- RUN ON: Production (or Feb 17 snapshot for testing)
-- =============================================================================


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 0: PRE-CHECK — Understand scope before changing anything
-- ─────────────────────────────────────────────────────────────────────────────

-- How many shifts need linking?
SELECT 'Shifts needing TC link (Feb 11-16)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-02-11'
  AND DATE(datetimein) <= '2026-02-16'
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);

-- How many already have a link?
SELECT 'Shifts already linked (Feb 11-16)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-02-11'
  AND DATE(datetimein) <= '2026-02-16'
  AND Employeetimecard_id > 0;

-- How many timecards exist for this period?
SELECT 'Timecards available (Feb 11-16)' AS Label,
       COUNT(*) AS Cnt
FROM employeetimecarddetail
WHERE IsActive = 1
  AND DATE(Date) >= '2026-02-11'
  AND DATE(Date) <= '2026-02-16';


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 1: BUILD MAPPING TABLE
--         Match each unlinked shift to its best timecard
--         using Employee_id + Client_id + Date, picking closest by time
-- ─────────────────────────────────────────────────────────────────────────────

DROP TEMPORARY TABLE IF EXISTS tmp_tc_mapping;

CREATE TEMPORARY TABLE tmp_tc_mapping (
    ShiftId       INT NOT NULL PRIMARY KEY,
    TimecardId    INT NOT NULL,
    StartTime     DATETIME,
    EndTime       DATETIME,
    HoursWorked   DOUBLE,
    TimeDiff      INT
) ENGINE=MEMORY;

-- For each unlinked shift, find the CLOSEST timecard by time
-- (handles case where employee has multiple shifts/TCs on same day at same client)
INSERT INTO tmp_tc_mapping (ShiftId, TimecardId, StartTime, EndTime, HoursWorked, TimeDiff)
SELECT
    css.Id AS ShiftId,
    best_tc.TcId AS TimecardId,
    best_tc.StartTime,
    best_tc.EndTime,
    best_tc.HoursWorked,
    best_tc.TimeDiff
FROM clientscheduleshift css
INNER JOIN (
    -- Subquery: for each shift, rank timecards by time proximity
    SELECT 
        css2.Id AS ShiftId,
        etcd.Id AS TcId,
        etcd.StartTime,
        etcd.EndTime,
        etcd.HoursWorked,
        ABS(TIMESTAMPDIFF(MINUTE, css2.datetimein, etcd.StartTime)) AS TimeDiff,
        ROW_NUMBER() OVER (
            PARTITION BY css2.Id 
            ORDER BY ABS(TIMESTAMPDIFF(MINUTE, css2.datetimein, etcd.StartTime)) ASC
        ) AS rn
    FROM clientscheduleshift css2
    INNER JOIN employeetimecarddetail etcd
        ON etcd.Employee_id = css2.employeeid
        AND etcd.Client_id  = css2.Client_id
        AND DATE(etcd.Date)  = DATE(css2.datetimein)
        AND etcd.IsActive    = 1
    WHERE css2.IsActive = 1
      AND DATE(css2.datetimein) >= '2026-02-11'
      AND DATE(css2.datetimein) <= '2026-02-16'
      AND (css2.Employeetimecard_id IS NULL OR css2.Employeetimecard_id = 0)
) best_tc ON best_tc.ShiftId = css.Id AND best_tc.rn = 1;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 2: DRY RUN — Review what will be updated
-- ─────────────────────────────────────────────────────────────────────────────

-- Total matches found
SELECT 'Shifts to be updated' AS Label, COUNT(*) AS Cnt FROM tmp_tc_mapping;

-- Breakdown by time difference (how close is the TC to the shift?)
SELECT 'Time diff distribution' AS Label,
       CASE 
           WHEN TimeDiff <= 5   THEN '0-5 min (excellent)'
           WHEN TimeDiff <= 15  THEN '6-15 min (good)'
           WHEN TimeDiff <= 30  THEN '16-30 min (ok)'
           WHEN TimeDiff <= 60  THEN '31-60 min (check)'
           ELSE '60+ min (review!)'
       END AS MatchQuality,
       COUNT(*) AS Cnt
FROM tmp_tc_mapping
GROUP BY MatchQuality
ORDER BY MIN(TimeDiff);

-- Sample of what will be updated (verify these look correct)
SELECT 
    m.ShiftId, m.TimecardId, m.TimeDiff AS MinDiff,
    css.employeeid, css.Client_id, css.CompanyID,
    css.datetimein AS ScheduledIn,
    css.datetimeout AS ScheduledOut,
    m.StartTime AS ActualClockIn,
    m.EndTime AS ActualClockOut,
    m.HoursWorked
FROM tmp_tc_mapping m
INNER JOIN clientscheduleshift css ON css.Id = m.ShiftId
ORDER BY m.TimeDiff DESC
LIMIT 20;

-- Check: any timecard mapped to more than one shift? (should not happen)
SELECT 'TCs mapped to multiple shifts (should be 0)' AS Label,
       COUNT(*) AS Cnt
FROM (
    SELECT TimecardId, COUNT(*) AS ShiftCnt
    FROM tmp_tc_mapping
    GROUP BY TimecardId
    HAVING ShiftCnt > 1
) dupes;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 3: APPLY THE UPDATE
--         Run this ONLY after reviewing Step 2 output
-- ─────────────────────────────────────────────────────────────────────────────

-- *** UNCOMMENT THE UPDATE BELOW AFTER REVIEWING DRY RUN ***

/*
UPDATE clientscheduleshift css
INNER JOIN tmp_tc_mapping m ON m.ShiftId = css.Id
SET 
    css.Employeetimecard_id = m.TimecardId,
    css.actualtimein        = m.StartTime,
    css.actualtimeout       = m.EndTime,
    -- actualduration = hours between actual in and actual out (matches API logic)
    css.actualduration      = CASE 
        WHEN m.StartTime IS NOT NULL AND m.EndTime IS NOT NULL
        THEN ROUND(TIMESTAMPDIFF(SECOND, m.StartTime, m.EndTime) / 3600.0, 4)
        ELSE 0 
    END,
    css.IsInMissing = 0,
    css.IsOutMissing = CASE WHEN m.EndTime IS NOT NULL THEN 0 ELSE css.IsOutMissing END,

    -- IsLateIn: (ActualClockIn - ScheduledIn) in minutes >= LateInDuration
    -- API: difference = actualIn - scheduledIn; if (totalMinutes >= LateInDuration) IsLateIn = true
    css.IsLateIn = CASE 
        WHEN TIMESTAMPDIFF(MINUTE, css.datetimein, m.StartTime) >= COALESCE(css.LateInDuration, 0)
        THEN 1 ELSE 0 
    END,

    -- IsEarlyOut: (ScheduledOut - ActualClockOut) in minutes >= LateOutDuration
    -- API: difference = scheduledOut - actualOut; if (totalMinutes >= LateOutDuration) IsEarlyOut = true
    css.IsEarlyOut = CASE 
        WHEN m.EndTime IS NOT NULL 
         AND TIMESTAMPDIFF(MINUTE, m.EndTime, css.datetimeout) >= COALESCE(css.LateOutDuration, 0)
        THEN 1 ELSE 0 
    END
WHERE css.IsActive = 1
  AND (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0);
*/


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 4: POST-CHECK — Verify the results
-- ─────────────────────────────────────────────────────────────────────────────

-- How many still unlinked? (should be only shifts with no clock-in)
SELECT 'Still unlinked after repair (Feb 11-16)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-02-11'
  AND DATE(datetimein) <= '2026-02-16'
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);

-- Verify linked count increased
SELECT 'Now linked (Feb 11-16)' AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= '2026-02-11'
  AND DATE(datetimein) <= '2026-02-16'
  AND Employeetimecard_id > 0;

-- Spot check: a few updated records
SELECT css.Id, css.employeeid, css.Client_id,
       css.datetimein, css.datetimeout,
       css.actualtimein, css.actualtimeout, css.actualduration,
       css.Employeetimecard_id, css.IsLateIn, css.IsEarlyOut
FROM clientscheduleshift css
WHERE css.IsActive = 1
  AND DATE(css.datetimein) >= '2026-02-11'
  AND DATE(css.datetimein) <= '2026-02-16'
  AND css.Employeetimecard_id > 0
  AND css.actualtimein IS NOT NULL
ORDER BY css.Id DESC
LIMIT 10;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 5: CLEANUP
-- ─────────────────────────────────────────────────────────────────────────────
DROP TEMPORARY TABLE IF EXISTS tmp_tc_mapping;

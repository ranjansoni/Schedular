-- =============================================================================
-- REPAIR PHASE 5: Reconstruct timecard links for 2023 and earlier
-- =============================================================================
-- Same logic as Phase 3/4 but for 2023 and anything older.
-- The damage assessment showed deleted shifts dating back to 2021-07-21.
-- Split into half-years for 2023, and one batch for pre-2023.
--
-- RUN ON: Production (or snapshot for testing)
-- =============================================================================


-- =============================================
-- Set the period you are running (change these two dates per run):
--   2023 H1: '2023-01-01' to '2023-06-30'
--   2023 H2: '2023-07-01' to '2023-12-31'
--   Pre-2023: '2021-01-01' to '2022-12-31'
-- =============================================

SET @start_date = '2023-01-01';
SET @end_date   = '2023-06-30';


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 0: PRE-CHECK
-- ─────────────────────────────────────────────────────────────────────────────

SELECT CONCAT('Shifts needing TC link (', @start_date, ' to ', @end_date, ')') AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= @start_date
  AND DATE(datetimein) <= @end_date
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);

SELECT CONCAT('Shifts already linked (', @start_date, ' to ', @end_date, ')') AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= @start_date
  AND DATE(datetimein) <= @end_date
  AND Employeetimecard_id > 0;

SELECT CONCAT('Timecards available (', @start_date, ' to ', @end_date, ')') AS Label,
       COUNT(*) AS Cnt
FROM employeetimecarddetail
WHERE IsActive = 1
  AND DATE(Date) >= @start_date
  AND DATE(Date) <= @end_date;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 1: BUILD MAPPING TABLE
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
      AND DATE(css2.datetimein) >= @start_date
      AND DATE(css2.datetimein) <= @end_date
      AND (css2.Employeetimecard_id IS NULL OR css2.Employeetimecard_id = 0)
) best_tc ON best_tc.ShiftId = css.Id AND best_tc.rn = 1;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 2: DRY RUN
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Shifts to be updated' AS Label, COUNT(*) AS Cnt FROM tmp_tc_mapping;

SELECT
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
--         *** UNCOMMENT AFTER REVIEWING DRY RUN ***
-- ─────────────────────────────────────────────────────────────────────────────

/*
UPDATE clientscheduleshift css
INNER JOIN tmp_tc_mapping m ON m.ShiftId = css.Id
SET 
    css.Employeetimecard_id = m.TimecardId,
    css.actualtimein        = m.StartTime,
    css.actualtimeout       = m.EndTime,
    css.actualduration      = CASE 
        WHEN m.StartTime IS NOT NULL AND m.EndTime IS NOT NULL
        THEN ROUND(TIMESTAMPDIFF(SECOND, m.StartTime, m.EndTime) / 3600.0, 4)
        ELSE 0 
    END,
    css.IsInMissing  = 0,
    css.IsOutMissing = CASE WHEN m.EndTime IS NOT NULL THEN 0 ELSE css.IsOutMissing END,
    css.IsLateIn = CASE 
        WHEN TIMESTAMPDIFF(MINUTE, css.datetimein, m.StartTime) >= COALESCE(css.LateInDuration, 0)
        THEN 1 ELSE 0 
    END,
    css.IsEarlyOut = CASE 
        WHEN m.EndTime IS NOT NULL 
         AND TIMESTAMPDIFF(MINUTE, m.EndTime, css.datetimeout) >= COALESCE(css.LateOutDuration, 0)
        THEN 1 ELSE 0 
    END
WHERE css.IsActive = 1
  AND (css.Employeetimecard_id IS NULL OR css.Employeetimecard_id = 0);
*/


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 4: POST-CHECK
-- ─────────────────────────────────────────────────────────────────────────────

SELECT CONCAT('Still unlinked (', @start_date, ' to ', @end_date, ')') AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= @start_date
  AND DATE(datetimein) <= @end_date
  AND (Employeetimecard_id IS NULL OR Employeetimecard_id = 0);

SELECT CONCAT('Now linked (', @start_date, ' to ', @end_date, ')') AS Label,
       COUNT(*) AS Cnt
FROM clientscheduleshift
WHERE IsActive = 1
  AND DATE(datetimein) >= @start_date
  AND DATE(datetimein) <= @end_date
  AND Employeetimecard_id > 0;


-- ─────────────────────────────────────────────────────────────────────────────
-- STEP 5: CLEANUP — then change @start_date / @end_date for next period
-- ─────────────────────────────────────────────────────────────────────────────
DROP TEMPORARY TABLE IF EXISTS tmp_tc_mapping;


-- =============================================================================
-- FINAL SUMMARY: Run after all phases are complete
-- =============================================================================
-- Uncomment and run this after finishing all phases (2023 H1, H2, Pre-2023):
/*
SELECT 
    YEAR(datetimein) AS ShiftYear,
    COUNT(*) AS TotalActive,
    SUM(CASE WHEN Employeetimecard_id > 0 THEN 1 ELSE 0 END) AS WithTC,
    SUM(CASE WHEN Employeetimecard_id IS NULL OR Employeetimecard_id = 0 THEN 1 ELSE 0 END) AS NoTC,
    ROUND(SUM(CASE WHEN Employeetimecard_id > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*), 1) AS LinkedPct
FROM clientscheduleshift
WHERE IsActive = 1
  AND datetimein < NOW()
GROUP BY ShiftYear
ORDER BY ShiftYear DESC;
*/

-- =============================================================================
-- JMScheduler — Daily Duplicate Check Query
-- Run this daily against sandbox to verify zero new duplicates before production.
-- =============================================================================

-- ─────────────────────────────────────────────────────────────────────────────
-- QUERY 1: QUICK HEALTH CHECK
-- Shows duplicates created in the last 24 hours. Should return 0 rows.
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'LAST 24H — New duplicates' AS CheckName,
       IFNULL(SUM(cnt - 1), 0) AS ExcessRows,
       CASE WHEN IFNULL(SUM(cnt - 1), 0) = 0 THEN 'PASS' ELSE 'FAIL' END AS Status
FROM (
    SELECT COUNT(*) AS cnt
    FROM   clientscheduleshift
    WHERE  CreateDate >= DATE_SUB(NOW(), INTERVAL 1 DAY)
    GROUP BY ModalId, Client_id, datetimein, datetimeout
    HAVING COUNT(*) > 1
) t;


-- ─────────────────────────────────────────────────────────────────────────────
-- QUERY 2: CROSS-RUN DUPLICATES
-- Checks if today's run created shifts that already existed from prior runs.
-- Should return 0.
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'CROSS-RUN — Today duplicating older' AS CheckName,
       COUNT(*) AS ExcessRows,
       CASE WHEN COUNT(*) = 0 THEN 'PASS' ELSE 'FAIL' END AS Status
FROM   clientscheduleshift recent
WHERE  recent.CreateDate >= CURDATE()
  AND  EXISTS (
         SELECT 1 FROM clientscheduleshift older
         WHERE  older.ModalId = recent.ModalId
           AND  older.Client_id = recent.Client_id
           AND  older.datetimein = recent.datetimein
           AND  older.datetimeout = recent.datetimeout
           AND  older.Id < recent.Id
           AND  older.CreateDate < CURDATE()
       );


-- ─────────────────────────────────────────────────────────────────────────────
-- QUERY 3: FULL DUPLICATE SUMMARY (all time, post-Feb-11)
-- Compare this to baseline after each cleanup. Should trend down to 0.
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'ALL TIME — Total duplicate excess (post Feb 11)' AS CheckName,
       IFNULL(SUM(cnt - 1), 0) AS ExcessRows,
       COUNT(*) AS DupeGroups
FROM (
    SELECT COUNT(*) AS cnt
    FROM   clientscheduleshift
    WHERE  CreateDate >= '2026-02-11'
    GROUP BY ModalId, Client_id, datetimein, datetimeout
    HAVING COUNT(*) > 1
) t;


-- ─────────────────────────────────────────────────────────────────────────────
-- QUERY 4: DUPLICATES BY SCHEDULE TYPE
-- Breakdown to quickly spot which type is affected.
-- ─────────────────────────────────────────────────────────────────────────────

SELECT csm.ScheduleType,
       CASE csm.ScheduleType
           WHEN 0 THEN 'Individual'
           WHEN 1 THEN 'OpenWithAllClaim'
           WHEN 2 THEN 'OpenWithSelectedClaim'
           WHEN 3 THEN 'TeamSchedule'
           ELSE CONCAT('Unknown(', csm.ScheduleType, ')')
       END AS TypeName,
       COUNT(*) AS DupeGroups,
       SUM(d.Copies - 1) AS ExcessRows
FROM (
    SELECT ModalId, Client_id, datetimein, datetimeout,
           COUNT(*) AS Copies
    FROM   clientscheduleshift
    WHERE  CreateDate >= '2026-02-11'
    GROUP BY ModalId, Client_id, datetimein, datetimeout
    HAVING COUNT(*) > 1
) d
JOIN clientschedulemodel csm ON csm.Id = d.ModalId
GROUP BY csm.ScheduleType
ORDER BY ExcessRows DESC;


-- ─────────────────────────────────────────────────────────────────────────────
-- QUERY 5: LATEST JOB RUN SUMMARY
-- Quick view of the most recent run's metrics.
-- ─────────────────────────────────────────────────────────────────────────────

SELECT RunId, StartedAt, CompletedAt, DurationSeconds, Status,
       WeeklyModelsLoaded, RecordsConsidered,
       ShiftsCreated, ShiftsSkipped,
       AuditEntriesCount, ConflictsCount, ErrorMessage
FROM   job_scheduler_run
ORDER BY StartedAt DESC
LIMIT 5;


-- ─────────────────────────────────────────────────────────────────────────────
-- QUERY 6: TOP 10 MODELS WITH MOST DUPLICATES (if any)
-- Helps pinpoint which models are problematic.
-- Only shows results if duplicates exist.
-- ─────────────────────────────────────────────────────────────────────────────

SELECT d.ModalId, csm.ScheduleType,
       CASE csm.ScheduleType
           WHEN 0 THEN 'Indiv' WHEN 1 THEN 'Open'
           WHEN 2 THEN 'OpenSel' WHEN 3 THEN 'Team'
           ELSE '?' END AS T,
       csm.Client_id, csm.employeeid,
       d.TotalShifts, d.UniqueSlots,
       d.TotalShifts - d.UniqueSlots AS ExcessShifts
FROM (
    SELECT ModalId,
           COUNT(*) AS TotalShifts,
           COUNT(DISTINCT CONCAT(DATE_FORMAT(datetimein,'%Y-%m-%d %H:%i'),'|',
                                  DATE_FORMAT(datetimeout,'%Y-%m-%d %H:%i'))) AS UniqueSlots
    FROM   clientscheduleshift
    WHERE  CreateDate >= '2026-02-11'
    GROUP BY ModalId
    HAVING TotalShifts > UniqueSlots
) d
JOIN clientschedulemodel csm ON csm.Id = d.ModalId
ORDER BY ExcessShifts DESC
LIMIT 10;

-- =============================================================================
-- REPAIR VERIFICATION: Run after Phase 1 and Phase 2 to confirm success
-- =============================================================================

-- ─────────────────────────────────────────────────────────────────────────────
-- 1. SHIFT COUNTS BY DATE RANGE
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Jan 2026 active shifts' AS Period, COUNT(*) AS Total,
       SUM(CASE WHEN Employeetimecard_id > 0 THEN 1 ELSE 0 END) AS WithTC,
       SUM(CASE WHEN actualtimein IS NOT NULL THEN 1 ELSE 0 END) AS WithActualIn
FROM clientscheduleshift
WHERE IsActive = 1 AND DATE(datetimein) >= '2026-01-01' AND DATE(datetimein) <= '2026-01-31'

UNION ALL

SELECT 'Feb 1-10 active shifts', COUNT(*),
       SUM(CASE WHEN Employeetimecard_id > 0 THEN 1 ELSE 0 END),
       SUM(CASE WHEN actualtimein IS NOT NULL THEN 1 ELSE 0 END)
FROM clientscheduleshift
WHERE IsActive = 1 AND DATE(datetimein) >= '2026-02-01' AND DATE(datetimein) <= '2026-02-10'

UNION ALL

SELECT 'Feb 11-16 active shifts', COUNT(*),
       SUM(CASE WHEN Employeetimecard_id > 0 THEN 1 ELSE 0 END),
       SUM(CASE WHEN actualtimein IS NOT NULL THEN 1 ELSE 0 END)
FROM clientscheduleshift
WHERE IsActive = 1 AND DATE(datetimein) >= '2026-02-11' AND DATE(datetimein) <= '2026-02-16'

UNION ALL

SELECT 'Feb 17+ active shifts', COUNT(*),
       SUM(CASE WHEN Employeetimecard_id > 0 THEN 1 ELSE 0 END),
       SUM(CASE WHEN actualtimein IS NOT NULL THEN 1 ELSE 0 END)
FROM clientscheduleshift
WHERE IsActive = 1 AND DATE(datetimein) >= '2026-02-17';


-- ─────────────────────────────────────────────────────────────────────────────
-- 2. BROKEN TIMECARD LINKS
--    Shifts that have Employeetimecard_id > 0 but the TC doesn't exist
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Broken TC links (shift→TC not found)' AS Check_Name,
       COUNT(*) AS Cnt
FROM clientscheduleshift css
LEFT JOIN employeetimecarddetail etcd ON etcd.Id = css.Employeetimecard_id
WHERE css.IsActive = 1
  AND css.Employeetimecard_id > 0
  AND etcd.Id IS NULL;


-- ─────────────────────────────────────────────────────────────────────────────
-- 3. UNLINKED TIMECARDS
--    Timecards that should be linked to a shift but aren't
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Unlinked TCs (Feb 11-16)' AS Check_Name,
       COUNT(*) AS Cnt
FROM employeetimecarddetail etcd
WHERE etcd.IsActive = 1
  AND DATE(etcd.Date) >= '2026-02-11'
  AND DATE(etcd.Date) <= '2026-02-16'
  AND NOT EXISTS (
      SELECT 1 FROM clientscheduleshift css
      WHERE css.Employeetimecard_id = etcd.Id
        AND css.IsActive = 1
  );


-- ─────────────────────────────────────────────────────────────────────────────
-- 4. DUPLICATE SHIFTS CHECK
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Active duplicate groups (Jan-Feb 2026)' AS Check_Name,
       COUNT(*) AS Cnt
FROM (
    SELECT ModalId, Client_id, datetimein, datetimeout
    FROM clientscheduleshift
    WHERE IsActive = 1 AND ModalId > 0
      AND DATE(datetimein) >= '2026-01-01'
      AND DATE(datetimein) <= '2026-02-28'
    GROUP BY ModalId, Client_id, datetimein, datetimeout
    HAVING COUNT(*) > 1
) dupes;


-- ─────────────────────────────────────────────────────────────────────────────
-- 5. ORPHANED CHILD RECORDS
-- ─────────────────────────────────────────────────────────────────────────────

SELECT 'Orphaned scheduleshiftscandetail' AS Check_Name,
       COUNT(*) AS Cnt
FROM scheduleshiftscandetail ssd
LEFT JOIN clientscheduleshift css ON css.Id = ssd.ShiftId
WHERE css.Id IS NULL;

SELECT 'Orphaned employeepunchesalert' AS Check_Name,
       COUNT(*) AS Cnt
FROM employeepunchesalert epa
LEFT JOIN clientscheduleshift css ON css.Id = epa.Clientscheduleshift_id
WHERE css.Id IS NULL AND epa.IsActive = 1;

SELECT 'Orphaned employeescheduleshiftclaim' AS Check_Name,
       COUNT(*) AS Cnt
FROM employeescheduleshiftclaim esc
LEFT JOIN clientscheduleshift css ON css.Id = esc.ClientScheduleShiftID
WHERE css.Id IS NULL;

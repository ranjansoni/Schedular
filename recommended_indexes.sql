-- ==========================================================================
-- RECOMMENDED INDEX CHANGES for Schedule Shift processing
-- Reviewed against existing indexes from SHOW INDEX output.
--
-- Section 1: NEW indexes to add
-- Section 2: DUPLICATE indexes to drop (optional cleanup — saves disk + write overhead)
--
-- IMPORTANT: Test on a staging/dev environment first.
-- Use ALGORITHM=INPLACE, LOCK=NONE to avoid blocking the production table.
-- ==========================================================================


-- ========================================
-- SECTION 1: INDEXES TO ADD
-- ========================================

-- ---------------------------------------------------------------------------
-- 1A. COMPOSITE: (Client_id, employeeid, datetimein, datetimeout)
--     on clientscheduleshift
--
-- WHY: This is the SINGLE BIGGEST WIN.
--
-- The duplicate check in ProcessScheduleModal (line 188) runs:
--   SELECT count(*) FROM clientscheduleshift
--   WHERE Client_id = ? AND employeeid = ? AND datetimein >= ? AND datetimeout <= ?
--
-- Right now, MySQL can only use ONE of the existing single-column indexes
-- (Client_id OR employeeid OR datetimein). With 4.5M rows, it still scans
-- thousands of rows per call. This query runs once per model per day —
-- potentially 460,000+ times per job execution.
--
-- With this composite index, each duplicate check becomes a tight range scan
-- on 4 columns — typically 0-2 rows examined instead of thousands.
--
-- EXISTING single-column indexes on these columns (Client_id, employeeid,
-- datetimein) remain useful for OTHER queries in the web/mobile app, so keep them.
-- ---------------------------------------------------------------------------
ALTER TABLE clientscheduleshift
  ADD INDEX idx_css_duplicate_check (Client_id, employeeid, datetimein, datetimeout),
  ALGORITHM=INPLACE, LOCK=NONE;


-- ---------------------------------------------------------------------------
-- 1B. COMPOSITE: (fromdate, ModalId) on clientscheduleshift
--
-- WHY: The reset/inactive DELETE (batch 2) filters:
--   WHERE css.fromdate > CURDATE() AND css.ModalId IN (...)
--
-- The existing FromDate_IX (fromdate only) handles the range scan, but MySQL
-- then must look up each row to check ModalId. Adding ModalId as a second
-- column lets MySQL filter both conditions from the index without hitting
-- the clustered row data.
--
-- With 4.5M rows this can cut the pre-computation SELECT from ~2-5s to <1s.
-- ---------------------------------------------------------------------------
ALTER TABLE clientscheduleshift
  ADD INDEX idx_css_fromdate_modalid (fromdate, ModalId),
  ALGORITHM=INPLACE, LOCK=NONE;


-- ========================================
-- SECTION 2: DUPLICATE INDEXES TO DROP
-- (Optional — saves ~5-10% write overhead and disk space)
-- ========================================
-- Run these only after verifying no query plans depend on the specific index name.
-- If unsure, leave them for now — they don't cause correctness issues.

-- ---------------------------------------------------------------------------
-- 2A. clientscheduleshift has THREE indexes on GroupScheduleId:
--   - clientscheduleshift_GroupScheduleId_idx (GroupScheduleId)
--   - index_nameIX_ShiftGroupId (GroupScheduleId)
--   - GroupScheduleId_ix (GroupScheduleId)
--
-- Keep ONE (e.g. clientscheduleshift_GroupScheduleId_idx), drop the other two.
-- ---------------------------------------------------------------------------
 ALTER TABLE clientscheduleshift DROP INDEX index_nameIX_ShiftGroupId;
 ALTER TABLE clientscheduleshift DROP INDEX GroupScheduleId_ix;

-- ---------------------------------------------------------------------------
-- 2B. clientscheduleshift has TWO indexes on Client_id:
--   - Client_id (Client_id)
--   - index_nameIX_ClientId (Client_id)
--
-- Keep ONE, drop the other.
-- ---------------------------------------------------------------------------
 ALTER TABLE clientscheduleshift DROP INDEX index_nameIX_ClientId;

-- ---------------------------------------------------------------------------
-- 2C. index_nameIX_IsActive (IsActive) is a prefix of
--     index_nameIX_IsActive_CompanyId (IsActive, CompanyID).
--
-- Any query that uses only IsActive can use the composite index.
-- The single-column index is redundant.
-- ---------------------------------------------------------------------------
 ALTER TABLE clientscheduleshift DROP INDEX index_nameIX_IsActive;


-- ========================================
-- INDEXES THAT ALREADY EXIST (no action needed)
-- ========================================
-- idx_cs_modalid_todate_id (ModalId, todate, Id) — covers orphaned-model DELETE ✓
-- FromDate_IX (fromdate)                          — partially covers batch 2 DELETE ✓
--   (but 1B above adds the more efficient composite)
-- employeescheduleshiftclaim_clientscheduleshift_idx (ClientScheduleShiftID)
--   — covers the LEFT JOIN anti-join check                                   ✓
-- PRIMARY (Id) on job_clientschedulemodel                                    ✓
-- PRIMARY (Id) on clientschedulemodel                                        ✓

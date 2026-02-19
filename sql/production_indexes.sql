-- =============================================================================
-- JMScheduler — recommended index ADDITIONS on existing tables (production)
-- Run after or with production_job_tables.sql.
--
-- These indexes speed up the C# job's bulk queries (existing shift key load,
-- overlap interval load, cleanup deletes). Safe to add with ALGORITHM=INPLACE,
-- LOCK=NONE to avoid blocking web/mobile/alert traffic.
--
-- If an index already exists you will get "Duplicate key name" — safe to ignore.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1. clientscheduleshift: duplicate-check / key-load / overlap-interval query
--    Job loads existing keys with: WHERE datetimein >= ? AND datetimein <= ?
--    This composite makes that range scan + column access very efficient.
-- -----------------------------------------------------------------------------
ALTER TABLE clientscheduleshift
  ADD INDEX idx_css_duplicate_check (Client_id, employeeid, datetimein, datetimeout),
  ALGORITHM=INPLACE, LOCK=NONE;

-- -----------------------------------------------------------------------------
-- 2. clientscheduleshift: cleanup phase (reset/inactive shift deletion)
--    CleanupService finds IDs with: WHERE fromdate > CURDATE() AND ModalId IN (...)
-- -----------------------------------------------------------------------------
ALTER TABLE clientscheduleshift
  ADD INDEX idx_css_fromdate_modalid (fromdate, ModalId),
  ALGORITHM=INPLACE, LOCK=NONE;


-- =============================================================================
-- OPTIONAL: Drop duplicate/redundant indexes (saves disk + write overhead)
-- Run only after verifying nothing else relies on the dropped index names.
-- If unsure, skip this section.
-- =============================================================================

-- Three indexes on GroupScheduleId — keep one, drop two:
-- ALTER TABLE clientscheduleshift DROP INDEX index_nameIX_ShiftGroupId;
-- ALTER TABLE clientscheduleshift DROP INDEX GroupScheduleId_ix;

-- Two indexes on Client_id — keep one, drop one:
-- ALTER TABLE clientscheduleshift DROP INDEX index_nameIX_ClientId;

-- IsActive single-column is redundant with (IsActive, CompanyID):
-- ALTER TABLE clientscheduleshift DROP INDEX index_nameIX_IsActive;

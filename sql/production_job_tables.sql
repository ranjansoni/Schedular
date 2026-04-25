-- =============================================================================
-- JMScheduler job tables for production
-- Run this once per database before (or the job will create them on first run).
-- Safe to re-run: uses CREATE TABLE IF NOT EXISTS.
--
-- For index ADDITIONS on existing tables (clientscheduleshift), run
-- production_indexes.sql as well.
-- =============================================================================

-- 1. Audit log: every shift attempt per run (Created/Duplicate/Overlap/Error)
CREATE TABLE IF NOT EXISTS job_shift_audit_log (
  Id               BIGINT AUTO_INCREMENT PRIMARY KEY,
  RunId            VARCHAR(36) NOT NULL,
  RunDate          DATETIME NOT NULL,
  ModalId          INT NOT NULL,
  ShiftId          BIGINT NULL,
  EmployeeId       INT NOT NULL,
  ClientId         INT NOT NULL,
  DateTimeIn       DATETIME NOT NULL,
  DateTimeOut      DATETIME NOT NULL,
  Result           VARCHAR(20) NOT NULL,
  ErrorDescription VARCHAR(500) NULL,
  ModelType        VARCHAR(10) NOT NULL,
  RecurringPattern VARCHAR(50) NOT NULL,
  CreatedAt        DATETIME NOT NULL DEFAULT NOW(),
  INDEX idx_audit_runid (RunId),
  INDEX idx_audit_created (CreatedAt),
  INDEX idx_audit_result (Result),
  INDEX idx_audit_employee (EmployeeId, DateTimeIn)
) ENGINE=InnoDB;

-- 2. Overlap conflicts: shifts blocked due to same employee at different location
CREATE TABLE IF NOT EXISTS job_shift_conflicts (
  Id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
  RunId                VARCHAR(36) NOT NULL,
  ModalId              INT NOT NULL,
  EmployeeId           INT NOT NULL,
  ClientId             INT NOT NULL,
  DateTimeIn           DATETIME NOT NULL,
  DateTimeOut          DATETIME NOT NULL,
  ConflictingShiftId   BIGINT NULL,
  ConflictingModalId   INT NULL,
  ConflictingClientId  INT NOT NULL,
  ConflictDateTimeIn   DATETIME NOT NULL,
  ConflictDateTimeOut  DATETIME NOT NULL,
  DetectedAt           DATETIME NOT NULL DEFAULT NOW(),
  INDEX idx_conflict_runid (RunId),
  INDEX idx_conflict_employee (EmployeeId),
  INDEX idx_conflict_detected (DetectedAt)
) ENGINE=InnoDB;

-- 3. Run summary: one row per run (portal drill-down by RunId)
CREATE TABLE IF NOT EXISTS job_scheduler_run (
  RunId                VARCHAR(36) NOT NULL PRIMARY KEY,
  StartedAt            DATETIME NOT NULL,
  CompletedAt          DATETIME NULL,
  DurationSeconds      INT NULL,
  Status               VARCHAR(20) NOT NULL DEFAULT 'Running',
  WeeklyModelsLoaded   INT NOT NULL DEFAULT 0,
  RecordsConsidered    INT NOT NULL DEFAULT 0,
  ShiftsCreated        INT NOT NULL DEFAULT 0,
  ShiftsSkipped        INT NOT NULL DEFAULT 0,
  OrphanedDeleted      INT NOT NULL DEFAULT 0,
  ResetDeleted         INT NOT NULL DEFAULT 0,
  AuditEntriesCount    INT NOT NULL DEFAULT 0,
  ConflictsCount       INT NOT NULL DEFAULT 0,
  ErrorMessage         VARCHAR(500) NULL,
  INDEX idx_run_started (StartedAt)
) ENGINE=InnoDB;

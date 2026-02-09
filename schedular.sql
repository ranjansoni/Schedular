DELIMITER $$
CREATE PROCEDURE `CallProcessScheduleModal`(IN ScheduleDateTime datetime, IN Advancedays int)
proc:begin
-- 09/11/2019 - changed drop table to insert table. Potentially causing slowness in the server due to too many DDLs in the SP

DECLARE P_companyid INT;
DECLARE P_clientid INT;
DECLARE P_advancedays INT;
DECLARE P_scheduledatetime datetime;
DECLARE P_days INT;

DECLARE P_StartDateTime datetime;
DECLARE P_EndDateTime datetime;
DECLARE P_SessionId varchar(50);
DECLARE P_nextscheduledate datetime;

DECLARE P_modalcount INT;
Declare P_jobName varchar(45);

SET SESSION time_zone = 'US/Eastern';

-- Configuration changes on 26May25
CALL mysql.rds_set_configuration('innodb_flush_log_at_trx_commit','2');
SET SESSION tmp_table_size = 536870912;
SET SESSION max_heap_table_size = 536870912;
-- SET autocommit=1;
CALL mysql.rds_set_configuration('innodb_purge_threads','4');


  
SELECT now() INTO P_StartDateTime;
SELECT uuid() INTO P_SessionId;

set P_jobName = 'ShiftSchedular';
if(StartEvent(P_SessionId, P_StartDateTime, P_jobName) = 0) then 
	leave proc;
end if;
select 'befor if line 29';
-- for two or more week 
 if((select count(*) from clientschedulemodel where isActive = 1 and recurringon > 1 and Id not in(select modal_id from job_ClientscheduleShiftnextrunStatus)) > 0 ) then
    begin
     insert into job_ClientscheduleShiftnextrunStatus
		select Id,employeeid,Client_id,startdate,0,0 
        from clientschedulemodel where isActive=1 and recurringon > 1 
        and Id not in(select modal_id from job_ClientscheduleShiftnextrunStatus);
	 end;
	end if;
    select 'after if line 39';
    
	-- delete from employeescheduleshiftclaim where ClientScheduleShiftID in (select Id from clientscheduleshift where ModalId not in (select Id from clientschedulemodel) and ModalId > 0 and todate > now());    
	-- delete from employeescheduleshiftclaim where ClientScheduleShiftID in (select Id from clientscheduleshift where date(fromdate) > date(now()) and ModalId in (select Id from clientschedulemodel where IsModelReset = 1 or IsActive = 0));

select 'befor delete line 44';
    /* delete from clientscheduleshift where 
     ModalId not in (select Id from clientschedulemodel) and ModalId > 0 and todate > now()
     and Id not in (select ClientScheduleShiftID from employeescheduleshiftclaim);
*/

-- Ranjan modified the delete statement. 

insert into logs.jobtracking values ('deletes started');

-- ==========================================================================
-- OPTIMIZED DELETE PHASE
-- Key changes vs original:
--   1. LEFT JOIN instead of NOT IN (avoids correlated subquery per-row)
--   2. Pre-compute IDs to delete into temp tables (locks on clientschedulemodel
--      and employeescheduleshiftclaim are held only during the fast SELECT,
--      NOT during the entire delete loop)
--   3. fromdate > CURDATE() instead of date(fromdate) > date(now()) to allow
--      index usage on fromdate
--   4. No START TRANSACTION wrapping deletes — each DELETE auto-commits,
--      minimizing lock duration
--   5. DO SLEEP(0.1) between batches gives the web/mobile/alert job breathing room
--   6. Batch size increased to 5000 (pre-computed IDs make each batch cheaper)
-- ==========================================================================

-- ---- Batch 1: Orphaned shifts (ModalId references a deleted model) --------
DROP TEMPORARY TABLE IF EXISTS tmp_del_orphaned;
CREATE TEMPORARY TABLE tmp_del_orphaned (
    shift_id INT NOT NULL PRIMARY KEY
) ENGINE = MEMORY;

INSERT INTO tmp_del_orphaned
SELECT css.Id
FROM   clientscheduleshift css
       LEFT JOIN clientschedulemodel cm
              ON css.ModalId = cm.Id
       LEFT JOIN employeescheduleshiftclaim esc
              ON css.Id = esc.ClientScheduleShiftID
WHERE  cm.Id IS NULL                        -- model no longer exists
  AND  css.ModalId > 0
  AND  css.todate > NOW()
  AND  esc.ClientScheduleShiftID IS NULL;   -- shift is not claimed

INSERT INTO logs.jobtracking
VALUES (CONCAT('Orphaned shifts to delete: ', (SELECT COUNT(*) FROM tmp_del_orphaned)));

SET @rows_affected := 1;
WHILE @rows_affected > 0 DO
    DELETE FROM clientscheduleshift
    WHERE  Id IN (
               SELECT shift_id
               FROM  (SELECT shift_id FROM tmp_del_orphaned LIMIT 5000) AS batch
           );
    SET @rows_affected := ROW_COUNT();
    DELETE FROM tmp_del_orphaned LIMIT 5000;
    COMMIT;                -- release locks immediately
    DO SLEEP(0.1);         -- yield to web/mobile/alert traffic
END WHILE;

DROP TEMPORARY TABLE IF EXISTS tmp_del_orphaned;

INSERT INTO logs.jobtracking VALUES ('deletes done - 1');

-- ---- Batch 2: Shifts for reset / inactive models -------------------------
DROP TEMPORARY TABLE IF EXISTS tmp_del_reset;
CREATE TEMPORARY TABLE tmp_del_reset (
    shift_id INT NOT NULL PRIMARY KEY
) ENGINE = MEMORY;

INSERT INTO tmp_del_reset
SELECT css.Id
FROM   clientscheduleshift css
       INNER JOIN clientschedulemodel cm
              ON css.ModalId = cm.Id
             AND (cm.IsModelReset = 1 OR cm.IsActive = 0)
       LEFT JOIN employeescheduleshiftclaim esc
              ON css.Id = esc.ClientScheduleShiftID
WHERE  css.fromdate > CURDATE()             -- index-friendly (no date() wrap)
  AND  esc.ClientScheduleShiftID IS NULL;   -- shift is not claimed

INSERT INTO logs.jobtracking
VALUES (CONCAT('Reset/inactive shifts to delete: ', (SELECT COUNT(*) FROM tmp_del_reset)));

SET @rows_affected := 1;
WHILE @rows_affected > 0 DO
    DELETE FROM clientscheduleshift
    WHERE  Id IN (
               SELECT shift_id
               FROM  (SELECT shift_id FROM tmp_del_reset LIMIT 5000) AS batch
           );
    SET @rows_affected := ROW_COUNT();
    DELETE FROM tmp_del_reset LIMIT 5000;
    COMMIT;
    DO SLEEP(0.1);
END WHILE;

DROP TEMPORARY TABLE IF EXISTS tmp_del_reset;

SET @rows_affected := NULL;  -- free memory from large user variable
INSERT INTO logs.jobtracking VALUES ('deletes done');

select 'befor ClientShiftModalEditable proc call line 54';
      call ClientShiftModalEditable(now());
   
   select 'befor update line 58';
      update clientschedulemodel set lastrundate = DATE_ADD(now(), INTERVAL -1 DAY), IsModelReset = 0  where IsModelReset = 1;

      delete from job_clientschedulemodel;
      delete from job_clientscheduletempfunctiondata;
      delete from job_clientscheduletempfunctiondataweekly;
      delete from job_clientschedulefunctiondataHistory where date(scheduledate)< date(DATE_ADD(now(), INTERVAL -120 DAY));

 
set P_advancedays = Advancedays;
set P_scheduledatetime = ScheduleDateTime;
set P_days = 0;

-- 0 means schedule shift created for today only
if(Advancedays < 0) then
begin
select 'Please provide correct days!';
end;
else
	begin
		-- create temporary table job_companydetail engine=memory 
               
	-- drop table if exists job_companydetail;
		-- CREATE TABLE IF NOT EXISTS job_companydetail
        -- as
                
					while(P_advancedays >= 0) do
						set P_scheduledatetime = DATE_ADD(P_scheduledatetime, INTERVAL P_days DAY);
                        -- select P_scheduledatetime;
                        insert into logs.jobtracking values (P_advancedays);
						CALL ProcessScheduleModal( P_scheduledatetime,P_advancedays);
						set P_advancedays = P_advancedays - 1;
						set P_days = P_days + 1;
                        set P_scheduledatetime = ScheduleDateTime;
					end while;
				
					-- Monthly Recurring Schedule Call                    
                    IF (DAYNAME(NOW()) = 'Saturday') THEN
						call ProcessScheduleModal_Monthly(0);
					END IF;
	end;
end if;

  /*
  SELECT now() INTO P_EndDateTime;
  insert into eventlog
  select null,P_SessionId,P_StartDateTime,P_EndDateTime,TIMESTAMPDIFF(second,P_StartDateTime,P_EndDateTime),'ShiftSchedular',null;
  */
  
  SELECT now() INTO P_EndDateTime;
  call CompleteEvent(P_SessionId, P_EndDateTime, TIMESTAMPDIFF(second,P_StartDateTime,P_EndDateTime)); 

end$$
DELIMITER ;
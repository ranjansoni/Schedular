CREATE DEFINER=`jmadmin`@`%` PROCEDURE `ProcessScheduleModal`(In ScheduleDateTime datetime, In advanceDays int)
BEGIN

DECLARE P_modalid INT;
DECLARE P_lastrundate DATETIME;
DECLARE P_startdate DATETIME;
DECLARE P_enddate DATETIME;
DECLARE P_fromdate DATETIME;
DECLARE P_todate DATETIME;
DECLARE P_days INT;
DECLARE P_datetimein datetime;
DECLARE P_datetimeout datetime;
DECLARE P_employeeid INT;
DECLARE P_clientid INT;
DECLARE P_sunday tinyint(1);
DECLARE P_monday tinyint(1);
DECLARE P_tuesday tinyint(1);
DECLARE P_wednesday tinyint(1);
DECLARE P_thursday tinyint(1);
DECLARE P_friday tinyint(1);
DECLARE P_saturday tinyint(1);
DECLARE P_IsLateInAlert tinyint(1); 
DECLARE P_LateInDuration int(11); 
DECLARE P_IsLateOutAlert tinyint(1); 
DECLARE P_LateOutDuration int(11);
DECLARE P_IsCustomInAlert tinyint(1) ;
DECLARE P_IsCustomOutAlert tinyint(1);

DECLARE P_IsAutoClockOut tinyint(1);
DECLARE P_AutoClockOutSelectedValue varchar(255);
DECLARE P_AutoClockOutHour double;
DECLARE P_AutoClockOutMinutes INT;

DECLARE P_JobClassification_Id INT;
DECLARE P_IsTeamSchedule tinyint(1);
DECLARE P_IsRounding tinyint(1);
DECLARE P_RoundUp double;
DECLARE P_RoundDown double;
DECLARE P_IsFlatRate tinyint(1);
DECLARE P_FlatRate double;
DECLARE P_GroupId INT;

DECLARE P_nextschedule datetime;
DECLARE P_recurringon INT; 
DECLARE ClientId int;
DECLARE Shiftcount int;
DECLARE P_Duration decimal(10,2);

DECLARE P_Advancedays INT;

DECLARE P_IsOpenSchedule tinyint(1);
DECLARE P_IsPublished tinyint(1);



Declare p_IsScheduleClockInRestrictionEnable TINYINT(1);
Declare p_IsScheduleClockOutRestrictionEnable TINYINT(1);
Declare p_IsScheduleDurationRestrictionEnable TINYINT(1);
Declare p_ScheduleRestrictClockInBefore DOUBLE;
Declare p_ScheduleRestrictClockInAfter DOUBLE;
Declare p_ScheduleRestrictClockOutBefore DOUBLE;
Declare p_ScheduleRestrictClockOutAfter DOUBLE;
Declare p_ScheduleRestrictMinDuration DOUBLE;
Declare p_ScheduleRestrictMaxDuration DOUBLE;
Declare p_IsScheduleRestrictionEnable DOUBLE;
Declare p_CompanyID int(11);
Declare p_ScheduleType int(11);
Declare p_BreakDetailID int (11);
Declare p_Timein VARCHAR(12);
Declare p_Timeout VARCHAR(12);

Declare p_NewGroupID int (11);
declare p_IsSuppressedScheduleRestriction tinyint(1);
declare p_IsManagerApprovalEnabled tinyint(1);
declare P_ScheduleScanType int(11);

declare p_lastinseretedshiftid int(11);

DECLARE P_StartDateTime datetime;
DECLARE P_EndDateTime datetime;
DECLARE P_SessionId varchar(50);
Declare P_jobName varchar(45);
Declare P_UserNote text;

SET SESSION time_zone = 'US/Eastern';

set P_Advancedays = advanceDays;

SELECT now() INTO P_StartDateTime;
SELECT uuid() INTO P_SessionId;


/*
set P_jobName = 'ShiftSchedular';
call StartEvent(P_SessionId, P_StartDateTime, P_jobName);

SELECT now() INTO P_EndDateTime;
  call CompleteEvent(P_SessionId, P_EndDateTime, TIMESTAMPDIFF(second,P_StartDateTime,P_EndDateTime)); 
*/


-- set P_GroupId = 0;
-- 09/11/2019 changed temporary table to permanent table. 
-- Commented on 9November2023 for recurring issues

insert into job_clientschedulemodel
Select clientschedulemodel.Id,clientschedulemodel.employeeid,clientschedulemodel.Client_id,clientschedulemodel.fromdate
,clientschedulemodel.todate ,clientschedulemodel.expirydate,clientschedulemodel.timein,clientschedulemodel.timeout
,clientschedulemodel.duration,clientschedulemodel.dayofweek,clientschedulemodel.note,clientschedulemodel.recurringon
,clientschedulemodel.lastrundate,clientschedulemodel.CreateDate,clientschedulemodel.UpdateDate,clientschedulemodel.IsActive
,clientschedulemodel.DeactivationDate,clientschedulemodel.CreateUser_id,clientschedulemodel.UpdateUser_id,clientschedulemodel.startdate
,clientschedulemodel.enddate,clientschedulemodel.noenddate,clientschedulemodel.sunday,clientschedulemodel.monday,clientschedulemodel.tuesday
,clientschedulemodel.wednesday,clientschedulemodel.thursday,clientschedulemodel.friday,clientschedulemodel.saturday
,clientschedulemodel.IsLateInAlert,clientschedulemodel.LateInDuration,clientschedulemodel.IsLateOutAlert,clientschedulemodel.LateOutDuration
,clientschedulemodel.IsCustomInAlert,clientschedulemodel.IsCustomOutAlert,clientschedulemodel.IsAutoClockOut
,clientschedulemodel.AutoClockOutSelectedValue,clientschedulemodel.AutoClockOutHour,clientschedulemodel.AutoClockOutMinutes
,clientschedulemodel.JobClassification_Id,clientschedulemodel.IsTeamSchedule,clientschedulemodel.GroupScheduleId,clientschedulemodel.IsRounding,clientschedulemodel.RoundUp
,clientschedulemodel.RoundDown,clientschedulemodel.IsFlatRate,clientschedulemodel.FlatRate
,clientschedulemodel.IsOpenSchedule,clientschedulemodel.IsPublished,

clientschedulemodel.IsScheduleClockInRestrictionEnable,clientschedulemodel.IsScheduleClockOutRestrictionEnable,clientschedulemodel.IsScheduleDurationRestrictionEnable,
clientschedulemodel.ScheduleRestrictClockInBefore,clientschedulemodel.ScheduleRestrictClockInAfter,clientschedulemodel.ScheduleRestrictClockOutBefore,clientschedulemodel.ScheduleRestrictClockOutAfter,
clientschedulemodel.ScheduleRestrictMinDuration,clientschedulemodel.ScheduleRestrictMaxDuration,clientschedulemodel.IsScheduleRestrictionEnable,clientschedulemodel.CompanyID,clientschedulemodel.ScheduleType,BreakDetailID,
clientschedulemodel.IsSuppressedScheduleRestriction,clientschedulemodel.IsManagerApprovalEnabled

  from clientschedulemodel 
  INNER JOIN clientdetail on client_id = clientdetail.id and clientdetail.IsActive = 1
  inner join companydetail on companydetail.id = clientdetail.company_id and companydetail.IsActive = 1 
  and companydetail.AccountStatus = 'Active' 
  where (clientschedulemodel.isActive = 1 and (clientschedulemodel.enddate = '0001-01-01 00:00:00' or date(clientschedulemodel.enddate) >= date(ScheduleDateTime)) -- ticket 90357 clientschedulemodel.enddate >= now()) 
  and (clientschedulemodel.lastrundate = '0001-01-01 00:00:00' or date(clientschedulemodel.lastrundate ) < date(now())))
  -- and RecurringStatus != 'Stopped'
  and clientschedulemodel.RecurringType = 0; -- and clientschedulemodel.Id = 69987;
  -- 15April22

-- Loop through all the schedule model for provided client
while ((Select count(*) from job_clientschedulemodel) > 0 ) Do -- Commented as on date 8November2023 for unnecesary schedules are generated 
-- while (((Select count(*) from job_clientschedulemodel) > 0) and ((Select count(*) from job_clientschedulemodel) < 100)) Do
select Id,lastrundate,startdate,enddate,fromdate,todate,DATEDIFF(date(todate),date(fromdate)),Client_id,employeeid,sunday,monday,tuesday
,wednesday,thursday,friday,saturday,IsLateInAlert,LateInDuration,IsLateOutAlert,LateOutDuration,IsCustomInAlert,IsCustomOutAlert
,recurringon,duration,IsAutoClockOut,AutoClockOutSelectedValue,AutoClockOutHour,AutoClockOutMinutes 
,JobClassification_Id,IsTeamSchedule,GroupScheduleId,IsRounding,RoundUp,RoundDown,IsFlatRate,FlatRate,IsOpenSchedule,IsPublished,
IsScheduleClockInRestrictionEnable,IsScheduleClockOutRestrictionEnable,IsScheduleDurationRestrictionEnable,
ScheduleRestrictClockInBefore,ScheduleRestrictClockInAfter,ScheduleRestrictClockOutBefore,ScheduleRestrictClockOutAfter,
ScheduleRestrictMinDuration,ScheduleRestrictMaxDuration,IsScheduleRestrictionEnable,CompanyID,ScheduleType,BreakDetailID,
timein,timeout,IsSuppressedScheduleRestriction,IsManagerApprovalEnabled

into P_modalid,P_lastrundate,P_startdate,P_enddate,P_fromdate,P_todate,P_days,P_clientid,P_employeeid,P_sunday,P_monday,P_tuesday
,P_wednesday,P_thursday,P_friday,P_saturday,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration,P_IsCustomInAlert
,P_IsCustomOutAlert,P_recurringon,P_Duration,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
,P_JobClassification_Id,P_IsTeamSchedule,P_GroupId,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate,P_IsOpenSchedule,P_IsPublished,

p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,
p_Timein,p_Timeout,p_IsSuppressedScheduleRestriction,p_IsManagerApprovalEnabled


from job_clientschedulemodel limit 1;
 
 -- select P_days;

 select ScheduleScanType, UserNote into P_ScheduleScanType, P_UserNote from clientschedulemodel where id = P_modalid;
 -- ticket 90357
 if(date(P_startdate) <= date(now()) and (date(P_enddate) >= date(ScheduleDateTime) or P_enddate = '0001-01-01 00:00:00')) then
 -- if(date(P_startdate) <= date(now()) and (P_enddate >= now() or P_enddate = '0001-01-01 00:00:00')) then
 begin    
    -- Check to see if shift is created across days and set date in/out accordingly
    if(P_days > 0) then
    begin
		select timestamp(date(ScheduleDateTime), time(P_fromdate)) into P_datetimein;
		select timestamp(DATE_ADD(date(ScheduleDateTime), INTERVAL P_days DAY), time(P_todate)) into P_datetimeout;
    end;
    else
    begin
		select timestamp(date(ScheduleDateTime), time(P_fromdate)) into P_datetimein;
		select timestamp(date(ScheduleDateTime), time(P_todate)) into P_datetimeout;
    end;
    end if;
    
    -- Check to see if schedule already exist 
    /*
    ScheduleType = 0, - Individual
    ScheduleType = 1, - OpenWithAllClaim
    ScheduleType = 2, - OpenWithSelectedClaim
    ScheduleType = 3, - TeamSchedule
    */
    if( (p_ScheduleType = 1) OR  ((select count(*) from clientscheduleshift where Client_id = P_clientid and employeeid = P_employeeid and datetimein >=P_datetimein and datetimeout <= P_datetimeout) = 0 )) then
    begin
    
		if(P_sunday = true and lower(DAYNAME(P_datetimein)) = 'sunday') then
        begin
        
           if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin       
				
                
                If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) 
                select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                
                -- select 'run';
               INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`,
                `IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,
                z.BreakDetailID,z.IsSuppressedScheduleRestriction,z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;

                update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else 
				set p_NewGroupID = P_GroupId;
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
				`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`
                ,`BreakDetailID`,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,
                p_IsManagerApprovalEnabled,
                P_ScheduleScanType,P_UserNote;
        			
                    set p_lastinseretedshiftid =  last_insert_id();
				Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
                call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
				update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
            end if;
            
            
            end;
            end if;
        end;
        end if;
        
        if(P_monday = true and lower(DAYNAME(P_datetimein)) = 'monday') then
        begin
		  if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin
            
            If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                 -- select 'run1';
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`
                ,`BreakDetailID`,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,z.IsSuppressedScheduleRestriction,
                z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;
               
               update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else 
				set p_NewGroupID = P_GroupId;
                 INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`
                ,`BreakDetailID`,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                 p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,p_IsManagerApprovalEnabled,
                P_ScheduleScanType,P_UserNote;
            
            set p_lastinseretedshiftid =  last_insert_id();
				Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
				update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
            end if;
            
            end;
           end if;
        end;
        end if;
        
        if(P_tuesday = true and lower(DAYNAME(P_datetimein)) = 'tuesday') then
        begin
        if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin 
				
                If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                -- select 'run2';
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,z.IsSuppressedScheduleRestriction,
                z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;
               
               
               update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else 
				set p_NewGroupID = P_GroupId;
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
                `IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,p_IsManagerApprovalEnabled,
                P_ScheduleScanType,P_UserNote;
            
            set p_lastinseretedshiftid =  last_insert_id();
				Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
				update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
            end if;
                
			end;
           end if;
        end;
        end if;
        
        
        
        if(P_wednesday = true and lower(DAYNAME(P_datetimein)) = 'wednesday') then
        begin			
         if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin 
				
                If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                -- select 'run3';
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,z.IsSuppressedScheduleRestriction,
                z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;
               
               update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else 
				set p_NewGroupID = P_GroupId;
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
                `IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`
                ,`BreakDetailID`,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,p_IsManagerApprovalEnabled,
                P_ScheduleScanType,P_UserNote;
            
            set p_lastinseretedshiftid =  last_insert_id();
            Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) 
            select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID 
            from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
			update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
            end if;
                
			end;
           end if;
        end;
        end if;
        
        if(P_thursday = true and lower(DAYNAME(P_datetimein)) = 'thursday') then
        begin
         if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin 
				
                If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                -- select 'run4';
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,z.IsSuppressedScheduleRestriction,
                z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;
               
               update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else 
				set p_NewGroupID = P_GroupId;
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
                `IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,p_IsManagerApprovalEnabled,
                P_ScheduleScanType,P_UserNote;
			        
                    set p_lastinseretedshiftid =  last_insert_id();
				Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
				call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
                update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
            end if;
                
            end;
           end if;
        end;
        end if;
        
        if(P_friday = true and lower(DAYNAME(P_datetimein)) = 'friday') then
        begin
        
         if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin 
				
                If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                -- select 'run5';
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,z.IsSuppressedScheduleRestriction,
                z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;
               
               update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else 
				set p_NewGroupID = P_GroupId;
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
                `IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,
                p_IsManagerApprovalEnabled,P_ScheduleScanType,P_UserNote;
            
            set p_lastinseretedshiftid =  last_insert_id();
				Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
				call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
				update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
            end if;
                
			end;
           end if;
           
        end;
        end if;
        
        if(P_saturday = true and lower(DAYNAME(P_datetimein)) = 'saturday') then
        begin
        if(SELECT SpanClientScheduleShift (P_modalid,ScheduleDateTime,P_recurringon,P_Advancedays) = 0) then
            begin 
				
                If(P_GroupId is not null and P_GroupId != 0) then
				INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`) select z.Client_id,z.IsEmployeeSchedule,z.IsClientSchedule, now() from groupschedule as z where z.id = P_GroupId;
				set p_NewGroupID = last_insert_id();
                -- select 'run6';
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,z.Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,p_NewGroupID,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,z.IsSuppressedScheduleRestriction,
                z.IsManagerApprovalEnabled,z.ScheduleScanType, z.UserNote
               from clientschedulemodel as z where z.GroupScheduleId = P_GroupId;
               
               update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;
			else            
				set p_NewGroupID = P_GroupId;
                
                INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,`UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
                `IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`
                ,`IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`,`ScheduleScanType`, `UserNote`)
				select P_modalid,P_employeeid,P_clientid,date(P_datetimein),date(P_datetimeout),P_datetimein,P_datetimeout,P_Duration,null
				,null,null,'Scheduled Event',now(),now(),true,null,41,41,p_Timein,p_TimeOut
				,null,false,false,false,false,false,false,false,false,P_IsLateInAlert,P_LateInDuration,P_IsLateOutAlert,P_LateOutDuration
				,P_IsCustomInAlert,P_IsCustomOutAlert,null,P_IsAutoClockOut,P_AutoClockOutSelectedValue,P_AutoClockOutHour,P_AutoClockOutMinutes
				,P_JobClassification_Id,P_IsTeamSchedule,p_NewGroupID,P_IsRounding,P_RoundUp,P_RoundDown,P_IsFlatRate,P_FlatRate
                ,P_IsOpenSchedule,P_IsPublished,
                p_IsScheduleClockInRestrictionEnable,p_IsScheduleClockOutRestrictionEnable,p_IsScheduleDurationRestrictionEnable,
			    p_ScheduleRestrictClockInBefore,p_ScheduleRestrictClockInAfter,p_ScheduleRestrictClockOutBefore,p_ScheduleRestrictClockOutAfter,
				p_ScheduleRestrictMinDuration,p_ScheduleRestrictMaxDuration,p_IsScheduleRestrictionEnable,p_CompanyID,p_ScheduleType,p_BreakDetailID,p_IsSuppressedScheduleRestriction,
                p_IsManagerApprovalEnabled,
                P_ScheduleScanType,P_UserNote;
            
            set p_lastinseretedshiftid =  last_insert_id();
				Insert into employeescheduleshiftclaim(DateCreated,DateModified,EmployeeID,ClientScheduleShiftID,CompanyID,JobclassID) select b.DateCreated,b.DateModified, b.EmployeeID, last_insert_id(), b.CompanyID, b.JobclassID from employeescheduleshiftmodelclaim as b where b.ClientScheduleShiftModelID = P_modalid;
				call ProcessRecurring_ScheduleScanArea(P_modalid, p_lastinseretedshiftid);
                update job_ClientscheduleShiftnextrunStatus set Changestatus=1 where modal_id=P_modalid;  
            end if;
                
			end;
           end if;
        end;
        end if;

    end;
    end if;
    
 end;
 end if;
 
 delete from job_clientschedulemodel where Id = P_modalid;
 
 if(advanceDays = 0) then -- this indicates that this is the last iteration. now we will update the lastrundate so it doesn't run again today. 
 begin
	update clientschedulemodel set lastrundate = now() where Id = P_modalid;
    	
   SELECT jobs.scheduledate into P_nextschedule FROM job_clientscheduletempfunctiondataweekly jobs where jobs.modal_id=P_modalid and 
   date(jobs.scheduledate) in(select date(datetimein) from clientscheduleshift where ModalId=jobs.modal_id)
    ORDER BY jobs.scheduledate DESC LIMIT 1;

    select count(*) into Shiftcount from clientscheduleshift where ModalId = P_modalid and date(datetimein) =date(P_nextschedule);
    if(Shiftcount IS NOT NULL)then
	begin
      update job_ClientscheduleShiftnextrunStatus set Nextscheduledate =date_add(P_nextschedule, interval 0 day),Changestatus=0 where modal_id = P_modalid and Changestatus=1;
      update job_ClientscheduleShiftnextrunStatus set ModalEditmode=0 where modal_id = P_modalid;
	end;
    else 
    begin
    
    SELECT DISTINCT scheduledate into P_nextschedule  FROM job_clientscheduletempfunctiondataweekly where modal_id = P_modalid ORDER BY weekcount DESC LIMIT 1,1 ;
     update job_ClientscheduleShiftnextrunStatus set Nextscheduledate =date_add(P_nextschedule, interval 0 day),Changestatus=0,ModalEditmode=0 where modal_id = P_modalid and Changestatus=1;
     update job_ClientscheduleShiftnextrunStatus set ModalEditmode=0 where modal_id = P_modalid;
    end;
   end if; 

    delete from job_Clientscheduletempfunctiondata;
    delete from job_clientscheduletempfunctiondataweekly where modal_id=P_modalid;
 end;
 end if;
  
END WHILE;

END
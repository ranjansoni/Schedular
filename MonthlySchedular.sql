CREATE DEFINER=`jmadmin`@`%` PROCEDURE `ProcessScheduleModal_Monthly`(
p_scheudleid int(11)
)
BEGIN

declare p_calculatedcount int(11);
declare p_count int(11);
declare p_totalweekdays int(11);
declare p_date datetime;
declare p_monthdate datetime;
declare p_monthstartdate datetime;
DECLARE P_sunday tinyint(1);
DECLARE P_monday tinyint(1);
DECLARE P_tuesday tinyint(1);
DECLARE P_wednesday tinyint(1);
DECLARE P_thursday tinyint(1);
DECLARE P_friday tinyint(1);
DECLARE P_saturday tinyint(1);
declare p_MonthlyRecurringType int(11);
declare P_Id int(11);
declare p_recurringscheduledate datetime;
declare p_recurringcount int(11);
declare P_datetimein datetime;
declare P_datetimeout datetime;
declare p_timein varchar(225);
declare p_timeout varchar(255);
declare P_clientid int(11);
declare P_employeeid int(11);
declare p_enddate datetime;
declare p_modelstartdate datetime;
declare p_recurringstartdate datetime;
declare P_modelgroupid int(11);
declare P_newmodelgroupid int(11);
set p_recurringcount =0;

set P_modelgroupid = null;
set P_newmodelgroupid = null;
set p_modelstartdate = null;




if(p_scheudleid > 0) then 

-- call ResetScheduleModal(p_scheudleid);
-- call JM_ResetScheduleModal_Monthly(p_scheudleid);
set p_modelstartdate  = (select startdate from clientschedulemodel where id = p_scheudleid);
end if;


while (p_recurringcount < 3) do begin 

if(p_modelstartdate is not null) then begin 
set p_recurringscheduledate  = date_add(p_modelstartdate, interval p_recurringcount month);
end;
else begin 
set p_recurringscheduledate  = date_add(now(), interval p_recurringcount month);
end;
end if;


-- select 1, p_count;
select p_recurringscheduledate;

set p_count= (Select count(1) from clientschedulemodel where date(p_recurringscheduledate) >= date(startdate) 
and  RecurringType = 1 and (lastrundate is null or  month(ifnull(lastrundate,now())) = month (p_recurringscheduledate) 
and year(ifnull(lastrundate,now())) <= year (p_recurringscheduledate))
and (date(enddate) = date('0001-01-01 00:00:00') or date(p_recurringscheduledate) <= date(enddate))
and (p_scheudleid is null or p_scheudleid  = 0 or p_scheudleid = id)
);


set p_calculatedcount = 0;
while(p_count > 0) do
begin 

-- Select id, sunday, monday, tuesday, wednesday, thursday, friday, saturday, MonthlyRecurringType,timein,timeout,Client_id,employeeid,startdate,GroupScheduleId
Select id, sunday, monday, tuesday, wednesday, thursday, friday, saturday, MonthlyRecurringType,fromDate,toDate,Client_id,employeeid,startdate,GroupScheduleId
into P_Id, P_sunday, P_monday,P_tuesday,P_wednesday,P_thursday,P_friday,P_saturday,p_MonthlyRecurringType,p_timein,p_timeout,P_clientid,P_employeeid, p_recurringstartdate,P_modelgroupid
 from clientschedulemodel where date(p_recurringscheduledate) >= date(startdate) and RecurringType = 1 and 
 (lastrundate is null or  month(ifnull(lastrundate,now())) = month (p_recurringscheduledate) and year(ifnull(lastrundate,now())) <= year (p_recurringscheduledate))
 and (date(enddate) = date('0001-01-01 00:00:00') or date(p_recurringscheduledate) <= date(enddate))
and (p_scheudleid is null or p_scheudleid  = 0 or p_scheudleid = id)
-- and RecurringStatus != 'Stopped'
 order by id  limit 1  offset p_calculatedcount;
 
 -- select p_Id;
  
-- Find first day of month
set p_monthstartdate = (SELECT date_add(date_add(LAST_DAY(p_recurringscheduledate),interval 1 DAY),interval -1 MONTH));
set p_date = p_monthstartdate;
set p_totalweekdays = 0;

		while(p_totalweekdays < 7) do
		begin 
		
        set p_date = (select date_add(p_monthstartdate, interval p_totalweekdays DAY));
        
         
        if((WEEKDAY(p_date) = 0 and P_monday = 1)
        || (WEEKDAY(p_date) = 1 and P_tuesday = 1)
        || (WEEKDAY(p_date) = 2 and P_wednesday = 1)
        || (WEEKDAY(p_date) = 3 and P_thursday = 1)
        || (WEEKDAY(p_date) = 4 and P_friday = 1)
        || (WEEKDAY(p_date) = 5 and P_saturday = 1)
        || (WEEKDAY(p_date) = 6 and P_sunday = 1)
        ) then begin 
			
            set p_monthdate = (select date_add(p_date, interval (p_MonthlyRecurringType * 7) DAY));
            
            -- If month is not same and deduct days 
            if(month(p_monthdate) != month(p_monthstartdate)) then begin 
				
                set p_monthdate = (select date_add(p_monthdate, interval -7 DAY));
					
            end; end if;
            
            
			-- Select p_monthdate, P_Id, p_date, p_MonthlyRecurringType;
			
            -- Corrected this on 13November2023
            set P_datetimein = timestamp(date(p_monthdate),TIME_FORMAT(p_timein, "%H:%i:%s"));
            set P_datetimeout = timestamp(date(p_monthdate),TIME_FORMAT(p_timeout, "%H:%i:%s"));
                
                -- if(P_modelgroupid is null )
				if(P_modelgroupid is null or P_modelgroupid = 0)
                then begin
                  if((select count(id) from clientscheduleshift where Client_id = P_clientid and employeeid = P_employeeid and datetimein >=P_datetimein and datetimeout <= P_datetimeout) = 0 ) then
				begin
                if(date(P_datetimein) >= date(p_recurringstartdate)) then begin 
             -- select 'run';
               INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`,
                `IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(p_monthdate),
                -- date(p_monthdate)
                date(DATE_ADD(date(p_monthdate), INTERVAL DATEDIFF(date(z.todate),date(z.fromdate)) DAY))
                -- , timestamp(date(p_monthdate), time(z.timein)),
                , timestamp(date(p_monthdate),TIME_FORMAT(z.fromdate, "%H:%i:%s") )
                -- timestamp(date(p_monthdate), time(z.timeout))
                --  , timestamp(date(p_monthdate),TIME_FORMAT(z.todate, "%H:%i:%s") )
                , timestamp(date(DATE_ADD(date(p_monthdate), INTERVAL DATEDIFF(date(z.todate),date(z.fromdate)) DAY)),TIME_FORMAT(z.todate, "%H:%i:%s") )
                ,z.Duration,null
				,null,null,'Schedule Event Monthly',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,z.GroupScheduleId,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,
                z.IsSuppressedScheduleRestriction, z.IsManagerApprovalEnabled, z.UserNote
               from clientschedulemodel as z where z.id = P_Id;
               
                call ProcessRecurring_ScheduleScanArea(P_Id, last_insert_id());
               end; end if;
               end; else begin 
               
              /* 
               INSERT INTO `duplicateclientscheduleshiftlogs` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`,
                `IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`)
				Select z.id,z.employeeid,z.Client_id , date(p_monthdate),date(p_monthdate)
                -- , timestamp(date(p_monthdate), time(z.timein)),
                , timestamp(date(p_monthdate),TIME_FORMAT(z.fromdate, "%H:%i:%s") )
                -- timestamp(date(p_monthdate), time(z.timeout)),z.Duration,null
                , timestamp(date(p_monthdate),TIME_FORMAT(z.todate, "%H:%i:%s") )
				,null,null,'Schedule Event Monthly',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,z.GroupScheduleId,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,
                z.IsSuppressedScheduleRestriction,z.IsManagerApprovalEnabled
               from clientschedulemodel as z where z.id = P_Id; */
               
               end; end if;
               end; else begin 
               
               -- group id case 
               
             
					INSERT INTO `groupschedule`(`Client_id`,`IsEmployeeSchedule`,`IsClientSchedule`,`DateCreated`)
					VALUES (P_clientid,1,0,now());
                    set P_newmodelgroupid =  LAST_INSERT_ID();
if((select count(id) from clientscheduleshift where Client_id = P_clientid and employeeid = P_employeeid and datetimein >=P_datetimein and datetimeout <= P_datetimeout) = 0 ) then
				begin
                 if(date(P_datetimein) >= date(p_recurringstartdate)) then begin 
					 INSERT INTO `clientscheduleshift` (`ModalId`,`employeeid`,`Client_id`,`fromdate`,`todate`,`datetimein`,`datetimeout`,`duration`,`actualtimein`,`actualtimeout`,`actualduration`,`note`,`CreateDate`,
                `UpdateDate`,`IsActive`,`DeactivationDate`,`CreateUser_id`,`UpdateUser_id`,`timein`,`timeout`,`Employeetimecard_id`,`IsInMissing`,`IsOutMissing`,`IsLateIn`,`IsEarlyOut`,`IsInMissingAlertSent`,`IsOutMissingAlertSent`,`IsLateInAlertSent`,`IsEarlyOutAlertSent`,`IsLateInAlert`,`LateInDuration`,`IsLateOutAlert`,`LateOutDuration`,`IsCustomInAlert`,`IsCustomOutAlert`,`WorkOrderID`,`IsAutoClockOut`,`AutoClockOutSelectedValue`,`AutoClockOutHour`,`AutoClockOutMinutes`,`JobClassification_Id`,`IsTeamSchedule`,`GroupScheduleId`,`IsRounding`,`RoundUp`,`RoundDown`,`IsFlatRate`,`FlatRate`,`IsOpenSchedule`,
				`IsPublished`,`IsScheduleClockInRestrictionEnable`,`IsScheduleClockOutRestrictionEnable`,`IsScheduleDurationRestrictionEnable`,
				`ScheduleRestrictClockInBefore`,`ScheduleRestrictClockInAfter`,`ScheduleRestrictClockOutBefore`,`ScheduleRestrictClockOutAfter`,
				`ScheduleRestrictMinDuration`,`ScheduleRestrictMaxDuration`,`IsScheduleRestrictionEnable`,`CompanyID`,`ScheduleType`,`BreakDetailID`,
                `IsSuppressedScheduleRestriction`,`IsManagerApprovalEnabled`, `UserNote`)
				Select z.id,z.employeeid,z.Client_id , date(p_monthdate),
                -- date(p_monthdate)
                date(DATE_ADD(date(p_monthdate), INTERVAL DATEDIFF(date(z.todate),date(z.fromdate)) DAY))
                -- , timestamp(date(p_monthdate), time(z.timein)),
                , timestamp(date(p_monthdate),TIME_FORMAT(z.fromdate, "%H:%i:%s") )
                -- timestamp(date(p_monthdate), time(z.timeout))
                -- , timestamp(date(p_monthdate),TIME_FORMAT(z.todate, "%H:%i:%s") )
                , timestamp(date(DATE_ADD(date(p_monthdate), INTERVAL DATEDIFF(date(z.todate),date(z.fromdate)) DAY)),TIME_FORMAT(z.todate, "%H:%i:%s") )
                ,z.Duration,null
				,null,null,'Schedule Event Monthly',now(),now(),true,null,41,41,z.Timein,z.TimeOut
				,null,false,false,false,false,false,false,false,false,z.IsLateInAlert,z.LateInDuration,z.IsLateOutAlert,z.LateOutDuration
				,z.IsCustomInAlert,z.IsCustomOutAlert,null,z.IsAutoClockOut,z.AutoClockOutSelectedValue,z.AutoClockOutHour,z.AutoClockOutMinutes
				,z.JobClassification_Id,z.IsTeamSchedule,P_newmodelgroupid,z.IsRounding,z.RoundUp,z.RoundDown,z.IsFlatRate,z.FlatRate
                ,z.IsOpenSchedule,z.IsPublished,
                z.IsScheduleClockInRestrictionEnable,z.IsScheduleClockOutRestrictionEnable,z.IsScheduleDurationRestrictionEnable,
			    z.ScheduleRestrictClockInBefore,z.ScheduleRestrictClockInAfter,z.ScheduleRestrictClockOutBefore,z.ScheduleRestrictClockOutAfter,
				z.ScheduleRestrictMinDuration,z.ScheduleRestrictMaxDuration,z.IsScheduleRestrictionEnable,z.CompanyID,z.ScheduleType,z.BreakDetailID,
                z.IsSuppressedScheduleRestriction, z.IsManagerApprovalEnabled, z.UserNote
               from clientschedulemodel as z 
               -- where z.GroupScheduleId = P_modelgroupid;
               where z.RecurringType = 1 and (z.GroupScheduleId = P_modelgroupid and z.GroupScheduleId is not null and z.GroupScheduleId != 0);
               end;  end if;
end;  end if;
	end;  end if;
            
        end; 
        end if;
		
        set p_totalweekdays= p_totalweekdays +1;
        end; end while;


set p_count = p_count - 1;
set p_calculatedcount = p_calculatedcount +1;
end;
end while;

if(P_modelgroupid is null) then begin 
 update clientschedulemodel set lastrundate = (select date_add(p_monthstartdate, interval 1 month))  where 
 date(p_recurringscheduledate) >= date(startdate) and RecurringType = 1 and 
 (lastrundate is null or  month(ifnull(lastrundate,now())) = month (p_recurringscheduledate) and year(ifnull(lastrundate,now())) <= year (p_recurringscheduledate))
and (date(enddate) = date('0001-01-01 00:00:00') or date(p_recurringscheduledate) <= date(enddate))
and (p_scheudleid is null or p_scheudleid  = 0 or p_scheudleid = id);

end;  else begin 
 update clientschedulemodel set lastrundate = (select date_add(p_monthstartdate, interval 1 month))  
 -- where GroupScheduleId = P_modelgroupid;
 where RecurringType = 1 and (GroupScheduleId = P_modelgroupid and GroupScheduleId is not null and GroupScheduleId != 0);
end; end if;

set p_recurringcount = p_recurringcount +1;
end; end while;


END
using JMScheduler.Core.Models;

namespace JMScheduler.Core.Services;

/// <summary>
/// Pure C# replacement for the SpanClientScheduleShift MySQL function (374 lines → ~80 lines).
///
/// For multi-week models (recurringon > 1), this calculates all valid schedule dates
/// within the advance window by stepping in (recurringon * 7)-day increments from an
/// anchor date, checking day-of-week flags at each step.
///
/// For weekly models (recurringon = 1), every matching day-of-week is valid — no
/// calculation needed (handled by the caller, not this class).
///
/// Key insight: The original SpanClientScheduleShift function populated
/// job_clientscheduletempfunctiondata and job_clientscheduletempfunctiondataweekly tables
/// and then checked if the run date was in the list. We do the same logic entirely in memory
/// with a HashSet, eliminating hundreds of thousands of DB round trips.
/// </summary>
public sealed class MultiWeekDateCalculator
{
    /// <summary>
    /// Calculate all valid schedule dates for a multi-week model within the given advance window.
    /// Returns a HashSet of dates (date-only, no time component) that should have shifts created.
    /// </summary>
    /// <param name="model">The schedule model with recurringon > 1.</param>
    /// <param name="anchorDate">
    /// The starting point for date generation.
    /// For first-time models: model.StartDate.
    /// For previously-run models: Nextscheduledate from job_ClientscheduleShiftnextrunStatus.
    /// For edited models with no shifts: model.StartDate (fallback).
    /// </param>
    /// <param name="restrictionDate">
    /// Only dates AFTER this date are valid.
    /// For normal mode: the last existing shift's datetimein date.
    /// For edit mode (ModalEditmode > 0): DateTime.Now (regenerate from today).
    /// For first-time models: DateTime.Now.AddDays(-1).
    /// </param>
    /// <param name="advanceDays">Number of days in the advance window.</param>
    /// <returns>Set of valid schedule dates (date-only) for this model.</returns>
    public HashSet<DateTime> CalculateValidDates(
        ScheduleModel model,
        DateTime anchorDate,
        DateTime restrictionDate,
        int advanceDays)
    {
        var validDates = new HashSet<DateTime>();

        if (model.RecurringOn <= 1)
            return validDates; // Weekly models don't need this calculation

        int recurInterval = model.RecurringOn;
        int daysPerCycle = 7 * recurInterval;

        // How many full cycles fit in the advance window
        // (matches: runinverval = Rundays / (7 * RecurInterval))
        int runInterval = advanceDays / daysPerCycle;
        int loopCounter = runInterval + 1;

        // Generate dates by walking backwards from the furthest cycle to the nearest
        // This mirrors the outer WHILE loop in SpanClientScheduleShift (lines 103-349)
        while (loopCounter >= 1)
        {
            // Calculate the start of this cycle's week
            // Matches: P_newschedudate = DATE_ADD(P_firstoccurrence, INTERVAL (DaysToSubtract)*runinverval DAY)
            DateTime weekStart;
            if (runInterval == 0)
            {
                weekStart = anchorDate.Date;
            }
            else
            {
                weekStart = anchorDate.Date.AddDays(daysPerCycle * runInterval);
            }

            // Walk through 7 days of this week, checking day-of-week flags
            // Matches the inner WHILE (P_Countloop >= 1) loop
            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                DateTime candidateDate = weekStart.AddDays(dayOffset);

                if (!model.IsScheduledForDay(candidateDate.DayOfWeek))
                    continue;

                // Only include dates after the restriction date
                // Matches: if(date(P_newschedudate) > date(P_restrictschedule))
                if (candidateDate.Date > restrictionDate.Date)
                {
                    validDates.Add(candidateDate.Date);
                }
            }

            runInterval--;
            loopCounter--;
        }

        return validDates;
    }

    /// <summary>
    /// Determine the anchor date and restriction date for a multi-week model.
    /// This encapsulates the complex anchor-date resolution logic from SpanClientScheduleShift
    /// lines 60-99 and the ClientShiftModalEditable logic.
    /// </summary>
    /// <param name="model">The schedule model.</param>
    /// <param name="trackingStatus">
    /// The model's row from job_ClientscheduleShiftnextrunStatus (null if no row exists).
    /// </param>
    /// <param name="lastShiftDate">
    /// The date of the most recent active shift for this model (null if no shifts exist).
    /// </param>
    /// <param name="lastHistoryDate">
    /// The last confirmed schedule date from job_clientschedulefunctiondataHistory
    /// where a matching shift exists (null if none).
    /// </param>
    /// <returns>Tuple of (anchorDate, restrictionDate) for use with CalculateValidDates.</returns>
    public (DateTime anchorDate, DateTime restrictionDate) ResolveAnchorAndRestriction(
        ScheduleModel model,
        NextRunStatus? trackingStatus,
        DateTime? lastShiftDate,
        DateTime? lastHistoryDate)
    {
        DateTime anchorDate;
        DateTime restrictionDate;

        if (model.HasNeverRun || trackingStatus == null)
        {
            // First time running — use model's start date as anchor
            // Matches SpanClientScheduleShift lines 64-68
            anchorDate = model.StartDate.Date;
            restrictionDate = DateTime.Now.Date.AddDays(-1);
        }
        else if (trackingStatus.ModalEditmode > 0)
        {
            // Model was edited — use tracking date as anchor, NOW as restriction
            // This causes regeneration from today forward
            // Matches SpanClientScheduleShift lines 120-127 (ModalEditmode > 0 path)
            anchorDate = trackingStatus.Nextscheduledate.Date;
            restrictionDate = DateTime.Now.Date;
        }
        else if (lastShiftDate == null)
        {
            // Model has been run before but all shifts were deleted
            // Matches SpanClientScheduleShift lines 76-86 (P_restrictschedule IS NULL path)
            anchorDate = model.StartDate.Date;
            restrictionDate = model.StartDate.Date.AddDays(-1);
        }
        else if (lastHistoryDate.HasValue)
        {
            // Normal case: use the last confirmed history date as anchor
            // Matches SpanClientScheduleShift lines 88-94
            anchorDate = lastHistoryDate.Value.Date;
            restrictionDate = lastShiftDate.Value.Date;
        }
        else
        {
            // Fallback: use tracking status date
            anchorDate = trackingStatus.Nextscheduledate.Date;
            restrictionDate = lastShiftDate.Value.Date;
        }

        return (anchorDate, restrictionDate);
    }
}

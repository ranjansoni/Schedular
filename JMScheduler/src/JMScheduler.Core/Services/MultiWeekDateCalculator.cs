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
    ///
    /// Cycles are always aligned to model.StartDate to prevent drift. Previous versions used
    /// anchorDate (last shift/tracking date) as the cycle root, which could be mid-cycle and
    /// cause shifts to be generated in the wrong week of the bi-weekly/tri-weekly pattern.
    /// </summary>
    /// <param name="model">The schedule model with recurringon > 1.</param>
    /// <param name="anchorDate">
    /// Used to determine the advance window end point (anchorDate + advanceDays).
    /// Cycle alignment always uses model.StartDate instead.
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
            return validDates;

        int daysPerCycle = 7 * model.RecurringOn;

        // Always align cycles to model.StartDate — this is the immutable cycle root
        DateTime cycleRoot = model.StartDate.Date;

        // End of the window: cover at least anchorDate + advanceDays + one extra cycle
        DateTime endDate = anchorDate.Date.AddDays(advanceDays + 7);

        // Jump to the first cycle that could contain dates near the restriction date
        int daysSinceRoot = Math.Max(0, (int)(restrictionDate.Date - cycleRoot).TotalDays);
        int completedCycles = daysSinceRoot / daysPerCycle;
        int startCycle = Math.Max(0, completedCycles - 1);
        DateTime firstCycleStart = cycleRoot.AddDays(startCycle * daysPerCycle);

        for (var weekStart = firstCycleStart; weekStart <= endDate; weekStart = weekStart.AddDays(daysPerCycle))
        {
            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                DateTime candidateDate = weekStart.AddDays(dayOffset);

                if (!model.IsScheduledForDay(candidateDate.DayOfWeek))
                    continue;

                if (candidateDate.Date > restrictionDate.Date)
                {
                    validDates.Add(candidateDate.Date);
                }
            }
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

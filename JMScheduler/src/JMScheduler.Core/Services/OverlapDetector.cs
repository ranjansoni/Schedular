using JMScheduler.Core.Models;

namespace JMScheduler.Core.Services;

/// <summary>
/// In-memory overlap detection engine for employee shift conflicts.
///
/// Loaded once at job start with all existing employee shift intervals in the date range,
/// then queried for each candidate shift before insertion.
///
/// Overlap rule:
///   Two shifts overlap if newShift.DateTimeIn &lt; existing.DateTimeOut
///                     AND newShift.DateTimeOut &gt; existing.DateTimeIn
///                     AND EmployeeId matches
///                     AND ClientId DIFFERS (same-location conflicts are handled by dedup).
///
/// EmployeeId = 0 (unassigned/open shifts) is always skipped — no overlap possible.
/// </summary>
public sealed class OverlapDetector
{
    /// <summary>Represents one existing shift interval for an employee.</summary>
    public readonly record struct ShiftInterval(
        DateTime Start,
        DateTime End,
        int ClientId,
        long? ShiftId,
        int ModalId);

    // Keyed by EmployeeId → list of intervals sorted by Start
    private readonly Dictionary<int, List<ShiftInterval>> _intervals = new();

    /// <summary>Total number of employee intervals loaded.</summary>
    public int TotalIntervals { get; private set; }

    /// <summary>Number of unique employees in the interval map.</summary>
    public int UniqueEmployees => _intervals.Count;

    /// <summary>
    /// Load intervals from the database result set.
    /// Called once during job initialization.
    /// </summary>
    public void Load(IEnumerable<(int EmployeeId, DateTime DateTimeIn, DateTime DateTimeOut,
        int ClientId, long ShiftId, int ModalId)> rows)
    {
        foreach (var (empId, dtIn, dtOut, clientId, shiftId, modalId) in rows)
        {
            if (empId == 0) continue; // Skip unassigned shifts

            if (!_intervals.TryGetValue(empId, out var list))
            {
                list = new List<ShiftInterval>();
                _intervals[empId] = list;
            }

            list.Add(new ShiftInterval(dtIn, dtOut, clientId, shiftId, modalId));
            TotalIntervals++;
        }

        // Sort each employee's intervals by Start for efficient scanning
        foreach (var list in _intervals.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
    }

    /// <summary>
    /// Check if a candidate shift overlaps with any existing shift for the same employee
    /// at a DIFFERENT client/location.
    /// </summary>
    /// <param name="employeeId">The employee ID of the candidate shift.</param>
    /// <param name="clientId">The client/location ID of the candidate shift.</param>
    /// <param name="dateTimeIn">Start time of the candidate shift.</param>
    /// <param name="dateTimeOut">End time of the candidate shift.</param>
    /// <returns>
    /// The first conflicting interval if an overlap is found at a different location;
    /// null if no overlap or same-location overlap (handled by dedup).
    /// </returns>
    public ShiftInterval? CheckOverlap(int employeeId, int clientId, DateTime dateTimeIn, DateTime dateTimeOut)
    {
        // EmployeeId=0 means unassigned — skip overlap check
        if (employeeId == 0) return null;

        if (!_intervals.TryGetValue(employeeId, out var intervals))
            return null; // No existing shifts for this employee

        // Linear scan through sorted intervals
        // Optimization: skip intervals that end before our start
        foreach (var existing in intervals)
        {
            // Early exit: if existing.Start >= dateTimeOut, all subsequent intervals are too late
            if (existing.Start >= dateTimeOut) break;

            // Overlap condition: newStart < existingEnd AND newEnd > existingStart
            if (dateTimeIn < existing.End && dateTimeOut > existing.Start)
            {
                // Only flag as conflict if different client/location
                if (existing.ClientId != clientId)
                {
                    return existing;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Register a newly created shift so subsequent overlap checks can detect it.
    /// Called after a shift is successfully categorized for insertion (not yet in DB).
    /// </summary>
    public void RegisterShift(int employeeId, int clientId, DateTime dateTimeIn, DateTime dateTimeOut,
        int modalId, long? shiftId = null)
    {
        if (employeeId == 0) return;

        if (!_intervals.TryGetValue(employeeId, out var list))
        {
            list = new List<ShiftInterval>();
            _intervals[employeeId] = list;
        }

        // Insert maintaining sort order (most new shifts append at end for same day)
        var interval = new ShiftInterval(dateTimeIn, dateTimeOut, clientId, shiftId, modalId);
        list.Add(interval);
        TotalIntervals++;

        // Re-sort only if the new interval is out of order (rare for sequential date processing)
        if (list.Count > 1 && list[^2].Start > interval.Start)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
    }
}

using JMScheduler.Core.Models;
using JMScheduler.Core.Services;

namespace JMScheduler.Core.Tests.Services;

public sealed class MultiWeekDateCalculatorTests
{
    private readonly MultiWeekDateCalculator _calculator = new();

    [Fact]
    public void CalculateValidDates_GeneratesReportedBiweeklyPatternInInitialWindow()
    {
        var model = CreateModel(
            startDate: new DateTime(2026, 6, 19),
            recurringOn: 2,
            friday: true,
            saturday: true,
            sunday: true);

        var dates = _calculator.CalculateValidDates(
            model,
            anchorDate: new DateTime(2026, 6, 19),
            restrictionDate: new DateTime(2026, 6, 18),
            advanceDays: 45);

        Assert.Equal(
            Dates(
                "2026-06-19", "2026-06-20", "2026-06-21",
                "2026-07-03", "2026-07-04", "2026-07-05",
                "2026-07-17", "2026-07-18", "2026-07-19",
                "2026-07-31", "2026-08-01", "2026-08-02"),
            dates.Order());
    }

    [Fact]
    public void CalculateValidDates_RollsBiweeklyPatternBeyondInitialWindow()
    {
        var model = CreateModel(
            startDate: new DateTime(2026, 6, 19),
            recurringOn: 2,
            friday: true,
            saturday: true,
            sunday: true);

        var dates = _calculator.CalculateValidDates(
            model,
            anchorDate: new DateTime(2026, 8, 2),
            restrictionDate: new DateTime(2026, 8, 2),
            advanceDays: 45);

        Assert.Equal(
            Dates(
                "2026-08-14", "2026-08-15", "2026-08-16",
                "2026-08-28", "2026-08-29", "2026-08-30",
                "2026-09-11", "2026-09-12", "2026-09-13"),
            dates.Order());
    }

    [Theory]
    [InlineData(3, "2026-07-10", "2026-07-31", "2026-08-21")]
    [InlineData(4, "2026-07-17", "2026-08-14", "2026-09-11")]
    public void CalculateValidDates_PreservesOriginalCycleForLongerIntervals(
        int recurringOn,
        string firstExpected,
        string secondExpected,
        string thirdExpected)
    {
        var model = CreateModel(
            startDate: new DateTime(2026, 6, 19),
            recurringOn: recurringOn,
            friday: true);

        var dates = _calculator.CalculateValidDates(
            model,
            anchorDate: new DateTime(2026, 7, 1),
            restrictionDate: new DateTime(2026, 6, 19),
            advanceDays: 90);

        Assert.Contains(DateTime.Parse(firstExpected), dates);
        Assert.Contains(DateTime.Parse(secondExpected), dates);
        Assert.Contains(DateTime.Parse(thirdExpected), dates);
        Assert.All(dates, date =>
            Assert.Equal(0, (date.Date - model.StartDate.Date).Days % (7 * recurringOn)));
    }

    [Fact]
    public void CalculateValidDates_RepeatedRollingRunsDoNotDriftOrDuplicate()
    {
        var model = CreateModel(
            startDate: new DateTime(2026, 6, 19),
            recurringOn: 2,
            friday: true,
            saturday: true,
            sunday: true);

        var firstRun = _calculator.CalculateValidDates(
            model,
            anchorDate: new DateTime(2026, 6, 19),
            restrictionDate: new DateTime(2026, 6, 18),
            advanceDays: 45);

        var secondRun = _calculator.CalculateValidDates(
            model,
            anchorDate: firstRun.Max(),
            restrictionDate: firstRun.Max(),
            advanceDays: 45);

        Assert.Empty(firstRun.Intersect(secondRun));

        var combined = firstRun.Union(secondRun).Order().ToList();
        Assert.Equal(combined.Count, combined.Distinct().Count());
        Assert.All(combined, date =>
        {
            var daysFromStart = (date.Date - model.StartDate.Date).Days;
            Assert.InRange(daysFromStart % 14, 0, 2);
        });
    }

    private static ScheduleModel CreateModel(
        DateTime startDate,
        int recurringOn,
        bool friday = false,
        bool saturday = false,
        bool sunday = false)
    {
        return new ScheduleModel
        {
            StartDate = startDate,
            EndDate = DateTime.MinValue,
            LastRunDate = DateTime.MinValue,
            RecurringType = 0,
            RecurringOn = recurringOn,
            Friday = friday,
            Saturday = saturday,
            Sunday = sunday
        };
    }

    private static List<DateTime> Dates(params string[] dates) =>
        dates.Select(DateTime.Parse).ToList();
}

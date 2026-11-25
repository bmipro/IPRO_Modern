namespace IPRO.Business.Services;

// 678 (audit 2026-11-24 C-3): the bounds of a recurring client invoice schedule, checked where one is saved
// (RecurringInvoicesController) and again where it runs (RecurringClientInvoiceJob). The job read the 200 earliest
// dates and made the first 100, so one adviser's 100 schedules dated year 1000, or with 3,000,000 days to pay (the
// due date then fails past year 9999 and never moves), headed the queue every hour and no other adviser's recurring
// invoice was ever made. Now: a date within a year back and five years ahead, 0 to 365 days to pay, every due
// schedule read (by Id, a page at a time), at most PerAdviserPerRun of one adviser's in a run, and a schedule outside
// the bounds is paused (IsActive off) with a warning instead of retried for ever.
public static class RecurringScheduleRules
{
    public const int MaxDueInDays = 365;
    public const int MaxDaysBack = 366;
    public const int MaxYearsAhead = 5;
    public const int PerAdviserPerRun = 25;
    public const int PageSize = 500;

    public static string? DateProblem(DateTime nextRunDate, DateTime today) =>
        nextRunDate.Date < today.Date.AddDays(-MaxDaysBack)
            ? "The next invoice date can be at most a year in the past."
            : nextRunDate.Date > today.Date.AddYears(MaxYearsAhead)
                ? $"The next invoice date can be at most {MaxYearsAhead} years ahead."
                : null;

    public static string? DueProblem(int dueInDays) =>
        dueInDays < 0 || dueInDays > MaxDueInDays ? $"Days to pay must be between 0 and {MaxDueInDays}." : null;

    // Why a schedule the job meets must stop rather than run, or null.
    public static string? ParkReason(DateTime nextRunDate, int dueInDays, DateTime nowUtc) =>
        DueProblem(dueInDays) ?? (nextRunDate.Date < nowUtc.Date.AddDays(-MaxDaysBack) ? "Its next invoice date is more than a year in the past." : null);
}

using Microsoft.Extensions.Logging;
using TicketMiser.Core.Entities;
using TicketMiser.Reliability;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// Enforces each provider's ceiling before a run starts. Rather than trust the schedule to
/// stay inside the bounds, every run asks permission and is refused when the budget is spent.
///
/// The measuring is <see cref="BudgetCalculator"/>'s job, in the reliability layer. What is
/// left here is only the decision.
/// </summary>
public class CreditBudgetGuard(BudgetCalculator budget, ILogger<CreditBudgetGuard> logger)
{
    public async Task<bool> TryReserveAsync(Source source, int estimatedRequests, CancellationToken ct)
    {
        var usage = await budget.GetUsageAsync(source, ct);

        if (usage.HourlyLimit is { } hourly && usage.RequestsLastHour + estimatedRequests > hourly)
        {
            logger.LogWarning("{Source}: hourly budget reached ({Used}/{Limit})",
                source.Key, usage.RequestsLastHour, hourly);
            return false;
        }

        if (usage.DailyLimit is { } daily && usage.RequestsLastDay + estimatedRequests > daily)
        {
            logger.LogWarning("{Source}: daily budget reached ({Used}/{Limit})",
                source.Key, usage.RequestsLastDay, daily);
            return false;
        }

        if (usage.MonthlyCreditLimit is { } monthly && usage.CreditsThisMonth >= monthly)
        {
            logger.LogWarning("{Source}: monthly credit budget exhausted ({Used}/{Limit})",
                source.Key, usage.CreditsThisMonth, monthly);
            return false;
        }

        return true;
    }
}

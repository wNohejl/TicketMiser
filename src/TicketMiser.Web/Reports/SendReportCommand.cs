namespace TicketMiser.Web.Reports;

/// <summary>
/// <c>dotnet run --project src/TicketMiser.Web -- send-report 2026-10</c>: the operator's
/// trigger for <see cref="ReportMailingService"/>. Program.cs runs it after the database is
/// migrated and before the host starts, so no scheduler, evaluator or web server runs with
/// it; it sends, prints the counts and exits. The step after publishing a report and
/// deploying it (the monthly-report skill: publish, deploy, then send-report).
/// </summary>
public static class SendReportCommand
{
    public const string Name = "send-report";

    /// <summary>What the command line asked for.</summary>
    /// <param name="Month">The month to send, when the arguments were a valid command.</param>
    /// <param name="Error">Why the arguments were refused, when they named the command badly.</param>
    public sealed record Parsed(string? Month, string? Error);

    /// <summary>
    /// Null when the arguments are not this command (the host starts as usual). Otherwise the
    /// month, or an error for a missing or malformed one. Arguments after the month are left to
    /// the configuration, so <c>--Reports:Path=...</c> still works.
    /// </summary>
    public static Parsed? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !string.Equals(args[0], Name, StringComparison.Ordinal))
            return null;

        if (args.Count < 2 || args[1].StartsWith('-'))
            return new Parsed(null, $"Usage: {Name} <yyyy-mm>, e.g. {Name} 2026-10");

        return ReportMarkdown.IsMonth(args[1])
            ? new Parsed(args[1], null)
            : new Parsed(null, $"'{args[1]}' is not a month. Usage: {Name} <yyyy-mm>, e.g. {Name} 2026-10");
    }

    /// <summary>Runs the send in its own scope, writes what happened, and returns the process exit code.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, Parsed command, TextWriter output, CancellationToken ct = default)
    {
        if (command.Month is not { } month)
        {
            await output.WriteLineAsync(command.Error);
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var mailing = scope.ServiceProvider.GetRequiredService<ReportMailingService>();
        var result = await mailing.SendAsync(month, ct);

        switch (result.Outcome)
        {
            case ReportSendOutcome.NotPublished:
                await output.WriteLineAsync(
                    $"No published report for {month}: nothing sent. Set `published: true` in docs/reports/{month}{ReportMarkdown.FileSuffix}, commit and deploy first.");
                return 1;

            case ReportSendOutcome.Locked:
                await output.WriteLineAsync($"Another process is sending a report now: nothing sent for {month}. Run it again when that one has finished.");
                return 1;

            default:
                await output.WriteLineAsync(
                    $"Report {month}: {result.Sent} sent, {result.Failed} failed, {result.AlreadySent} already sent before this run.");
                return result.Failed > 0 ? 1 : 0;
        }
    }
}

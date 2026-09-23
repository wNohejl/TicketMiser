using TicketMiser.Ingestion.Configuration;
using TicketMiser.Ingestion.Services;

namespace TicketMiser.Tests.Cadence;

/// <summary>
/// The on-sale record's schedule, as arithmetic. These are the marks the cadence-check skill
/// prints, and they are what a five-minute watch means.
/// </summary>
[Trait("Category", "Cadence")]
public class OnSaleWindowTests
{
    private static readonly OnSaleWatchOptions Options = new();
    private static readonly DateTimeOffset T = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_window_has_twenty_seven_dense_marks_and_twenty_four_hourly_ones()
    {
        var marks = OnSaleWindow.Marks(T, Options).ToList();

        // T-15, T-10, ..., T+120 is 28 marks at five minutes; then T+3h .. T+26h is 24.
        Assert.Equal(T - TimeSpan.FromMinutes(15), marks.First());
        Assert.Equal(T + TimeSpan.FromHours(2) + TimeSpan.FromHours(24), marks.Last());
        Assert.Equal(28 + 24, marks.Count);
        Assert.Equal(52, OnSaleWindow.CallsPerEvent(Options));
    }

    [Fact]
    public void Before_the_lead_nothing_is_due_and_after_the_tail_nothing_is_due()
    {
        Assert.False(OnSaleWindow.IsTickDue(T, null, T - TimeSpan.FromMinutes(16), Options));
        Assert.False(OnSaleWindow.IsTickDue(T, null, T + TimeSpan.FromHours(26) + TimeSpan.FromMinutes(1), Options));
    }

    [Fact]
    public void A_tick_is_due_once_per_mark_and_a_restart_resumes_on_the_next_mark()
    {
        // At T-15 exactly, the first mark is owed.
        Assert.True(OnSaleWindow.IsTickDue(T, null, T - TimeSpan.FromMinutes(15), Options));

        // Recorded at T-15; at T-14 the same mark is not owed again.
        var last = T - TimeSpan.FromMinutes(15);
        Assert.False(OnSaleWindow.IsTickDue(T, last, T - TimeSpan.FromMinutes(14), Options));

        // At T-10 the next mark is.
        Assert.True(OnSaleWindow.IsTickDue(T, last, T - TimeSpan.FromMinutes(10), Options));

        // A host that was down from T-10 to T+3 owes the T mark, not three back-dated ones.
        Assert.Equal(T, OnSaleWindow.CurrentMark(T, T + TimeSpan.FromMinutes(3), Options));
        Assert.True(OnSaleWindow.IsTickDue(T, last, T + TimeSpan.FromMinutes(3), Options));
    }

    [Fact]
    public void After_the_dense_tail_marks_are_hourly()
    {
        var denseEnd = T + TimeSpan.FromHours(2);

        Assert.Equal(denseEnd, OnSaleWindow.CurrentMark(T, denseEnd + TimeSpan.FromMinutes(30), Options));
        Assert.Equal(denseEnd + TimeSpan.FromHours(1), OnSaleWindow.CurrentMark(T, denseEnd + TimeSpan.FromMinutes(61), Options));
        Assert.False(OnSaleWindow.IsTickDue(T, denseEnd, denseEnd + TimeSpan.FromMinutes(59), Options));
        Assert.True(OnSaleWindow.IsTickDue(T, denseEnd, denseEnd + TimeSpan.FromMinutes(60), Options));
    }

    [Fact]
    public void Walking_a_whole_window_minute_by_minute_lands_exactly_the_planned_marks()
    {
        var recorded = new List<DateTimeOffset>();
        DateTimeOffset? last = null;

        for (var now = T - TimeSpan.FromMinutes(20); now <= T + TimeSpan.FromHours(27); now += TimeSpan.FromMinutes(1))
        {
            if (!OnSaleWindow.IsTickDue(T, last, now, Options))
                continue;

            var mark = OnSaleWindow.CurrentMark(T, now, Options)!.Value;
            recorded.Add(mark);
            last = mark;
        }

        Assert.Equal(OnSaleWindow.Marks(T, Options).ToList(), recorded);
    }
}

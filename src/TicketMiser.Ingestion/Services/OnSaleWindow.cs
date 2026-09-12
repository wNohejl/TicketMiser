using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Services;

/// <summary>
/// The on-sale record's schedule, as pure arithmetic over the announced on-sale time.
///
/// <para>
/// Ticks are marks on a clock anchored at T, not intervals since the last run, so a restart
/// inside the window resumes on the next mark and two hosts would agree on the marks. The
/// window is: every <see cref="OnSaleWatchOptions.Tick"/> from T minus <see cref="OnSaleWatchOptions.Lead"/>
/// to T plus <see cref="OnSaleWatchOptions.Tail"/>, then hourly until T plus Tail plus
/// <see cref="OnSaleWatchOptions.HourlyTail"/>.
/// </para>
/// </summary>
public static class OnSaleWindow
{
    /// <summary>Whether the event is inside its watch window at all.</summary>
    public static bool IsInWindow(DateTimeOffset onSaleAt, DateTimeOffset now, OnSaleWatchOptions o)
        => now >= onSaleAt - o.Lead && now <= onSaleAt + o.Tail + o.HourlyTail;

    /// <summary>The mark at or before <paramref name="now"/>, or null outside the window.</summary>
    public static DateTimeOffset? CurrentMark(DateTimeOffset onSaleAt, DateTimeOffset now, OnSaleWatchOptions o)
    {
        if (!IsInWindow(onSaleAt, now, o))
            return null;

        var denseEnd = onSaleAt + o.Tail;

        if (now <= denseEnd)
        {
            var since = now - (onSaleAt - o.Lead);
            var steps = (long)Math.Floor(since / o.Tick);
            return onSaleAt - o.Lead + o.Tick * steps;
        }

        var hourly = TimeSpan.FromHours(1);
        var hours = (long)Math.Floor((now - denseEnd) / hourly);
        return denseEnd + hourly * hours;
    }

    /// <summary>
    /// Whether a tick is owed: the current mark is one we have not yet recorded.
    /// <paramref name="lastTick"/> is the newest tick on record for the event.
    /// </summary>
    public static bool IsTickDue(DateTimeOffset onSaleAt, DateTimeOffset? lastTick, DateTimeOffset now, OnSaleWatchOptions o)
    {
        var mark = CurrentMark(onSaleAt, now, o);
        if (mark is null)
            return false;

        return lastTick is null || lastTick.Value < mark.Value;
    }

    /// <summary>Every mark in the window, for the plan on screen and for the cadence test.</summary>
    public static IEnumerable<DateTimeOffset> Marks(DateTimeOffset onSaleAt, OnSaleWatchOptions o)
    {
        for (var t = onSaleAt - o.Lead; t <= onSaleAt + o.Tail; t += o.Tick)
            yield return t;

        for (var t = onSaleAt + o.Tail + TimeSpan.FromHours(1); t <= onSaleAt + o.Tail + o.HourlyTail; t += TimeSpan.FromHours(1))
            yield return t;
    }

    /// <summary>Requests one event's window costs on one source: one call per mark.</summary>
    public static int CallsPerEvent(OnSaleWatchOptions o) => Marks(DateTimeOffset.UnixEpoch, o).Count();
}

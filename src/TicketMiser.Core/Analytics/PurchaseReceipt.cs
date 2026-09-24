using System.Globalization;
using System.Text;
using TicketMiser.Core.Entities;

namespace TicketMiser.Core.Analytics;

/// <summary>
/// The ledger as a dated receipt: one event's on-sale record and the purchases logged against it,
/// rendered as a Markdown file a fan can keep, print, or attach to a complaint.
///
/// <para>
/// A receipt of what was shown, and nothing more (legal guidelines, rule 9). Every tick is listed
/// with the page of the source that reported it; the derived facts are stated as what a named
/// source reported at a named minute; nothing here says a seller did anything wrong. Where to take
/// it is named, and whether to is the reader's.
/// </para>
///
/// <para>
/// Deterministic from its inputs — the record, the purchases and the instant it was generated —
/// so the same request an hour apart differs only in the dateline, and a test can pin every
/// section without a database ("exports are files", the lifecycle plan §4.7).
/// </para>
/// </summary>
public static class PurchaseReceipt
{
    /// <summary>The zone the receipt is dated in, besides UTC. Every room this product covers is in it.</summary>
    public const string NashvilleZone = "America/Chicago";

    public const string ContentType = "text/markdown; charset=utf-8";

    /// <summary>The Tennessee Attorney General's consumer complaint form, as the public record page links it.</summary>
    public const string TennesseeComplaintUrl = "https://www.tn.gov/attorneygeneral/working-for-tennessee/consumer/file-a-consumer-complaint.html";

    public const string FtcComplaintUrl = "https://reportfraud.ftc.gov/";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary><c>ticketmiser-receipt-{slug}-{yyyyMMdd}.md</c>, dated on the Nashville calendar.</summary>
    public static string FileName(Event evt, DateTimeOffset generatedAt)
    {
        var slug = evt.Slug is { Length: > 0 } s ? EventSlug.Fold(s) : EventSlug.Base(evt);
        var day = OnSaleCalendar.InZone(generatedAt, NashvilleZone).ToString("yyyyMMdd", Invariant);
        return $"ticketmiser-receipt-{slug}-{day}.md";
    }

    /// <summary>The receipt.</summary>
    /// <param name="record">The event's on-sale record, with every tick and the sources that wrote them.</param>
    /// <param name="purchases">The purchases logged against this event. Other events' rows are ignored.</param>
    /// <param name="generatedAt">When the file is being written; the receipt's date.</param>
    /// <param name="siteBase">The site's origin, for the public record link. Omitted when null.</param>
    public static string Write(OnSaleRecord record, IEnumerable<PurchaseLine> purchases, DateTimeOffset generatedAt, string? siteBase = null)
    {
        var evt = record.Event;
        var sources = record.Sources.ToDictionary(s => s.Id);
        var mine = purchases.Where(p => p.Purchase.EventId == evt.Id).OrderBy(p => p.Purchase.PurchasedAt).ToList();
        var ticks = record.Ticks.OrderBy(t => t.MinutesFromOnSale).ThenBy(t => t.ObservedAt).ToList();
        var zone = evt.Venue?.Timezone is { Length: > 0 } z ? z : NashvilleZone;

        var sb = new StringBuilder();

        sb.Append("# Receipt: ").Append(Inline(evt.Name)).Append('\n').Append('\n');
        sb.Append("Generated ").Append(Utc(generatedAt))
          .Append(" (").Append(InZone(generatedAt, NashvilleZone)).Append(", Nashville time).").Append('\n').Append('\n');
        sb.Append("A dated record of what the sources below reported about this event, at the minutes shown. ")
          .Append("It records what was shown and when; it does not say that anyone broke a law.").Append('\n').Append('\n');

        // ---- the event ------------------------------------------------------------------
        sb.Append("## The event").Append('\n').Append('\n');
        sb.Append("| | |").Append('\n').Append("|---|---|").Append('\n');
        Row(sb, "Event", Inline(evt.Name));
        if (evt.Performer is { } performer && performer.Name != evt.Name)
            Row(sb, "Performer", Inline(performer.Name));
        Row(sb, "Venue", evt.Venue is { } v ? Inline(string.Join(", ", new[] { v.Name, v.City, v.State }.Where(p => p.Length > 0))) : "not recorded");
        Row(sb, "Starts", $"{InZone(evt.StartsAt, zone)} ({Utc(evt.StartsAt)})");
        Row(sb, "Public on-sale", evt.OnSaleTbd || evt.OnSaleAt is null
            ? "not announced by the source"
            : $"{InZone(evt.OnSaleAt.Value, zone)} ({Utc(evt.OnSaleAt.Value)})");
        if (siteBase is { Length: > 0 } && evt.Slug is { Length: > 0 } slug)
            Row(sb, "Public record", $"<{siteBase.TrimEnd('/')}/e/{Uri.EscapeDataString(slug)}>");
        sb.Append('\n');

        // ---- purchases ------------------------------------------------------------------
        sb.Append("## Purchases logged").Append('\n').Append('\n');
        if (mine.Count == 0)
        {
            sb.Append("No purchase is logged against this event.").Append('\n').Append('\n');
        }
        else
        {
            sb.Append("As entered by the buyer. *All-in* means the price per ticket included fees; *face value* means it did not.").Append('\n').Append('\n');
            sb.Append("| Bought (Nashville time) | Where | Tickets | Per ticket | Fees | Total | Note |").Append('\n');
            sb.Append("|---|---|--:|--:|---|--:|---|").Append('\n');
            foreach (var line in mine)
            {
                var p = line.Purchase;
                // SourceLink.For, never Tagged, here and in the tick table: a receipt is evidence,
                // not a sale, and a citation carries no affiliate hop.
                var where = line.Source is { } s ? $"{Inline(s.Name)} <{SourceLink.For(s, evt)}>" : PurchaseLedger.Elsewhere;
                sb.Append("| ").Append(InZone(p.PurchasedAt, NashvilleZone))
                  .Append(" | ").Append(where)
                  .Append(" | ").Append(p.Quantity.ToString(Invariant))
                  .Append(" | ").Append(PurchaseLedger.Amount(p.PaidPerTicket, p.Currency))
                  .Append(" | ").Append(PurchaseLedger.BasisWord(p.AllIn))
                  .Append(" | ").Append(PurchaseLedger.Amount(line.Total, p.Currency))
                  .Append(" | ").Append(p.Note is { Length: > 0 } note ? Inline(note) : "")
                  .Append(" |").Append('\n');
            }
            sb.Append('\n');
        }

        // ---- ticks ----------------------------------------------------------------------
        sb.Append("## The on-sale record").Append('\n').Append('\n');
        if (ticks.Count == 0)
        {
            sb.Append("No on-sale ticks were recorded for this event. A record is kept only for an event that was watched through its on-sale window.")
              .Append('\n').Append('\n');
        }
        else
        {
            sb.Append("Every tick recorded, in order. The minute is counted from the announced public on-sale time (T). ")
              .Append("Each row links to the page of the source that reported it.").Append('\n').Append('\n');
            sb.Append("| Observed (UTC) | Minute | Source | Market | Primary status | Resale status | Lowest | Highest | Listings | Fees | Source page |").Append('\n');
            sb.Append("|---|--:|---|---|---|---|--:|--:|--:|---|---|").Append('\n');
            foreach (var t in ticks)
            {
                var source = sources.GetValueOrDefault(t.SourceId);
                sb.Append("| ").Append(t.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Invariant))
                  .Append(" | ").Append(Minute(t.MinutesFromOnSale))
                  .Append(" | ").Append(source is null ? $"source {t.SourceId.ToString(Invariant)}" : Inline(source.Name))
                  .Append(" | ").Append(source is null ? "" : PurchaseLedger.MarketWord(source.Kind))
                  .Append(" | ").Append(Inline(t.PrimaryStatus ?? ""))
                  .Append(" | ").Append(Inline(t.ResaleStatus ?? ""))
                  .Append(" | ").Append(t.Lowest is { } lo ? PurchaseLedger.Amount(lo, t.Currency) : "")
                  .Append(" | ").Append(t.Highest is { } hi ? PurchaseLedger.Amount(hi, t.Currency) : "")
                  .Append(" | ").Append(t.ListingCount?.ToString(Invariant) ?? "")
                  .Append(" | ").Append(t.AllIn switch { true => "all-in", false => "face value", null => "not stated" })
                  .Append(" | ").Append(source is null ? "" : $"<{SourceLink.For(source, evt)}>")
                  .Append(" |").Append('\n');
            }
            sb.Append('\n');
        }

        // ---- what the sources reported --------------------------------------------------
        sb.Append("## What the sources reported").Append('\n').Append('\n');
        foreach (var fact in Facts(record, ticks, sources))
            sb.Append("- ").Append(fact).Append('\n');
        sb.Append('\n');

        // ---- how, and where -------------------------------------------------------------
        sb.Append("## How this was collected").Append('\n').Append('\n');
        sb.Append("Every number in this receipt came from an official API — ").Append(ApiOwners(record))
          .Append(" — read at the minute shown against the announced on-sale time. Nothing was scraped from a web page. ")
          .Append("Each tick links to the source that published it, so any figure can be checked against the site it came from. ")
          .Append("Only the summary a source gave — a status, a lowest and highest price, a listing count, and whether fees were included — was kept; ")
          .Append("the response it arrived in was discarded once it was read, and no listing, seat or seller was recorded. ")
          .Append("The purchases above were entered by the buyer and are not from any source.").Append('\n').Append('\n');
        sb.Append("A price marked *all-in* included fees when the source showed it; *face value* did not. ")
          .Append("The two are never compared to each other here, and neither market is ranked against the other.").Append('\n').Append('\n');

        sb.Append("## Where to take this").Append('\n').Append('\n');
        sb.Append("This receipt is a dated record of what each source showed. It draws no conclusion about anyone's conduct. ")
          .Append("If what it shows is something you want to raise, these offices take complaints about ticket sales:").Append('\n').Append('\n');
        sb.Append("- Tennessee Attorney General, Division of Consumer Affairs — consumer complaint form: <").Append(TennesseeComplaintUrl).Append('>').Append('\n');
        sb.Append("- Federal Trade Commission — <").Append(FtcComplaintUrl).Append('>').Append('\n').Append('\n');
        sb.Append("Either form asks for what you saw and when. Whether to file, and what to say, is yours to decide.").Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// The derived facts, each as a sentence naming the source and the minute. "Reported" every
    /// time: the record knows what a source said, not what was true behind it.
    /// </summary>
    public static IReadOnlyList<string> Facts(OnSaleRecord record, IReadOnlyList<OnSaleTick> ticks, IReadOnlyDictionary<int, Source> sources)
    {
        if (ticks.Count == 0)
            return ["Nothing: no tick was recorded."];

        var facts = new List<string>();
        var f = record.Facts;
        var primary = ticks.Where(t => sources.GetValueOrDefault(t.SourceId)?.Kind == SourceKind.Primary).ToList();

        string Name(int sourceId) => sources.GetValueOrDefault(sourceId)?.Name is { } n ? Inline(n) : $"source {sourceId.ToString(Invariant)}";

        if (f.OnSalePrice is { } price)
        {
            facts.Add($"At {Minute(price.Minute)}, {Name(price.SourceId)} reported a lowest primary price of " +
                      $"{PurchaseLedger.Amount(price.Lowest, price.Currency)} " +
                      $"({price.AllIn switch { true => "all-in", false => "face value", null => "fees not stated" }}).");
        }
        else
        {
            facts.Add("No primary source reported a price at or after T in the ticks recorded.");
        }

        if (f.FewLeftMinute is { } few && primary.FirstOrDefault(t => t.MinutesFromOnSale == few && t.PrimaryStatus == InventoryStatus.FewLeft) is { } fewTick)
            facts.Add($"{Name(fewTick.SourceId)} first reported {InventoryStatus.FewLeft} at {Minute(few)} ({Utc(fewTick.ObservedAt)}).");

        if (f.SoldOutMinute is { } sold && primary.FirstOrDefault(t => t.MinutesFromOnSale == sold && t.PrimaryStatus == InventoryStatus.NotAvailable) is { } soldTick)
        {
            facts.Add($"{Name(soldTick.SourceId)} first reported {InventoryStatus.NotAvailable} at {Minute(sold)} ({Utc(soldTick.ObservedAt)}).");

            if (f.ResaleListingsAtSellout is { } listings)
                facts.Add($"At that minute, the resale sources recorded had reported {listings.ToString("N0", Invariant)} listings between them.");

            if (f.ReappearedMinute is { } back
                && primary.FirstOrDefault(t => t.MinutesFromOnSale == back && t.PrimaryStatus is InventoryStatus.Available or InventoryStatus.FewLeft) is { } backTick)
            {
                facts.Add($"After that, {Name(backTick.SourceId)} reported {backTick.PrimaryStatus} at {Minute(back)} ({Utc(backTick.ObservedAt)}).");
            }
            else
            {
                facts.Add("In the ticks recorded after that, no primary source reported tickets available again.");
            }
        }
        else
        {
            facts.Add($"In the ticks recorded, no primary source reported {InventoryStatus.NotAvailable}.");
        }

        var first = ticks[0];
        var last = ticks[^1];
        facts.Add($"{ticks.Count.ToString(Invariant)} ticks were recorded, from {Minute(first.MinutesFromOnSale)} to {Minute(last.MinutesFromOnSale)}.");

        return facts;
    }

    /// <summary>
    /// Text as Markdown shows it and no more: backslash-escaped so a name cannot open a link,
    /// start emphasis, or — the one that matters in a table — close a cell with a pipe. Line
    /// breaks collapse to spaces, because a row is one line.
    /// </summary>
    public static string Inline(string text)
    {
        var sb = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // An underscore between two letters or digits cannot open or close emphasis, so a
            // status code such as TICKETS_AVAILABLE reads as the source spelled it.
            if (c == '_' && i > 0 && i < text.Length - 1 && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]))
            {
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\\' or '|' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#':
                    sb.Append('\\').Append(c);
                    break;
                case '\r':
                    break;
                case '\n' or '\t':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>A minute against the announced on-sale: "T", "T-15 min", "T+10 min". ASCII, because the file travels.</summary>
    public static string Minute(int minute) => minute switch
    {
        0 => "T",
        < 0 => $"T-{(-minute).ToString(Invariant)} min",
        _ => $"T+{minute.ToString(Invariant)} min"
    };

    private static void Row(StringBuilder sb, string label, string value)
        => sb.Append("| ").Append(label).Append(" | ").Append(value).Append(" |").Append('\n');

    private static string Utc(DateTimeOffset instant)
        => instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Invariant) + " UTC";

    private static string InZone(DateTimeOffset instant, string zone)
        => $"{OnSaleCalendar.InZone(instant, zone).ToString("ddd d MMM yyyy HH:mm", Invariant)} {zone}";

    private static string ApiOwners(OnSaleRecord record)
    {
        var owners = record.Sources.Select(s => $"{Inline(s.Name)}'s").Distinct().ToList();
        return owners.Count == 0 ? "the sources that report on this event" : string.Join(" and ", owners);
    }
}

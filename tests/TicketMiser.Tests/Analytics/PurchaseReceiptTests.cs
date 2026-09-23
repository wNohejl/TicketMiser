using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;

namespace TicketMiser.Tests.Analytics;

/// <summary>
/// The receipt a fan keeps. Pinned by section rather than byte for byte, so wording can improve
/// without a golden file churning: it is dated in UTC and Nashville time, it lists every tick with
/// its source's page, it states the derived facts as what a named source reported, it says how
/// the numbers were collected, it names where a complaint goes — and it never claims a violation
/// (legal guidelines, rule 9). A pipe in a name must not break a table.
/// </summary>
public class PurchaseReceiptTests
{
    private static readonly DateTimeOffset OnSale = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero); // 10:00 in Nashville
    private static readonly DateTimeOffset Generated = new(2026, 9, 22, 3, 30, 0, TimeSpan.Zero); // 21 Sep, 22:30 in Nashville

    private static readonly Source Ticketmaster = new() { Id = 1, Key = "ticketmaster", Name = "Ticketmaster", Kind = SourceKind.Primary };
    private static readonly Source SeatGeek = new() { Id = 2, Key = "seatgeek", Name = "SeatGeek", Kind = SourceKind.Resale };

    private static Event Evt(string name = "Example Tour") => new()
    {
        Id = 7,
        Name = name,
        Slug = "example-performer-ryman-auditorium-2026-11-20",
        StartsAt = new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero),
        OnSaleAt = OnSale,
        Performer = new Performer { Name = "Example Performer" },
        Venue = new Venue { Name = "Ryman Auditorium", City = "Nashville", State = "TN", Timezone = "America/Chicago" },
        ExternalIds = new Dictionary<string, string> { ["ticketmaster"] = "tm-123", ["seatgeek"] = "6543210" }
    };

    private static OnSaleTick Primary(int minute, string status, decimal? lowest = null, bool? allIn = null) => new()
    {
        EventId = 7,
        SourceId = Ticketmaster.Id,
        MinutesFromOnSale = minute,
        ObservedAt = OnSale.AddMinutes(minute),
        PrimaryStatus = status,
        Lowest = lowest,
        Highest = lowest is null ? null : lowest + 100m,
        AllIn = allIn
    };

    private static OnSaleTick Resale(int minute, int listings, decimal lowest) => new()
    {
        EventId = 7,
        SourceId = SeatGeek.Id,
        MinutesFromOnSale = minute,
        ObservedAt = OnSale.AddMinutes(minute).AddSeconds(20),
        ListingCount = listings,
        Lowest = lowest,
        AllIn = false
    };

    private static OnSaleRecord Record(Event evt, params OnSaleTick[] ticks)
    {
        Source[] sources = [Ticketmaster, SeatGeek];
        return new OnSaleRecord(evt, sources, ticks, OnSaleRecord.Derive(ticks, sources));
    }

    private static OnSaleRecord SoldOutAndBack(Event evt) => Record(evt,
        Primary(-5, InventoryStatus.Available),
        Primary(0, InventoryStatus.Available, 59.5m, allIn: false),
        Resale(5, 120, 140m),
        Primary(10, InventoryStatus.FewLeft, 59.5m, allIn: false),
        Primary(15, InventoryStatus.NotAvailable),
        Resale(15, 410, 210m),
        Primary(50, InventoryStatus.Available, 59.5m, allIn: false));

    private static PurchaseLine Bought(Event evt, string? note = null) => new(
        new Purchase { Id = 1, EventId = evt.Id, SourceId = SeatGeek.Id, Quantity = 2, PaidPerTicket = 212.4m, AllIn = true, PurchasedAt = OnSale.AddMinutes(20), Note = note },
        evt, SeatGeek, null, null, PurchaseGrade.NotYetPlayed, "The event has not been played yet.");

    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf($"## {heading}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing section {heading}");
        var next = markdown.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    [Fact]
    public void The_receipt_is_titled_and_dated_in_UTC_and_Nashville_time()
    {
        var evt = Evt();
        var md = PurchaseReceipt.Write(SoldOutAndBack(evt), [], Generated, "https://ticketmiser.example");

        Assert.StartsWith("# Receipt: Example Tour\n", md);
        Assert.Contains("Generated 2026-09-22 03:30:00 UTC (Mon 21 Sep 2026 22:30 America/Chicago, Nashville time).", md);
    }

    [Fact]
    public void The_event_section_names_the_room_the_start_the_on_sale_and_the_public_record()
    {
        var md = PurchaseReceipt.Write(SoldOutAndBack(Evt()), [], Generated, "https://ticketmiser.example/");
        var section = Section(md, "The event");

        Assert.Contains("| Venue | Ryman Auditorium, Nashville, TN |", section);
        Assert.Contains("| Performer | Example Performer |", section);
        Assert.Contains("Fri 20 Nov 2026 20:00 America/Chicago", section);
        Assert.Contains("| Public on-sale | Fri 18 Sep 2026 10:00 America/Chicago (2026-09-18 15:00:00 UTC) |", section);
        Assert.Contains("<https://ticketmiser.example/e/example-performer-ryman-auditorium-2026-11-20>", section);
    }

    [Fact]
    public void Every_tick_is_a_row_with_its_minute_status_prices_listings_fee_basis_and_source_page()
    {
        var md = PurchaseReceipt.Write(SoldOutAndBack(Evt()), [], Generated);
        var section = Section(md, "The on-sale record");
        var rows = section.Split('\n').Where(l => l.StartsWith("| 2026-", StringComparison.Ordinal)).ToList();

        Assert.Equal(7, rows.Count);

        Assert.Equal(
            "| 2026-09-18 15:00:00 | T | Ticketmaster | primary | TICKETS_AVAILABLE |  | $59.50 | $159.50 |  | face value | <https://www.ticketmaster.com/event/tm-123> |",
            rows[1]);
        Assert.Contains("| T-5 min |", rows[0]);
        Assert.Contains("| not stated |", rows[0]);
        Assert.Contains("| T+15 min | SeatGeek | resale |", rows[5]);
        Assert.Contains("| 410 |", rows[5]);
        Assert.EndsWith("<https://seatgeek.com/e/events/6543210> |", rows[5]);
    }

    [Fact]
    public void The_derived_facts_are_stated_as_what_a_named_source_reported()
    {
        var md = PurchaseReceipt.Write(SoldOutAndBack(Evt()), [], Generated);
        var facts = Section(md, "What the sources reported");

        Assert.Contains("- At T, Ticketmaster reported a lowest primary price of $59.50 (face value).", facts);
        Assert.Contains("- Ticketmaster first reported FEW_TICKETS_LEFT at T+10 min (2026-09-18 15:10:00 UTC).", facts);
        Assert.Contains("- Ticketmaster first reported TICKETS_NOT_AVAILABLE at T+15 min (2026-09-18 15:15:00 UTC).", facts);
        Assert.Contains("- At that minute, the resale sources recorded had reported 410 listings between them.", facts);
        Assert.Contains("- After that, Ticketmaster reported TICKETS_AVAILABLE at T+50 min (2026-09-18 15:50:00 UTC).", facts);
        Assert.Contains("- 7 ticks were recorded, from T-5 min to T+50 min.", facts);
    }

    [Fact]
    public void A_record_that_never_sold_out_says_so_without_inventing_a_sellout()
    {
        var md = PurchaseReceipt.Write(Record(Evt(), Primary(0, InventoryStatus.Available, 59.5m, allIn: true)), [], Generated);
        var facts = Section(md, "What the sources reported");

        Assert.Contains("In the ticks recorded, no primary source reported TICKETS_NOT_AVAILABLE.", facts);
        Assert.DoesNotContain("After that", facts);
    }

    [Fact]
    public void An_event_with_no_ticks_and_no_purchases_says_both_plainly()
    {
        var md = PurchaseReceipt.Write(Record(Evt()), [], Generated);

        Assert.Contains("No purchase is logged against this event.", Section(md, "Purchases logged"));
        Assert.Contains("No on-sale ticks were recorded for this event.", Section(md, "The on-sale record"));
        Assert.Contains("Nothing: no tick was recorded.", Section(md, "What the sources reported"));
    }

    [Fact]
    public void Purchases_are_listed_with_their_fee_basis_and_the_source_page()
    {
        var evt = Evt();
        var md = PurchaseReceipt.Write(SoldOutAndBack(evt), [Bought(evt, "Sec 4, row B")], Generated);
        var section = Section(md, "Purchases logged");

        Assert.Contains(
            "| Fri 18 Sep 2026 10:20 America/Chicago | SeatGeek <https://seatgeek.com/e/events/6543210> | 2 | $212.40 | all-in | $424.80 | Sec 4, row B |",
            section);
    }

    [Fact]
    public void A_pipe_in_a_name_or_note_is_escaped_so_it_cannot_close_a_table_cell()
    {
        var evt = Evt("Pipes | Wires [Live]");
        evt.Venue!.Name = "The Basement | East";
        var md = PurchaseReceipt.Write(SoldOutAndBack(evt), [Bought(evt, "row A | seat 3\nsecond line")], Generated);

        Assert.StartsWith(@"# Receipt: Pipes \| Wires \[Live\]", md);
        Assert.Contains(@"| Venue | The Basement \| East, Nashville, TN |", md);
        Assert.Contains(@"| row A \| seat 3 second line |", md);

        // Every purchase row still has exactly the table's eight unescaped cell edges.
        var row = Section(md, "Purchases logged").Split('\n').Single(l => l.StartsWith("| Fri", StringComparison.Ordinal));
        var edges = row.Where((c, i) => c == '|' && (i == 0 || row[i - 1] != '\\')).Count();
        Assert.Equal(8, edges);
    }

    [Fact]
    public void Emphasis_and_links_are_escaped_but_a_status_code_keeps_its_underscores()
    {
        Assert.Equal(@"\_live\_ \*tour\* \`x\` TICKETS_AVAILABLE a\\b", PurchaseReceipt.Inline("_live_ *tour* `x` TICKETS_AVAILABLE a\\b"));
    }

    [Fact]
    public void How_it_was_collected_matches_the_public_page_and_where_to_take_it_names_both_offices_without_a_claim()
    {
        var md = PurchaseReceipt.Write(SoldOutAndBack(Evt()), [], Generated);

        var how = Section(md, "How this was collected");
        Assert.Contains("came from an official API — Ticketmaster's and SeatGeek's —", how);
        Assert.Contains("Nothing was scraped from a web page.", how);
        Assert.Contains("the response it arrived in was discarded once it was read", how);

        var where = Section(md, "Where to take this");
        Assert.Contains("Tennessee Attorney General", where);
        Assert.Contains("<https://www.tn.gov/attorneygeneral/", where);
        Assert.Contains("<https://reportfraud.ftc.gov/>", where);
        Assert.Contains("draws no conclusion about anyone's conduct", where);

        // Rule 9: a receipt, never an accusation.
        foreach (var claim in new[] { "violat", "illegal", "unlawful", "fraudulent", "broke the law" })
            Assert.DoesNotContain(claim, md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_file_is_named_for_the_event_and_the_Nashville_date_it_was_made()
    {
        Assert.Equal(
            "ticketmiser-receipt-example-performer-ryman-auditorium-2026-11-20-20260921.md",
            PurchaseReceipt.FileName(Evt(), Generated));
    }
}

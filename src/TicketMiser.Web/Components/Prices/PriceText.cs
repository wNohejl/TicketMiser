using System.Globalization;
using TicketMiser.Core.Analytics;
using TicketMiser.Core.Entities;
using TicketMiser.Desk.Primitives;
using TicketMiser.Web.Services;

namespace TicketMiser.Web.Components.Prices;

/// <summary>
/// The words the price windows share: an amount, its fee basis, a status, an instant in the
/// venue's zone. One copy, so "face value" is spelt the same in every window that says it.
/// </summary>
public static class PriceText
{
    public static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-US");

    public static string Amount(decimal amount, string currency)
        => currency == "USD" ? amount.ToString("C2", Money) : $"{amount.ToString("N2", Money)} {currency}";

    /// <summary>A signed difference: "+$4.00", "−$12.50", "no change".</summary>
    public static string Change(decimal delta, string currency)
        => delta == 0 ? "no change" : (delta > 0 ? "+" : "−") + Amount(Math.Abs(delta), currency);

    public static string AllInWord(bool? allIn) => allIn switch
    {
        true => "all-in",
        false => "face value",
        null => "fees unstated"
    };

    public static DeskState AllInState(bool? allIn) => allIn == true ? DeskState.Info : DeskState.Neutral;

    public static string AllInTitle(bool? allIn) => allIn switch
    {
        true => "Fees included",
        false => "Fees not included",
        null => "The source did not say whether fees are included"
    };

    public static string MarketWord(SourceKind kind) => kind == SourceKind.Primary ? "Primary" : "Resale";

    public static DeskState MarketState(SourceKind kind) => kind == SourceKind.Primary ? DeskState.Info : DeskState.Neutral;

    public static string StatusWord(string? code) => code switch
    {
        InventoryStatus.Available => "Available",
        InventoryStatus.FewLeft => "Few left",
        InventoryStatus.NotAvailable => "Not available",
        InventoryStatus.Unknown => "Unknown",
        null or "" => "—",
        _ => code
    };

    public static DeskState StatusState(string? code) => code switch
    {
        InventoryStatus.Available => DeskState.Positive,
        InventoryStatus.FewLeft => DeskState.Warning,
        InventoryStatus.NotAvailable => DeskState.Negative,
        _ => DeskState.Neutral
    };

    public static string OnSaleWord(OnSaleState state) => state switch
    {
        OnSaleState.Unannounced => "TBA",
        OnSaleState.Upcoming => "Upcoming",
        OnSaleState.OnSaleNow => "On sale now",
        _ => "Opened"
    };

    public static DeskState OnSaleTagState(OnSaleState state) => state == OnSaleState.OnSaleNow ? DeskState.Positive : DeskState.Neutral;

    /// <summary>The instant in the venue's zone, terse: "Fri 18 Sep 10:00".</summary>
    public static string When(DateTimeOffset instant, string? zone)
        => InZone(instant, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The calendar day in the venue's zone: "18 Sep".</summary>
    public static string Day(DateTimeOffset instant, string? zone)
        => InZone(instant, zone).ToString("d MMM", CultureInfo.InvariantCulture);

    public static string Day(DateOnly day) => day.ToString("d MMM", CultureInfo.InvariantCulture);

    public static DateTimeOffset InZone(DateTimeOffset instant, string? zone)
        => zone is { Length: > 0 } ? OnSaleCalendar.InZone(instant, zone) : instant.ToUniversalTime();

    /// <summary>How long ago, at the scale it is worth: 12m, 3.2h, 2.1d.</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var minutes = (now - at).TotalMinutes;

        return minutes switch
        {
            < 1 => "just now",
            < 60 => $"{minutes.ToString("F0", CultureInfo.InvariantCulture)}m ago",
            < 1440 => $"{(minutes / 60).ToString("F1", CultureInfo.InvariantCulture)}h ago",
            _ => $"{(minutes / 1440).ToString("F1", CultureInfo.InvariantCulture)}d ago"
        };
    }

    public static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    /// <summary>Whether SeatGeek's numbers are among these sources, and so whether its name must be on the page.</summary>
    public static bool HasSeatGeek(IEnumerable<Source> sources) => sources.Any(s => s.Key == "seatgeek");

    public const string SeatGeekAttribution = "Resale listings and prices from SeatGeek.";
}

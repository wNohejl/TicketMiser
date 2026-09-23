using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketMiser.Core.Contracts;
using TicketMiser.Ingestion.Configuration;

namespace TicketMiser.Ingestion.Adapters;

/// <summary>
/// Ticketmaster Inventory Status API: near real-time availability for up to about 350 event
/// ids per call, on its own key. The cheap way to watch a sale sell through: one call covers
/// the whole watchlist, and it spends no Discovery quota.
/// </summary>
public class TicketmasterInventoryAdapter(
    HttpClient http,
    IOptions<IngestionOptions> options) : IAvailabilitySource
{
    public const string SourceKey = "ticketmaster-inventory";

    /// <summary>The documented request ceiling is about 350 ids and 7 KB; 300 leaves room.</summary>
    public const int IdsPerCall = 300;

    private readonly IngestionOptions _options = options.Value;

    public string Key => SourceKey;

    public async Task<AvailabilityResult> FetchAvailabilityAsync(IReadOnlyList<ExternalEventRef> events, CancellationToken ct)
    {
        var key = _options.TicketmasterInventory.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"{SourceKey}: no API key configured.");

        var statuses = new List<CanonicalAvailability>();
        var requests = 0;

        foreach (var chunk in events.Chunk(IdsPerCall))
        {
            ct.ThrowIfCancellationRequested();

            var ids = string.Join(',', chunk.Select(e => e.SourceEventId));
            using var response = await http.GetAsync(
                $"availability?apikey={Uri.EscapeDataString(key)}&events={Uri.EscapeDataString(ids)}", ct);
            requests++;
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            statuses.AddRange(Parse(doc.RootElement));
        }

        return new AvailabilityResult(statuses, new FetchCost(requests));
    }

    /// <summary>Reads the response: an array of { eventId, status, resaleStatus }, or an object wrapping one.</summary>
    public static IReadOnlyList<CanonicalAvailability> Parse(JsonElement root)
    {
        var list = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("events", out var events) ? events : default;

        var result = new List<CanonicalAvailability>();

        if (list.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in list.EnumerateArray())
        {
            var id = Str(item, "eventId") ?? Str(item, "id");
            if (id is null)
                continue;

            result.Add(new CanonicalAvailability(id, Str(item, "status"), Str(item, "resaleStatus")));
        }

        return result;
    }

    private static string? Str(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

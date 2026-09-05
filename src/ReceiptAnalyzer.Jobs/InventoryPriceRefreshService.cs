using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Jobs;

public sealed class InventoryPriceRefreshService
{
    private readonly InventoryStore _inventory;
    private readonly PurchaseHistoryStore _history;
    private readonly TrolleyPriceClient _trolley;
    private readonly IProductCandidateMatcher _matcher;
    private readonly InventoryRefreshOptions _options;
    private readonly ILogger<InventoryPriceRefreshService> _logger;
    private readonly SemaphoreSlim _batchGate = new(1, 1);

    public InventoryPriceRefreshService(
        InventoryStore inventory, PurchaseHistoryStore history, TrolleyPriceClient trolley,
        IProductCandidateMatcher matcher, InventoryRefreshOptions options,
        ILogger<InventoryPriceRefreshService> logger)
    {
        _inventory = inventory;
        _history = history;
        _trolley = trolley;
        _matcher = matcher;
        _options = options;
        _logger = logger;
    }

    public InventoryRefreshState Status() => _inventory.Load().Refresh;

    public InventoryRefreshState Start(DateOnly today, DateTimeOffset now)
    {
        _inventory.Synchronise(_history.Load());
        return _inventory.Update(data =>
        {
            if (data.Refresh.Status == "active") return data.Refresh;
            var due = data.Products.Where(p => IsDue(p, today)).Select(p => p.Key).ToList();
            data.Refresh = new InventoryRefreshState
            {
                Id = Guid.NewGuid().ToString("N"),
                Status = due.Count == 0 ? "completed" : "active",
                StartedAt = now.ToString("O"),
                NextBatchAt = due.Count == 0 ? null : now.ToString("O"),
                CompletedAt = due.Count == 0 ? now.ToString("O") : null,
                Total = due.Count,
                PendingKeys = due
            };
            return data.Refresh;
        });
    }

    public InventoryRefreshState Cancel(DateTimeOffset now) => _inventory.Update(data =>
    {
        if (data.Refresh.Status == "active")
        {
            data.Refresh.Status = "cancelled";
            data.Refresh.CompletedAt = now.ToString("O");
            data.Refresh.NextBatchAt = null;
        }
        return data.Refresh;
    });

    public async Task<bool> ProcessDueBatchAsync(DateOnly today, DateTimeOffset now, CancellationToken ct)
    {
        if (!await _batchGate.WaitAsync(0, ct)) return false;
        try
        {
            var snapshot = _inventory.Load();
            if (snapshot.Refresh.Status != "active" || snapshot.Refresh.PendingKeys.Count == 0 ||
                (DateTimeOffset.TryParse(snapshot.Refresh.NextBatchAt, out var next) && next > now)) return false;

            var pending = snapshot.Refresh.PendingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceKnown = snapshot.Products.Where(p => pending.Contains(p.Key) && p.SourceUrl is not null)
                .Take(_options.KnownBatchSize).ToList();
            var unresolved = snapshot.Products.Where(p => pending.Contains(p.Key) && p.SourceUrl is null)
                .Take(_options.AgentBatchSize).ToList();

            var completed = new Dictionary<string, InventoryProduct>(StringComparer.OrdinalIgnoreCase);
            var directRequests = 0;
            foreach (var product in sourceKnown)
            {
                completed[product.Key] = await RefreshKnownAsync(product, today, ct);
                directRequests++;
            }

            var ambiguous = new List<ProductMatchRequest>();
            foreach (var product in unresolved)
            {
                try
                {
                    var searchName = product.Pack is null ? product.Name : $"{product.Name} {product.Pack}";
                    var candidates = await _trolley.SearchAsync(searchName, ct);
                    directRequests++;
                    var exact = candidates.FirstOrDefault(c =>
                        KeyNormaliser.PriceKey(c.Name).Equals(product.Key, StringComparison.OrdinalIgnoreCase));
                    if (exact is not null)
                    {
                        completed[product.Key] = await RefreshKnownAsync(
                            product with { SourceUrl = exact.Url, MatchStatus = InventoryMatchStatus.Matched }, today, ct);
                        directRequests++;
                    }
                    else if (candidates.Count > 0)
                    {
                        ambiguous.Add(new ProductMatchRequest(product, candidates));
                    }
                    else
                    {
                        completed[product.Key] = product with
                        {
                            MatchStatus = InventoryMatchStatus.NotComparable,
                            LastMatchAttemptOn = today.ToString("yyyy-MM-dd"),
                            Error = "No exact current product match was found."
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    completed[product.Key] = Failed(product, ex.Message);
                }
            }

            var agentCalls = 0;
            if (ambiguous.Count > 0)
            {
                try
                {
                    var matches = await _matcher.MatchAsync(ambiguous, ct);
                    agentCalls = 1;
                    foreach (var request in ambiguous)
                    {
                        if (matches.TryGetValue(request.Product.Key, out var url))
                        {
                            completed[request.Product.Key] = await RefreshKnownAsync(
                                request.Product with { SourceUrl = url, MatchStatus = InventoryMatchStatus.Matched }, today, ct);
                            directRequests++;
                        }
                        else
                        {
                            completed[request.Product.Key] = request.Product with
                            {
                                MatchStatus = InventoryMatchStatus.NotComparable,
                                LastMatchAttemptOn = today.ToString("yyyy-MM-dd"),
                                Error = "Candidate products were not an exact product-and-pack match."
                            };
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    agentCalls = 1;
                    foreach (var request in ambiguous) completed[request.Product.Key] = Failed(request.Product, ex.Message);
                }
            }

            // One atomic store update is the publication boundary for this batch.
            _inventory.Update(data =>
            {
                foreach (var pair in completed)
                {
                    var index = data.Products.FindIndex(p => p.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) data.Products[index] = pair.Value;
                    data.Refresh.PendingKeys.RemoveAll(k => k.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                }

                data.Refresh.Processed += completed.Count;
                data.Refresh.Updated += completed.Values.Count(p => p.CurrentOffers.Count > 0 && p.MatchStatus == InventoryMatchStatus.Matched);
                data.Refresh.Unmatched += completed.Values.Count(p => p.MatchStatus == InventoryMatchStatus.NotComparable);
                data.Refresh.Failed += completed.Values.Count(p => p.MatchStatus == InventoryMatchStatus.Error);
                data.Refresh.DirectRequests += directRequests;
                data.Refresh.AgentCalls += agentCalls;
                data.Refresh.PublishedBatch++;
                data.Refresh.LastPublishedAt = now.ToString("O");
                if (data.Refresh.PendingKeys.Count == 0)
                {
                    data.Refresh.Status = "completed";
                    data.Refresh.CompletedAt = now.ToString("O");
                    data.Refresh.NextBatchAt = null;
                }
                else
                {
                    data.Refresh.NextBatchAt = now.Add(_options.BatchDelay).ToString("O");
                }
                return true;
            });

            _logger.LogInformation(
                "Published inventory price batch: {Products} products, {DirectRequests} direct requests, {AgentCalls} agent calls.",
                completed.Count, directRequests, agentCalls);
            return true;
        }
        finally
        {
            _batchGate.Release();
        }
    }

    private async Task<InventoryProduct> RefreshKnownAsync(InventoryProduct product, DateOnly today, CancellationToken ct)
    {
        try
        {
            var offers = await _trolley.GetOffersAsync(product.SourceUrl!, today, ct);
            return product with
            {
                MatchStatus = offers.Count == 0 ? InventoryMatchStatus.Unavailable : InventoryMatchStatus.Matched,
                Offers = offers,
                LastCheckedOn = today.ToString("yyyy-MM-dd"),
                LastMatchAttemptOn = today.ToString("yyyy-MM-dd"),
                Error = offers.Count == 0 ? "The source currently lists no supported-store offer." : null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(product, ex.Message);
        }
    }

    private static InventoryProduct Failed(InventoryProduct product, string error) => product with
    {
        MatchStatus = InventoryMatchStatus.Error,
        Error = error.Length <= 300 ? error : error[..300]
        // Preserve the last published offers and dates after a transient failure.
    };

    private bool IsDue(InventoryProduct product, DateOnly today)
    {
        if (product.SourceUrl is null)
        {
            return !DateOnly.TryParse(product.LastMatchAttemptOn, out var attempted) ||
                   attempted <= today.AddDays(-_options.UnmatchedRetryDays);
        }
        if (!DateOnly.TryParse(product.LastCheckedOn, out var checkedOn)) return true;
        var recent = DateOnly.TryParse(product.LastPurchasedOn, out var purchased) &&
                     purchased >= today.AddDays(-_options.RecentPurchaseDays);
        return checkedOn <= today.AddDays(-(recent ? _options.RecentRefreshDays : _options.OlderRefreshDays));
    }
}

public sealed class InventoryPriceRefreshWorker : BackgroundService
{
    private readonly InventoryPriceRefreshService _refresh;
    private readonly ILogger<InventoryPriceRefreshWorker> _logger;

    public InventoryPriceRefreshWorker(
        InventoryPriceRefreshService refresh, ILogger<InventoryPriceRefreshWorker> logger)
    {
        _refresh = refresh;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                await _refresh.ProcessDueBatchAsync(LondonToday(now), now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory price refresh worker failed; it will retry without losing the queue.");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private static DateOnly LondonToday(DateTimeOffset now)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(now.DateTime); }
    }
}

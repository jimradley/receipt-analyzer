using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ReceiptAnalyzer.Jobs;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public sealed class InventoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ra-inventory-" + Guid.NewGuid().ToString("N"));

    public InventoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Synchronise_builds_all_history_and_keeps_pack_sizes_separate()
    {
        var history = new PurchaseHistoryData
        {
            Records =
            {
                new("cola", "Coca-Cola 330ml", "Tesco", new(2026, 1, 1), 1, 1.20m, "a"),
                new("cola", "Coca-Cola 2L", "Asda", new(2026, 2, 1), 1, 2.50m, "b"),
                new("cola", "Coca-Cola 330ml", "Morrisons", new(2026, 3, 1), 1, 1.10m, "c")
            }
        };

        var data = new InventoryStore(_dir).Synchronise(history);

        Assert.Equal(2, data.Products.Count);
        var can = Assert.Single(data.Products, p => p.Pack == "330ml");
        Assert.Equal(2, can.PurchaseCount);
        Assert.Equal("Morrisons", can.LastPurchasedStore);
        Assert.All(data.Products, p => Assert.Equal(InventoryMatchStatus.Pending, p.MatchStatus));
    }

    [Fact]
    public void Shopping_list_places_an_item_at_all_tied_cheapest_stores_and_keeps_unpriced_items()
    {
        var checkedOn = "2026-09-05";
        var product = new InventoryProduct(
            "beans-400g|400g", "beans-400g", "400g", "Beans 400g", 2, checkedOn,
            "Morrisons", 1.20m, InventoryMatchStatus.Matched, "https://www.trolley.co.uk/product/beans/ABC123",
            checkedOn, checkedOn, null,
            [
                new("Tesco", 1m, 1m, 1, 1m, null, false, checkedOn, "https://www.trolley.co.uk/product/beans/ABC123"),
                new("Asda", 2m, 1m, 2, 2m, "2 for £2", false, checkedOn, "https://www.trolley.co.uk/product/beans/ABC123"),
                new("B&M", .80m, .80m, 1, .80m, null, false, checkedOn, "https://www.trolley.co.uk/product/beans/ABC123")
            ]);
        var pending = new InventoryProduct("milk|2l", "milk", "2l", "Milk 2L", 1, checkedOn,
            "Tesco", 1.50m, InventoryMatchStatus.Pending);

        var result = ShoppingListBuilder.Build(new InventoryData { Products = [product, pending] }, []);

        Assert.Single(result.Stores, s => s.Store == "Tesco");
        var asda = Assert.Single(result.Stores, s => s.Store == "Asda");
        Assert.Equal(2m, Assert.Single(asda.Groceries).CheckoutCost);
        Assert.DoesNotContain(result.Stores, s => s.Store == "B&M");
        Assert.Equal("Milk 2L", Assert.Single(result.UnpricedProducts!).Item);
    }

    [Fact]
    public void Shopping_list_hides_inventory_products_with_no_real_saving()
    {
        var product = new InventoryProduct(
            "beans|400g", "beans", "400g", "Baked Beans 400g", 1, "2026-09-05",
            "Asda", 1m, InventoryMatchStatus.Matched, Offers:
            [new("Morrisons", 1m, 1m, 1, 1m, null, false, "2026-09-05", "https://example.test/beans")]);

        var result = ShoppingListBuilder.Build(new InventoryData { Products = [product] }, []);

        Assert.Empty(result.Stores);
        Assert.Empty(result.UnpricedProducts!);
    }

    [Fact]
    public void Trolley_parser_reads_supported_offers_and_multibuy_checkout_cost()
    {
        const string html = """
            <div class="comparison-table"><div class="collapse">
              <div class="_item"><div><svg title="Tesco"><title>Tesco</title></svg></div><div><div class="_price"><b>&pound;2.40</b><div>Any 2 for £4.00 Clubcard</div></div></div></div>
              <div class="_item"><div><svg title="B&amp;M"><title>B&amp;M</title></svg></div><div><div class="_price"><b>&pound;1.00</b></div></div></div>
            </div></div><div class="disclaimer price">
            """;

        var offers = TrolleyPriceClient.ParseOffers(
            html, "https://www.trolley.co.uk/product/example/ABC123", new(2026, 9, 5));

        var tesco = Assert.Single(offers);
        Assert.Equal("Tesco", tesco.Store);
        Assert.Equal(2m, tesco.EffectivePrice);
        Assert.Equal(2, tesco.MinimumQuantity);
        Assert.Equal(4m, tesco.CheckoutCost);
        Assert.True(tesco.LoyaltyRequired);
    }

    [Fact]
    public void Trolley_search_parser_combines_brand_title_and_pack()
    {
        const string html = """
            <a n=1 href="/product/maltesers-funsize/WGI529" title="Milk &amp; Honeycomb Funsize Snack Bags (214.5g)">
              <div class="_brand">Maltesers</div><div class="price">£2.00</div>
            </a>
            """;

        var candidate = Assert.Single(TrolleyPriceClient.ParseCandidates(html));
        Assert.Equal("Maltesers Milk & Honeycomb Funsize Snack Bags (214.5g)", candidate.Name);
        Assert.Equal("https://www.trolley.co.uk/product/maltesers-funsize/WGI529", candidate.Url);
    }

    [Fact]
    public async Task Each_hourly_batch_is_published_before_the_next_batch_runs()
    {
        var history = new PurchaseHistoryStore(_dir, NullLogger<PurchaseHistoryStore>.Instance);
        history.Save(new PurchaseHistoryData
        {
            Records =
            {
                new("one", "One 100g", "Tesco", new(2026, 9, 1), 1, 2m, "a"),
                new("two", "Two 100g", "Tesco", new(2026, 9, 1), 1, 3m, "b")
            }
        });
        var inventory = new InventoryStore(_dir);
        inventory.Synchronise(history.Load());
        inventory.Update(data =>
        {
            data.Products = data.Products.Select(p => p with
            {
                SourceUrl = $"https://www.trolley.co.uk/product/{p.ProductKey}/ABC123",
                MatchStatus = InventoryMatchStatus.Matched
            }).ToList();
            return true;
        });

        var handler = new StaticHtmlHandler(ProductHtml("Tesco", "1.00"));
        var trolley = new TrolleyPriceClient(new HttpClient(handler) { BaseAddress = new("https://www.trolley.co.uk/") });
        var options = new InventoryRefreshOptions { KnownBatchSize = 1, AgentBatchSize = 0, BatchDelay = TimeSpan.FromHours(1) };
        var service = new InventoryPriceRefreshService(
            inventory, history, trolley, new NoMatcher(), options,
            NullLogger<InventoryPriceRefreshService>.Instance);
        var now = new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);

        service.Start(new(2026, 9, 5), now);
        Assert.True(await service.ProcessDueBatchAsync(new(2026, 9, 5), now, CancellationToken.None));

        var afterOne = inventory.Load();
        Assert.Equal(1, afterOne.Refresh.PublishedBatch);
        Assert.Equal(1, afterOne.Refresh.Remaining);
        Assert.Single(afterOne.Products, p => p.CurrentOffers.Count > 0);
        Assert.False(await service.ProcessDueBatchAsync(new(2026, 9, 5), now.AddMinutes(59), CancellationToken.None));

        Assert.True(await service.ProcessDueBatchAsync(new(2026, 9, 5), now.AddHours(1), CancellationToken.None));
        var complete = inventory.Load();
        Assert.Equal("completed", complete.Refresh.Status);
        Assert.Equal(2, complete.Refresh.PublishedBatch);
        Assert.All(complete.Products, p => Assert.NotEmpty(p.CurrentOffers));
    }

    private static string ProductHtml(string store, string price) =>
        $"<div class=\"comparison-table\"><div class=\"_item\"><svg title=\"{store}\"></svg><b>&pound;{price}</b></div><div class=\"disclaimer price\">";

    private sealed class StaticHtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
    }

    private sealed class NoMatcher : IProductCandidateMatcher
    {
        public Task<IReadOnlyDictionary<string, string>> MatchAsync(
            IReadOnlyList<ProductMatchRequest> products, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }
}

using ReapDeep.Server;

var serverKey = Environment.GetEnvironmentVariable("REAPDEEP_SERVER_KEY")
    ?? throw new InvalidOperationException("Set REAPDEEP_SERVER_KEY to a server key (sk_...)");
var tag = args.Length > 0 ? args[0] : "sample";

await using var client = new ReapDeepClient(new ReapDeepOptions
{
    ServerKey = serverKey,
    IngestionUrl = Environment.GetEnvironmentVariable("REAPDEEP_INGESTION_URL") ?? "http://localhost:8000",
    RecommenderUrl = Environment.GetEnvironmentVariable("REAPDEEP_RECOMMENDER_URL") ?? "http://localhost:8002",
    OnError = e => Console.Error.WriteLine($"reapdeep: {e.Message}"),
});

await client.LoadFlagsAsync();

var start = DateTimeOffset.UtcNow.AddDays(-60);
var orders = Enumerable.Range(0, 120).Select(i => new ServerEvent(
    "order_completed",
    $"customer-{tag}-{i % 40}",
    new Dictionary<string, object?> { ["tag"] = tag, ["amount"] = 10m + i, ["channel"] = "phone" },
    start.AddMinutes(i)));
var imported = await client.ImportAsync(orders);

var customer = $"customer-{tag}-1";
var plan = new Dictionary<string, object?> { ["plan"] = "pro" };
client.Identify(customer, $"anon-{tag}", plan);
var enabled = client.IsEnabled($"srv-{tag}", customer, plan);
client.Capture("checkout_started", customer, new Dictionary<string, object?> { ["tag"] = tag, ["new_checkout"] = enabled });
await client.FlushAsync();

Console.WriteLine($$"""{"imported":{{imported.Accepted}},"rejected":{{imported.Rejected}},"flags_loaded":{{(client.FlagsLoaded ? "true" : "false")}},"flag_enabled":{{(enabled ? "true" : "false")}}}""");

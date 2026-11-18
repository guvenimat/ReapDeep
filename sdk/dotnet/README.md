# ReapDeep.Server

A .NET client for sending events to ReapDeep from a server and for evaluating feature flags in process. It targets .NET 8 and later, has no dependencies outside the base library and is trimming and AOT compatible.

It needs a **server key** (`sk_...`), created by an owner or admin under Settings → API keys. Keep it on the server: it can write events for any person in the project and read every flag's targeting rules.

## Set up

```csharp
using ReapDeep.Server;

await using var reapdeep = new ReapDeepClient(new ReapDeepOptions
{
    ServerKey = Environment.GetEnvironmentVariable("REAPDEEP_SERVER_KEY")!,
    IngestionUrl = "http://localhost:8000",
    RecommenderUrl = "http://localhost:8002",
    OnError = e => logger.LogWarning(e, "ReapDeep"),
});
```

Create one client for the application (for example as a singleton) and dispose it on shutdown: disposing sends what is still queued. Pass your own `HttpClient` as the second argument to control its handler or timeouts.

## Events

```csharp
reapdeep.Capture("order_completed", "customer-42", new Dictionary<string, object?>
{
    ["order_id"] = "1001",
    ["amount"] = 49.90m,
    ["$ip"] = httpContext.Connection.RemoteIpAddress?.ToString(),
});

reapdeep.Identify("customer-42", anonymousId: browserDistinctId, set: new Dictionary<string, object?> { ["plan"] = "pro" });
reapdeep.SetPersonProperties("customer-42", setOnce: new Dictionary<string, object?> { ["first_order"] = "1001" }, unset: ["trial_ends"]);
```

`Capture` only queues the event; it never blocks on the network. The queue is sent every `FlushInterval` (5 s) or as soon as `BatchSize` (500) events are waiting, through `POST /batch`. Each event gets an id when it is queued, so a retry after a lost response is stored once. Retries back off exponentially, follow `Retry-After` on `429`, and give up after `MaxAttempts` (4); a rejected batch (`401`, `400`) is not retried. Dropped events are counted in `DroppedEvents` and reported to `OnError`. When more than `MaxQueueSize` (10,000) events are waiting, new ones are dropped instead of using more memory.

The server takes no IP address or user agent from these requests. Pass `$ip` to attribute the event to a visitor's address (it is anonymized if the project asks for it), and `$browser`, `$os` or `$current_url` if you have them.

`await reapdeep.FlushAsync()` sends the queue now and throws `ReapDeepException` if a batch could not be delivered.

## Importing history

```csharp
var result = await reapdeep.ImportAsync(orders.Select(o => new ServerEvent(
    "order_completed", o.CustomerId,
    new Dictionary<string, object?> { ["order_id"] = o.Id, ["amount"] = o.Total },
    Timestamp: o.CompletedAt,
    EventId: $"order-{o.Id}")));
Console.WriteLine($"{result.Accepted} stored, {result.Rejected} rejected, {result.Duplicates} already sent");
```

`ImportAsync` sends batches of 5,000 directly and waits for each one. Timestamps are stored as given; events older than 364 days or more than an hour in the future are rejected. Stable `EventId`s make an interrupted import safe to restart within the server's deduplication window (10 minutes by default); after that, an event sent again is stored again.

## Feature flags

```csharp
await reapdeep.LoadFlagsAsync();

var props = new Dictionary<string, object?> { ["plan"] = "pro", ["seats"] = 12 };
if (reapdeep.IsEnabled("new-checkout", "customer-42", props)) { /* ... */ }

var model = reapdeep.GetFlag("similar-model", "customer-42")?.Variant ?? "co_viewed";
```

`LoadFlagsAsync` downloads the enabled flags once and then refreshes them every `FlagRefreshInterval` (30 s); until the first load, `GetFlag` returns `null`. Evaluation happens in process with the same hashing and rules as the Recommender and the browser SDK, so a person gets the same answer everywhere when the same identity is used. In the browser, flags are keyed by the visitor's first anonymous id; to agree with it, evaluate with that id.

`GetFlag` and `IsEnabled` send a `$feature_flag_called` event the first time a person sees a value, which experiments use to count exposures. Set `SendFlagExposures = false` to turn that off, or use `GetAllFlags`, which never sends exposures.

## Sample

[`samples/ReapDeep.Server.Sample`](samples/ReapDeep.Server.Sample/Program.cs) imports 120 orders dated 60 days back, identifies a customer, evaluates a flag and sends one event. `deploy/local/server-test.ps1` runs it against the local stack.

```powershell
$env:REAPDEEP_SERVER_KEY = "sk_..."
dotnet run --project sdk/dotnet/samples/ReapDeep.Server.Sample -- my-tag
```

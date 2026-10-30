using ReapDeep.LoadTest;

if (args.Length == 0)
{
    Console.WriteLine("""
        Usage: ReapDeep.LoadTest <command> [--option value ...]

          setup    --pg <conn> --bench-key <key>
          ingest   --name <label> --rate <events/s> --seconds <n> --batch <n> --run-id <id> --sr <conn> [--marker]
          seed     --sr <conn> --pg <conn> [--events 100000000] [--days 30] [--persons 150000] [--report-only]
          cohorts  --pg <conn> [--timeout 900]
          recommendations     --sr <conn> --pg <conn> [--timeout 900]
          recommend-latency   --url <recommender url> --key <public key> [--seconds 20] [--concurrency 16]
          bench    --api <url> --key <read key> [--runs 20]
          cleanup  --sr <conn> --pg <conn>
          demo     --pg <conn> [--url http://localhost:8000/capture] [--visitors 3000] [--days 56] [--seed 42] [--reset --sr <conn>]

        Common: --report <file.md> appends markdown results.
        """);
    return 1;
}

var opts = Options.Parse(args.Skip(1));
Report.Path = opts.Get("report");

try
{
    switch (args[0])
    {
        case "setup":
            await Setup.Run(opts.Require("pg"), opts.Require("bench-key"));
            break;
        case "ingest":
            await Ingest.Run(new IngestRun(
                opts.Get("name") ?? "ingest",
                opts.Get("url") ?? "http://localhost:8000/capture",
                opts.Get("key") ?? Setup.LoadTestPublicKey,
                opts.Int("rate", 10_000),
                opts.Int("seconds", 30),
                opts.Int("batch", 50),
                opts.Require("run-id"),
                opts.Require("sr"),
                opts.Has("marker")));
            break;
        case "seed":
            await Seeder.Run(opts.Require("sr"), opts.Require("pg"), opts.Long("events", 100_000_000), opts.Int("days", 30), opts.Int("persons", 150_000), opts.Has("report-only"));
            break;
        case "cohorts":
            await Cohorts.Run(opts.Require("pg"), opts.Int("timeout", 900));
            break;
        case "recommendations":
            await Recommendations.Run(opts.Require("sr"), opts.Require("pg"), opts.Int("timeout", 900));
            break;
        case "recommend-latency":
            await Recommendations.Latency(opts.Require("url"), opts.Require("key"), opts.Int("seconds", 20), opts.Int("concurrency", 16));
            break;
        case "bench":
            await Bench.Run(opts.Get("api") ?? "http://localhost:8001", opts.Require("key"), opts.Int("runs", 20));
            break;
        case "demo":
            await Demo.Run(opts.Get("url") ?? "http://localhost:8000/capture", opts.Require("pg"), opts.Int("visitors", 3000), opts.Int("days", 56), opts.Int("seed", 42),
                opts.Has("reset") ? opts.Require("sr") : null);
            break;
        case "cleanup":
            await Setup.Cleanup(opts.Require("sr"), opts.Require("pg"));
            break;
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}

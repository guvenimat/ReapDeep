using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ReapDeep.ServiceDefaults;

public static class Telemetry
{
    public static readonly string[] Sources = ["ReapDeep", "MySqlConnector", "Npgsql"];

    public static bool IsEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    public static IHostApplicationBuilder AddReapDeepTelemetry(this IHostApplicationBuilder builder, string serviceName, params string[] meters)
    {
        if (!IsEnabled) return builder;
        var web = builder is WebApplicationBuilder;
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceNamespace: "reapdeep"))
            .WithTracing(t =>
            {
                t.AddSource(Sources).AddHttpClientInstrumentation();
                if (web) t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsHealth(ctx));
            })
            .WithMetrics(m =>
            {
                m.AddMeter(["ReapDeep", "MySqlConnector", "Npgsql", .. meters]).AddHttpClientInstrumentation().AddRuntimeInstrumentation();
                if (web) m.AddAspNetCoreInstrumentation();
            })
            .UseOtlpExporter();
        return builder;
    }

    static bool IsHealth(HttpContext ctx) => ctx.Request.Path.StartsWithSegments("/healthz");
}

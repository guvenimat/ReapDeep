using System.Text.Json.Serialization;
using ReapDeep.Recommender.Recommendations;

namespace ReapDeep.Recommender.Infrastructure;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RecommendResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal partial class AppJsonContext : JsonSerializerContext;

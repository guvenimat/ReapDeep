using System.Text.Json.Serialization;
using ReapDeep.Recommender.Flags;
using ReapDeep.Recommender.Recommendations;

namespace ReapDeep.Recommender.Infrastructure;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RecommendResponse))]
[JsonSerializable(typeof(FlagsResponse))]
[JsonSerializable(typeof(FlagDefinitionsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(string))]
internal partial class AppJsonContext : JsonSerializerContext;

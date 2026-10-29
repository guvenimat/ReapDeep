using System.Text.Json.Serialization;
using ReapDeep.Ingestion.Capture;

namespace ReapDeep.Ingestion.Infrastructure;

[JsonSerializable(typeof(CaptureRequest))]
[JsonSerializable(typeof(CaptureResponse))]
[JsonSerializable(typeof(string))]
internal partial class AppJsonContext : JsonSerializerContext;

using System.Text.Json.Serialization;

namespace DotNetLab.Contracts;

/// <summary>
/// Contexto de serialización generado en tiempo de compilación (source generators).
/// Evita la reflexión en runtime: menos arranque en frío, compatible con trimming/AOT
/// y necesario para que el cliente Blazor WASM no pierda metadatos al recortar el IL.
/// </summary>
// Camel case en el JSON: convención de la web, mientras C# mantiene PascalCase.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
// Cada [JsonSerializable] emite un serializador especializado para ese tipo raíz.
[JsonSerializable(typeof(SpanDemoResponse))]
[JsonSerializable(typeof(ParseStrategyResult))]
[JsonSerializable(typeof(ChannelEvent))]
// El stream del endpoint de Channels llega como array: se registra la forma enumerable.
[JsonSerializable(typeof(IAsyncEnumerable<ChannelEvent>))]
[JsonSerializable(typeof(List<ChannelEvent>))]
[JsonSerializable(typeof(ChannelSummary))]
[JsonSerializable(typeof(RecordsDemoResponse))]
[JsonSerializable(typeof(PatternMatchResponse))]
[JsonSerializable(typeof(SensorReading))]
[JsonSerializable(typeof(LifetimesDemoResponse))]
[JsonSerializable(typeof(KeyedServiceResponse))]
[JsonSerializable(typeof(HealthResponse))]
// 'partial' porque el generador escribe la otra mitad de la clase en obj/.
public sealed partial class LabJsonSerializerContext : JsonSerializerContext;

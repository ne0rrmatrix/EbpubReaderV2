using System.Text.Json.Serialization;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

/// <summary>
/// Source-generated serialization metadata for everything the OPDS client persists as JSON
/// (the feed cache under <c>Opds/Cache/</c> and the server profiles in <c>Opds/servers.json</c>).
/// Release builds trim/AOT the app with reflection-based serialization switched off, so every
/// <see cref="System.Text.Json.JsonSerializer"/> call on those paths has to resolve its type
/// metadata through this context -- see the <c>TypeInfoResolver</c> assignments in
/// <see cref="OpdsCatalogCache"/> and <see cref="OpdsServerRepository"/>.
/// </summary>
/// <remarks>
/// The generation options mirror the naming/null handling those two callers already used under
/// reflection so previously written cache and server files still round-trip. Enums stay numeric
/// (no <c>JsonStringEnumConverter</c>) for the same reason. The
/// <c>Dictionary&lt;string, object&gt;</c> members are handled by the runtime-registered
/// <c>StringDictionaryConverter</c>, which the generated metadata defers to.
/// </remarks>
[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpdsFeed))]
[JsonSerializable(typeof(OpdsFeedCacheEntry))]
[JsonSerializable(typeof(List<OpdsServer>))]
partial class OpdsJsonContext : JsonSerializerContext
{
}

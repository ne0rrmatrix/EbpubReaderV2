using System.Text.Json;
using System.Text.Json.Serialization;
using DisplayBook.Viewer.Bridge;
using DisplayBook.Viewer.Models;

namespace DisplayBook.Viewer.Serialization;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ReaderBridgeMessage))]
[JsonSerializable(typeof(EpubLocator))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
partial class ReaderJsonContext : JsonSerializerContext
{
}

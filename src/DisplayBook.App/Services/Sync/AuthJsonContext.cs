using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplayBook.App.Services.Sync;

sealed record IdentityToolkitSignRequest(string Email, string Password, bool ReturnSecureToken = true);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IdentityToolkitSignRequest))]
partial class AuthJsonContext : JsonSerializerContext
{
}

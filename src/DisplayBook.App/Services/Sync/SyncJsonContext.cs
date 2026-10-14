using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplayBook.App.Services.Sync;

/// <summary>
/// A Firestore REST document body. Each field is wrapped in a single-key object naming its type
/// (<c>stringValue</c>, <c>integerValue</c>, <c>timestampValue</c>) -- that shape is Firestore's,
/// not ours. Integers are strings per the REST API.
/// </summary>
sealed record FirestoreDocument(FirestorePositionFields Fields);

sealed record FirestorePositionFields(
	FirestoreString ResourceHref,
	FirestoreInteger CharOffset,
	FirestoreInteger Page,
	FirestoreInteger PageCount,
	FirestoreTimestamp UpdatedAt);

sealed record FirestoreString(string StringValue);

sealed record FirestoreInteger(string IntegerValue);

sealed record FirestoreTimestamp(string TimestampValue);

/// <summary>
/// Source-generated metadata for the position document <see cref="PositionSyncService"/> PATCHes
/// to Firestore. Release builds trim/AOT the app with reflection-based serialization disabled, so
/// the body has to be a declared type with generated metadata rather than an anonymous object.
/// Pulled documents are read with <see cref="JsonDocument"/> and need nothing here.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FirestoreDocument))]
partial class SyncJsonContext : JsonSerializerContext
{
}

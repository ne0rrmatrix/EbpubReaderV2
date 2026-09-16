using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplayBook.App.Services;

/// <summary>
/// Serializes <see cref="Dictionary{TKey,TValue}"/> with <see langword="string"/> keys and
/// <see langword="object"/> values as plain JSON (values written/read as strings), which is what
/// the OPDS parser stores in <c>ExtendedFeedMetadata</c> and <c>ExtendedMetadata</c>.
/// </summary>
sealed class StringDictionaryConverter : JsonConverter<Dictionary<string, object>>
{
	public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			throw new JsonException($"Cannot deserialize dictionary from token {reader.TokenType}.");
		}

		Dictionary<string, object> result = [with(StringComparer.OrdinalIgnoreCase)];
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JsonException($"Unexpected token {reader.TokenType} while reading dictionary.");
			}

			string key = reader.GetString() ?? string.Empty;
			reader.Read();

			object value = reader.TokenType switch
			{
				JsonTokenType.Null => null!,
				JsonTokenType.String => reader.GetString() ?? string.Empty,
				JsonTokenType.True => true,
				JsonTokenType.False => false,
				JsonTokenType.Number => reader.GetDecimal(),
				_ => reader.GetString() ?? string.Empty,
			};

			result[key] = value;
		}

		return result;
	}

	public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		foreach (KeyValuePair<string, object> pair in value)
		{
			writer.WritePropertyName(pair.Key);
			WriteValue(writer, pair.Value);
		}

		writer.WriteEndObject();
	}

	static void WriteValue(Utf8JsonWriter writer, object? value)
	{
		switch (value)
		{
			case null:
				writer.WriteNullValue();
				break;
			case string s:
				writer.WriteStringValue(s);
				break;
			case bool b:
				writer.WriteBooleanValue(b);
				break;
			case int i:
				writer.WriteNumberValue(i);
				break;
			case long l:
				writer.WriteNumberValue(l);
				break;
			case decimal d:
				writer.WriteNumberValue(d);
				break;
			case double dbl:
				writer.WriteNumberValue(dbl);
				break;
			default:
				writer.WriteStringValue(value.ToString() ?? string.Empty);
				break;
		}
	}
}
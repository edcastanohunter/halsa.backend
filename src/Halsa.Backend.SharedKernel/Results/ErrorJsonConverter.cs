using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halsa.Backend.SharedKernel.Results;

/// <summary>
/// Round-trips <see cref="Error"/> through System.Text.Json. A successful <see cref="Result"/> carries
/// <see cref="Error.None"/>, whose empty code the public constructor rightly rejects; without this
/// converter every cached success would fail to deserialize.
/// </summary>
internal sealed class ErrorJsonConverter : JsonConverter<Error>
{
    public override Error Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object to deserialize {nameof(Error)}.");
        }

        string code = string.Empty;
        string description = string.Empty;
        ErrorType type = ErrorType.Failure;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string property = reader.GetString() ?? string.Empty;
            reader.Read();

            if (property.Equals(nameof(Error.Code), StringComparison.OrdinalIgnoreCase))
            {
                code = reader.GetString() ?? string.Empty;
            }
            else if (property.Equals(nameof(Error.Description), StringComparison.OrdinalIgnoreCase))
            {
                description = reader.GetString() ?? string.Empty;
            }
            else if (property.Equals(nameof(Error.Type), StringComparison.OrdinalIgnoreCase))
            {
                type = reader.TokenType == JsonTokenType.String
                    ? Enum.Parse<ErrorType>(reader.GetString()!, ignoreCase: true)
                    : (ErrorType)reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }

        return string.IsNullOrEmpty(code) ? Error.None : new Error(code, description, type);
    }

    public override void Write(Utf8JsonWriter writer, Error value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        writer.WriteStartObject();
        writer.WriteString(Name(nameof(Error.Code), options), value.Code);
        writer.WriteString(Name(nameof(Error.Description), options), value.Description);
        writer.WriteNumber(Name(nameof(Error.Type), options), (int)value.Type);
        writer.WriteEndObject();
    }

    private static string Name(string property, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;
}

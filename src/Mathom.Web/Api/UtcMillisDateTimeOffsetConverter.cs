using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mathom.Web.Api;

// Wire format for every API timestamp: UTC, exactly three fractional digits (2026-10-23T08:15:00.000Z).
// Reading stays lenient so clients may send any ISO 8601 offset (capturedAt).
public sealed class UtcMillisDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
}

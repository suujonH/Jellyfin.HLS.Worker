using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Hls.Worker.Protocol;

/// <summary>
/// Reads a GUID in any format and writes it as 32 digits, the way the server does: the default converter takes only the form
/// with hyphens, which the server never writes.
/// </summary>
internal sealed class GuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Guid.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("N", CultureInfo.InvariantCulture));
}

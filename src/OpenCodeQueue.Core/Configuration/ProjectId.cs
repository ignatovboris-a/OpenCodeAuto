using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodeQueue.Core.Configuration;

[JsonConverter(typeof(ProjectIdJsonConverter))]
public readonly partial record struct ProjectId(string Value)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && SlugRegex().IsMatch(Value);

    public override string ToString() => Value;

    public static implicit operator ProjectId(string value) => new(value);

    public static implicit operator string(ProjectId value) => value.Value;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]*$")]
    private static partial Regex SlugRegex();
}

public sealed class ProjectIdJsonConverter : JsonConverter<ProjectId>
{
    public override ProjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new ProjectId(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, ProjectId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

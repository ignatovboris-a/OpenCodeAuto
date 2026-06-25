using System.Text.RegularExpressions;

namespace OpenCodeQueue.Core.Prompts;

public sealed partial record NumericPrefix : IComparable<NumericPrefix>
{
    public NumericPrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !PrefixRegex().IsMatch(value))
        {
            throw new ArgumentException("Числовой префикс должен состоять из числовых сегментов, разделённых точками.", nameof(value));
        }

        Value = value;
        var segments = new List<long>();
        foreach (var segment in value.Split('.'))
        {
            if (!long.TryParse(segment, out var parsed))
            {
                throw new ArgumentException("Числовой префикс содержит слишком большой числовой сегмент.", nameof(value));
            }

            segments.Add(parsed);
        }

        Segments = segments;
    }

    public string Value { get; }

    public IReadOnlyList<long> Segments { get; }

    public int CompareTo(NumericPrefix? other)
    {
        if (other is null)
        {
            return 1;
        }

        var max = Math.Max(Segments.Count, other.Segments.Count);
        for (var index = 0; index < max; index++)
        {
            var left = index < Segments.Count ? Segments[index] : 0;
            var right = index < other.Segments.Count ? other.Segments[index] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return Segments.Count.CompareTo(other.Segments.Count);
    }

    public override string ToString() => Value;

    public static bool TryParse(string? value, out NumericPrefix? prefix)
    {
        if (!string.IsNullOrWhiteSpace(value) && PrefixRegex().IsMatch(value))
        {
            try
            {
                prefix = new NumericPrefix(value);
                return true;
            }
            catch (ArgumentException)
            {
            }
        }

        prefix = null;
        return false;
    }

    public static bool TryParseFileNamePrefix(string fileName, out NumericPrefix? prefix)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var match = FileNamePrefixRegex().Match(nameWithoutExtension);
        if (match.Success)
        {
            return TryParse(match.Groups[1].Value, out prefix);
        }

        prefix = null;
        return false;
    }

    [GeneratedRegex("^[0-9]+(?:\\.[0-9]+)*$")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex("^([0-9]+(?:\\.[0-9]+)*)(?:$|[ ._-])")]
    private static partial Regex FileNamePrefixRegex();
}

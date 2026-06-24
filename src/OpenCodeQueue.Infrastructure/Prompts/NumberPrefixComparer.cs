namespace OpenCodeQueue.Infrastructure.Prompts;

internal sealed class NumberPrefixComparer : IComparer<string>
{
    public static NumberPrefixComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftSegments = left.Split('.');
        var rightSegments = right.Split('.');
        var maxSegments = Math.Max(leftSegments.Length, rightSegments.Length);

        for (var index = 0; index < maxSegments; index++)
        {
            var leftValue = index < leftSegments.Length ? int.Parse(leftSegments[index]) : 0;
            var rightValue = index < rightSegments.Length ? int.Parse(rightSegments[index]) : 0;
            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftSegments.Length.CompareTo(rightSegments.Length);
    }
}

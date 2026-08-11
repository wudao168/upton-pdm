using System.Globalization;

namespace Upton.Pdm.Domain;

public readonly record struct RevisionLabel(char? BaseRevision, int WorkIteration, bool IsReleased)
{
    public string Display => IsReleased
        ? BaseRevision?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("正式版本缺少修订号。")
        : BaseRevision is null
            ? $"W{WorkIteration}"
            : $"{BaseRevision}-W{WorkIteration}";

    public static RevisionLabel InitialWork() => new(null, 1, false);

    public static RevisionLabel Released(char revision)
    {
        var normalized = char.ToUpperInvariant(revision);
        if (normalized is < 'A' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "正式版本必须是A到Z。 ");
        }

        return new RevisionLabel(normalized, 0, true);
    }

    public RevisionLabel NextWork()
    {
        if (IsReleased)
        {
            return new RevisionLabel(BaseRevision, 1, false);
        }

        return this with { WorkIteration = checked(WorkIteration + 1) };
    }

    public RevisionLabel Release()
    {
        if (IsReleased)
        {
            throw new InvalidOperationException("已发布版本不能再次发布。 ");
        }

        var revision = BaseRevision is null ? 'A' : NextRevision(BaseRevision.Value);
        return Released(revision);
    }

    public static RevisionLabel Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z')
        {
            return Released(normalized[0]);
        }

        if (normalized.StartsWith('W') && int.TryParse(normalized.AsSpan(1), out var iteration) && iteration > 0)
        {
            return new RevisionLabel(null, iteration, false);
        }

        if (normalized.Length >= 4 && normalized[0] is >= 'A' and <= 'Z' && normalized.AsSpan(1, 2).SequenceEqual("-W") && int.TryParse(normalized.AsSpan(3), out iteration) && iteration > 0)
        {
            return new RevisionLabel(normalized[0], iteration, false);
        }

        throw new FormatException($"无法识别版本号：{value}");
    }

    public override string ToString() => Display;

    private static char NextRevision(char value)
    {
        if (value == 'Z')
        {
            throw new InvalidOperationException("正式版本已达到Z，需要管理员处理。 ");
        }

        return (char)(value + 1);
    }
}

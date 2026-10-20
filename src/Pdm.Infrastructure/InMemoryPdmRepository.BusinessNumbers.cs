using System.Globalization;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<string, int> businessNumberSequences = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> ReserveBusinessNumberAsync(string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = businessNumberSequences.GetValueOrDefault(prefix);
            foreach (var number in projectApplications.Values.Select(item => item.Number).Concat(packages.Values.Select(item => item.Number)))
                if (number.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && number.Length == prefix.Length + 4
                    && number[prefix.Length..].All(char.IsAsciiDigit))
                    current = Math.Max(current, int.Parse(number.AsSpan(prefix.Length), CultureInfo.InvariantCulture));
            if (current >= 9999) throw new PdmRuleException($"编号 {prefix} 的4位流水号已用尽，请联系管理员处理。");
            var next = current + 1;
            businessNumberSequences[prefix] = next;
            return Task.FromResult(prefix + next.ToString("D4", CultureInfo.InvariantCulture));
        }
    }
}

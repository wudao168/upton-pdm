using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectApplicationSettlements
{
    private const decimal MaximumAmount = 1_000_000_000m;

    public static IReadOnlyList<ProjectSettlementLine> Normalize(IReadOnlyList<ProjectSettlementLine>? lines, bool required)
    {
        if (lines is null) return [];
        if (lines.Count > 100) throw new PdmRuleException("项目结算明细最多100条。");
        var result = new ProjectSettlementLine[lines.Count];
        decimal total = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index] ?? throw new PdmRuleException($"项目结算第{index + 1}行资料无效，请重新填写。");
            line = line with { Content = line.Content?.Trim() ?? "", Unit = line.Unit?.Trim(), Note = line.Note?.Trim() };
            if (line.Content.Length > 200 || line.Unit?.Length > 20 || line.Note?.Length > 500)
                throw new PdmRuleException($"项目结算第{index + 1}行内容最多200字、单位最多20字、备注最多500字。");
            if (line.Quantity is { } quantity && (quantity < 1 || quantity > 1_000_000m || decimal.Truncate(quantity) != quantity))
                throw new PdmRuleException($"项目结算第{index + 1}行数量应为1至100万的整数。");
            if (line.UnitPrice is { } price && (price < 0 || price > MaximumAmount || decimal.Round(price, 2) != price))
                throw new PdmRuleException($"项目结算第{index + 1}行单价应为0至10亿元，最多保留两位小数。");
            if (required && (line.Content.Length == 0 || !line.Quantity.HasValue || !line.UnitPrice.HasValue))
                throw new PdmRuleException($"请补齐项目结算第{index + 1}行的内容、数量和单价；暂未填写完成可先保存草稿。");
            if (line.Quantity.HasValue && line.UnitPrice.HasValue)
            {
                var amount = decimal.Round(line.Quantity.Value * line.UnitPrice.Value, 2, MidpointRounding.AwayFromZero);
                if (amount > MaximumAmount) throw new PdmRuleException($"项目结算第{index + 1}行小计不能超过10亿元。");
                total += amount;
                if (total > MaximumAmount) throw new PdmRuleException("项目结算总金额不能超过10亿元。");
            }
            result[index] = line;
        }
        return result;
    }

    public static decimal? Total(IReadOnlyList<ProjectSettlementLine>? lines)
    {
        if (lines is null || lines.Count == 0 || lines.Any(line => line is null || !line.Quantity.HasValue || !line.UnitPrice.HasValue)) return null;
        return lines.Sum(line => decimal.Round(line.Quantity!.Value * line.UnitPrice!.Value, 2, MidpointRounding.AwayFromZero));
    }
}

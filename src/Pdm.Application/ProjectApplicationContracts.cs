namespace Upton.Pdm.Application;

public sealed record ProjectApplicationContractLine(string Name, string Specification, decimal? Quantity, string Remarks);

public sealed record ProjectApplicationContractGroup(ProjectApplicationContractLine Main, IReadOnlyList<ProjectApplicationContractLine> Children);

public sealed record ProjectApplicationContract(string Number, string Details)
{
    public const int MaximumLines = 100;
    public const int MaximumQuantity = 1_000_000;

    public IReadOnlyList<ProjectApplicationContractGroup>? Groups { get; init; }

    public bool IsComplete() => !string.IsNullOrWhiteSpace(Number) && (Groups is null
        ? !string.IsNullOrWhiteSpace(Details)
        : Groups.Count > 0 && Groups.All(group => IsComplete(group.Main) && group.Children.All(IsComplete)));

    private static bool IsComplete(ProjectApplicationContractLine line) => line.Name.Length > 0 && line.Quantity.HasValue;

    internal static ProjectApplicationContract? PreserveStructure(ProjectApplicationContract? input, ProjectApplicationContract? previous)
    {
        if (input is null) return previous;
        if (input.Groups is not null) return previous is null ? input : input with { Details = previous.Details };
        if (previous?.Groups is null) return input;
        return input with { Groups = previous.Groups, Details = previous.Details };
    }

    internal static ProjectApplicationContract Normalize(ProjectApplicationContract? input, bool required)
    {
        var contract = new ProjectApplicationContract(input?.Number?.Trim() ?? "", input?.Details?.Trim() ?? "");
        if (contract.Number.Length > 100 || contract.Details.Length > 4000)
            throw new PdmRuleException("合同编号最多100字，历史合同明细最多4000字。");

        if (input?.Groups is { } groups)
        {
            if (groups.Sum(group => 1L + (group?.Children?.Count ?? 0)) > MaximumLines)
                throw new PdmRuleException("合同明细的主项目和子项目合计最多100行。");
            contract = contract with
            {
                Groups = groups.Select(group => new ProjectApplicationContractGroup(
                    NormalizeLine(group?.Main), (group?.Children ?? []).Select(NormalizeLine).ToArray())).ToArray()
            };
        }

        if (required && !contract.IsComplete())
            throw new PdmRuleException(contract.Groups is null ? "请填写合同编号和合同明细。" : "请填写合同编号，并补齐合同明细每行的名称和数量。");
        return contract;
    }

    private static ProjectApplicationContractLine NormalizeLine(ProjectApplicationContractLine? line)
    {
        if (line is null) throw new PdmRuleException("合同明细缺少主项目或子项目资料，请重新填写。");
        var name = line.Name?.Trim() ?? "";
        var specification = line.Specification?.Trim() ?? "";
        var remarks = line.Remarks?.Trim() ?? "";
        if (name.Length > 200 || specification.Length > 200 || remarks.Length > 500)
            throw new PdmRuleException("合同明细的名称和规格最多200字，备注最多500字。");
        if (line.Quantity is { } quantity && (quantity < 1 || quantity > MaximumQuantity || decimal.Truncate(quantity) != quantity))
            throw new PdmRuleException("合同明细的数量应为1至100万的整数。");
        return new(name, specification, line.Quantity, remarks);
    }
}

public sealed record ProjectApplicationContractLookup(Guid ProjectId, string ProjectCode, Guid ApplicationId, string ApplicationNumber,
    string Status, ProjectApplicationContract? Contract);

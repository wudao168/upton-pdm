using System.Globalization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ApprovalConditionRules
{
    public static IReadOnlyDictionary<string, string> Fields(string flow)
    {
        var fields = new Dictionary<string, string> {
            ["SubmitterRole"] = "set", ["Submitter"] = "text", ["SubmitterDepartment"] = "set",
            ["ProjectCode"] = "text", ["ProjectName"] = "text"
        };
        if (flow.StartsWith("expense-", StringComparison.Ordinal))
            foreach (var (key, type) in new[] { ("TotalAmount", "number"), ("Purpose", "enum"), ("Department", "enum"), ("RequiredOn", "date"), ("Reason", "text"), ("LineCount", "number"), ("ItemKind", "set") }) fields[key] = type;
        else if (flow == "validation-plan")
            foreach (var (key, type) in new[] { ("PreparedBy", "text"), ("ValidationDate", "date"), ("ItemCount", "number"), ("RevisionNumber", "number") }) fields[key] = type;
        else if (flow == "material-code-application")
            foreach (var key in new[] { "MaterialName", "Specification", "Brand", "Remark" }) fields[key] = "text";
        else
            foreach (var (key, type) in new[] { ("ReleaseScope", "text"), ("ChangeReason", "text"), ("ItemCount", "number") }) fields[key] = type;
        return fields;
    }

    public static ApprovalConditionSet? Normalize(ApprovalConditionSet? conditions, string flow)
    {
        if (conditions is null) return null;
        if (conditions.Match is not ("All" or "Any") || conditions.Rules is null || conditions.Rules.Count is < 1 or > 10)
            throw new PdmRuleException("流转条件须选择全部满足或任一满足，并配置1至10条规则。");
        var fields = Fields(flow);
        return conditions with { Rules = conditions.Rules.Select(rule => {
            if (rule is null || !fields.TryGetValue(rule.Field, out var type)) throw new PdmRuleException("流转条件包含此审批单不支持的字段，请重新选择。");
            var allowed = type is "number" or "date" ? new[] { "Eq", "Ne", "Gt", "Gte", "Lt", "Lte", "Empty", "NotEmpty" }
                : type == "text" ? ["Eq", "Ne", "Contains", "NotContains", "Empty", "NotEmpty"] : ["Eq", "Ne", "Empty", "NotEmpty"];
            if (!allowed.Contains(rule.Operator)) throw new PdmRuleException("流转条件的比较方式与字段类型不匹配。");
            if (rule.Operator is "Empty" or "NotEmpty") return rule with { Value = null };
            var value = rule.Value?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 500) throw new PdmRuleException("请填写流转条件的比较值，最多500个字符。");
            if (type == "number" && !Number(value, out _) || type == "date" && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new PdmRuleException("流转条件的金额、数量或日期格式不正确。");
            if (rule.Field == "Purpose" && value is not ("SitePurchase" or "RepairSupplement")
                || rule.Field == "Department" && value is not ("Procurement" or "SupplyChain")
                || rule.Field == "ItemKind" && value is not ("Material" or "Service")) throw new PdmRuleException("流转条件的选项无效，请重新选择。");
            return rule with { Value = value };
        }).ToArray() };
    }

    public static Dictionary<string, object?> Context(Project project, string actor, OrganizationDirectory directory, IReadOnlyDictionary<string, object?>? values = null)
    {
        var result = values?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
        result["ProjectCode"] = project.Code;
        result["ProjectName"] = project.Name;
        result["Submitter"] = actor;
        result["SubmitterDepartment"] = directory.Memberships.Where(member => member.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))
            .Select(member => member.UnitId.ToString()).ToArray();
        return result;
    }

    public static bool Matches(ApprovalConditionSet? conditions, string flow, UserAccount? submitter, IReadOnlyDictionary<string, object?>? values)
    {
        if (conditions is null) return true;
        conditions = Normalize(conditions, flow)!;
        // Evaluate every rule so invalid configuration cannot hide behind short-circuit evaluation.
        var matches = conditions.Rules.Select(rule => Match(rule, Fields(flow)[rule.Field], submitter, values)).ToArray();
        return conditions.Match == "All" ? matches.All(value => value) : matches.Any(value => value);
    }

    private static bool Match(ApprovalFieldCondition rule, string type, UserAccount? submitter, IReadOnlyDictionary<string, object?>? values)
    {
        if (rule.Field == "SubmitterRole")
        {
            if (submitter is null) throw new PdmRuleException("提交人账号无效，无法匹配审批条件。");
            return rule.Operator switch { "Empty" => false, "NotEmpty" => true, "Ne" => !submitter.HasRole(rule.Value!), _ => submitter.HasRole(rule.Value!) };
        }
        if (values is null || !values.TryGetValue(rule.Field, out var raw)) throw new PdmRuleException("审批单缺少条件字段，请刷新后重试或联系管理员检查流程。");
        var collection = raw as IEnumerable<string>;
        var text = raw switch { DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), IFormattable value => value.ToString(null, CultureInfo.InvariantCulture), _ => raw?.ToString() };
        var empty = collection is not null ? !collection.Any() : string.IsNullOrWhiteSpace(text);
        if (rule.Operator == "Empty") return empty;
        if (rule.Operator == "NotEmpty") return !empty;
        if (empty) return false;
        if (collection is not null) { var contains = collection.Contains(rule.Value!, StringComparer.OrdinalIgnoreCase); return rule.Operator == "Ne" ? !contains : contains; }
        int compared;
        if (type == "number") {
            if (!Number(text!, out var actual) || !Number(rule.Value!, out var target)) throw new PdmRuleException("审批单数值字段无效，无法计算流转条件。");
            compared = actual.CompareTo(target);
        } else compared = string.Compare(text, rule.Value, StringComparison.OrdinalIgnoreCase);
        return rule.Operator switch {
            "Eq" => compared == 0, "Ne" => compared != 0, "Gt" => compared > 0, "Gte" => compared >= 0, "Lt" => compared < 0, "Lte" => compared <= 0,
            "Contains" => text!.Contains(rule.Value!, StringComparison.OrdinalIgnoreCase), "NotContains" => !text!.Contains(rule.Value!, StringComparison.OrdinalIgnoreCase), _ => false
        };
    }
    private static bool Number(string value, out decimal result) => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
}

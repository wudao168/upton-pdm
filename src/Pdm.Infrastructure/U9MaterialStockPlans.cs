using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class U9OpenApiClient
{
    public async Task<IReadOnlyList<MaterialStockPlan>?> QueryStockPlansAsync(string baseUrl, string token, string enterpriseCode,
        string organizationCode, DateTimeOffset syncedAt, CancellationToken cancellationToken, IReadOnlyList<string>? materialCodes = null)
    {
        var result = new Dictionary<string, MaterialStockPlan>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>();
        var selectedCodes = materialCodes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // U9C支持ne比较。非零规划与原常备件的清零回查分开，避免扫描全部料品。
        var filters = new List<object>();
        if (materialCodes is null)
        {
            filters.Add(new { Field = "Org.Code", Operator = "eq", Value = organizationCode });
            filters.Add(new { Field = "InventoryInfo.SafetyStockQty", Operator = "ne", Value = "0" });
        }
        else if (materialCodes.Count == 0) return [];
        else if (materialCodes.Count == 1) filters.Add(new { Field = "Code", Operator = "eq", Value = materialCodes[0] });
        // 料号OR筛选单独分组；返回资料仍须逐条通过下方组织校验。
        else filters.Add(new { Logic = "or", Filters = materialCodes.Select(code => new { Field = "Code", Operator = "eq", Value = code }).ToArray() });
        for (var page = 1; page <= 1000; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(baseUrl, "/webapi/CommonEntity/Query"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    PageSize = 1000, PageIndex = page, EntityFullName = "UFIDA.U9.CBO.SCM.Item.ItemMaster",
                    ReturnFields = new[] { "ID", "Code", "Org.Code", "Effective.IsEffective", "InventoryInfo.SafetyStockQty", "InventoryInfo.ReorderQty", "InventoryUOM.Code", "InventoryUOM.Name" },
                    Filters = filters
                }), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
            using var response = await SendAsync(request, "U9C库存规划读取", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new PdmRuleException($"U9C库存规划读取失败：HTTP {(int)response.StatusCode}，保留上次常备件资料，请在料品同步中重试。");
            using var document = ParseJson(await response.Content.ReadAsStringAsync(cancellationToken), "U9C库存规划返回格式无效，保留上次常备件资料。");
            if (ReadInt(document.RootElement, "ResCode") != 0) throw new PdmRuleException("U9C库存规划读取未成功，保留上次常备件资料，请在料品同步中重试。");
            var rows = ReadDataRows(document.RootElement).ToArray();
            if (rows.Length == 0) return result.Values.ToArray();
            foreach (var row in rows)
            {
                var id = ReadString(row, "ID");
                if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id)) throw new PdmRuleException("U9C库存规划分页未向后推进，已停止读取并保留上次常备件资料。");
                var code = ReadString(row, "Code");
                var plan = ReadStockPlan(row);
                if (string.IsNullOrWhiteSpace(code) || selectedCodes is not null && !selectedCodes.Contains(code)
                    || plan.OrganizationCode != organizationCode || plan.SafetyStockQuantity is null || plan.IsEffective is null) continue;
                result[code.Trim()] = new(enterpriseCode, organizationCode, code.Trim(), plan.SafetyStockQuantity.Value,
                    plan.ReorderQuantity, plan.IsEffective.Value, plan.UnitCode, plan.UnitName, syncedAt);
            }
        }
        throw new PdmRuleException("U9C库存规划分页超过处理范围，保留上次常备件资料，请检查接口分页设置。");
    }
}

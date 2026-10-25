using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    public Task<WorkHoursViewDto> ReadWorkHoursAsync(DateTime from, DateTime to, CancellationToken token) =>
        GetJsonAsync<WorkHoursViewDto>("api/work-hours?from=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + "&to=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&report=false", token);

    public async Task SaveWorkHoursAsync(WorkHoursSaveDto input, CancellationToken token)
    {
        HttpResponseMessage response;
        using (var content = new StringContent(serializer.Serialize(input), Encoding.UTF8, "application/json"))
        {
            try { response = await httpClient.PutAsync("api/work-hours", content, token).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException)
            {
                // 不自动重试新增工时，避免已提交但响应丢失时重复累计。
                throw new WorkHoursSaveUncertainException(error);
            }
        }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)response.StatusCode >= 500)
                throw new WorkHoursSaveUncertainException(new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body)));
            ThrowAuthenticationExpiredIfNeeded(response);
            throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
        }
    }

    internal static WorkHoursSaveDto PrepareWorkHours(WorkHoursViewDto current, IReadOnlyList<WorkHourInputDto> added)
    {
        if (added == null || added.Count == 0) throw new InvalidOperationException("请至少填写一行工时。");
        var today = DateTime.ParseExact(current.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var row in added)
        {
            if (!DateTime.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date.Year < 2000 || date > today) throw new InvalidOperationException("请选择有效日期，实际工时不能填写未来日期。");
            if (row.ProjectId == Guid.Empty || (row.Location != "厂内" && row.Location != "厂外")) throw new InvalidOperationException("请为每一行选择项目和厂内／厂外。");
            if (row.Hours <= 0 || row.Hours > 24 || row.Hours * 2 != decimal.Truncate(row.Hours * 2)) throw new InvalidOperationException("工时须大于0，按0.5小时填写且不超过24小时。");
        }
        var from = added.Min(x => x.Date); var to = added.Max(x => x.Date);
        if ((DateTime.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture) - DateTime.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Days >= 31)
            throw new InvalidOperationException("单次填报的日期跨度最多31天，请分次保存。");
        // 既有记录必须完整带回；原接口按日期范围替换，不能只发送新增行。
        var entries = current.Sheets.SelectMany(x => x.Entries).Where(x => string.CompareOrdinal(x.Date, from) >= 0 && string.CompareOrdinal(x.Date, to) <= 0)
            .Concat(added).GroupBy(x => new { x.Date, x.ProjectId, x.Location })
            .Select(group => new WorkHourInputDto { Date = group.Key.Date, ProjectId = group.Key.ProjectId, Location = group.Key.Location, Hours = group.Sum(x => x.Hours) }).ToList();
        if (entries.GroupBy(x => x.Date).Any(group => group.Sum(x => x.Hours) > 24)) throw new InvalidOperationException("加上已填记录后，同一天所有项目的工时合计不能超过24小时。");
        if (entries.Count > 500) throw new InvalidOperationException("本次工时记录超过500条，请减少日期范围后分次保存。");
        var versions = new List<WorkHoursVersionDto>();
        for (var month = DateTime.ParseExact(from.Substring(0, 7) + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture); month.ToString("yyyy-MM", CultureInfo.InvariantCulture).CompareTo(to.Substring(0, 7)) <= 0; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var sheet = current.Sheets.SingleOrDefault(x => x.Month == key);
            if (sheet == null) throw new InvalidOperationException("工时版本信息不完整，请重新打开工时窗口。");
            versions.Add(new WorkHoursVersionDto { Month = key, RowVersion = sheet.RowVersion });
        }
        return new WorkHoursSaveDto { From = from, To = to, Entries = entries, Versions = versions };
    }

    internal sealed class WorkHoursSaveUncertainException : InvalidOperationException
    {
        public WorkHoursSaveUncertainException(Exception error) : base("保存结果尚未确认，请先到工时管理核对，避免重复提交。填写内容已保留。", error) { }
    }
}

internal sealed class WorkHoursViewDto
{
    public string Today { get; set; }
    public string DisplayName { get; set; }
    public List<WorkHoursProjectDto> Projects { get; set; } = new List<WorkHoursProjectDto>();
    public List<WorkHoursMonthDto> Sheets { get; set; } = new List<WorkHoursMonthDto>();
}
internal sealed class WorkHoursProjectDto
{
    public Guid Id { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public string Label => Code + " · " + Name;
}
internal sealed class WorkHoursMonthDto
{
    public string Month { get; set; }
    public long RowVersion { get; set; }
    public List<WorkHourInputDto> Entries { get; set; } = new List<WorkHourInputDto>();
}
internal sealed class WorkHourInputDto
{
    public string Date { get; set; }
    public string Location { get; set; }
    public decimal Hours { get; set; }
    public Guid ProjectId { get; set; }
}
internal sealed class WorkHoursVersionDto
{
    public string Month { get; set; }
    public long RowVersion { get; set; }
}
internal sealed class WorkHoursSaveDto
{
    public string From { get; set; }
    public string To { get; set; }
    public List<WorkHourInputDto> Entries { get; set; }
    public List<WorkHoursVersionDto> Versions { get; set; }
}

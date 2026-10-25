using System.Text.Json;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

/// <summary>与网页日历共用同一份节假日源文件，服务端提醒不依赖浏览器在线。</summary>
public sealed class WorkHoursCalendar : IWorkHoursCalendar
{
    private readonly Dictionary<DateOnly, bool> overrides;
    public WorkHoursCalendar()
    {
        using var source = typeof(WorkHoursCalendar).Assembly.GetManifestResourceStream("WorkHoursCalendar.json")!;
        using var data = JsonDocument.Parse(source);
        overrides = data.RootElement.GetProperty("holidayCalendars").EnumerateArray().SelectMany(x => x.GetProperty("days").EnumerateArray())
            .GroupBy(x => DateOnly.ParseExact(x.GetProperty("date").GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(x => x.Key, x => !x.Last().GetProperty("isOffDay").GetBoolean());
    }
    public bool IsWorkingDay(DateOnly date) => overrides.TryGetValue(date, out var working) ? working : date.DayOfWeek != DayOfWeek.Sunday;
}

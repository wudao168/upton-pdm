using System.Globalization;

namespace Upton.Pdm.Api;

public static class AiClockContext
{
    public static string Describe(DateTimeOffset utc)
    {
        var local = utc.ToOffset(TimeSpan.FromHours(8));
        var weekday = new[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" }[(int)local.DayOfWeek];
        return $"本轮可信服务器时间：{local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}，{weekday}，Asia/Shanghai（北京时间，UTC+08:00）。今天、昨天、明天等相对日期以本轮时间为准，不沿用历史消息中的日期。日期和星期可以直接据此回答，无需联网或业务查询工具。";
    }
}

using System;
using System.Globalization;
using System.Text.Json;

namespace LiteMonitor.src.Plugins.Native
{
    /// <summary>
    /// 在节假日接口不可用时，按本机日期计算元旦倒计时，不推测调休安排。
    /// </summary>
    public static class NewYearNative
    {
        public static string GetCountdownJson(DateTime today)
        {
            today = today.Date;
            var newYear = new DateTime(today.Year, 1, 1);
            if (newYear < today) newYear = newYear.AddYears(1);

            return JsonSerializer.Serialize(new
            {
                name = "元旦",
                date = newYear.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rest = (newYear - today).Days
            });
        }
    }
}

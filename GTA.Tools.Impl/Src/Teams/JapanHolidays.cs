
namespace GTA.Tools.Impl.Teams;

/// <summary>
/// Japanese public holidays, calculated by the law (no web call, no table to keep up to date).
/// Valid for the years 2022 to 2099. Outside this range nothing is reported as a holiday.
/// Includes substitute holidays (Sunday holiday -> next free day) and citizens' holidays (a day between two holidays).
/// It does NOT know company holidays (year-end / New Year break, Obon, and so on). Use skip_dates for those.
/// </summary>
internal static class JapanHolidays
{
    public const int FirstYear = 2022;
    public const int LastYear = 2099;

    private static readonly Dictionary<int, Dictionary<DateTime, string>> Cache = new();
    private static readonly object Gate = new();

    /// <summary>True when <paramref name="date"/> is a public holiday in Japan. <paramref name="name"/> is the Japanese name.</summary>
    public static bool TryGetName(DateTime date, out string name)
    {
        name = "";
        int year = date.Year;
        if (year < FirstYear || year > LastYear) return false;

        Dictionary<DateTime, string>? map;
        lock (Gate)
        {
            if (!Cache.TryGetValue(year, out map))
            {
                map = Build(year);
                Cache[year] = map;
            }
        }
        return map.TryGetValue(date.Date, out name!);
    }

    /// <summary>All holidays of one year (exposed for the smoke test).</summary>
    public static IReadOnlyDictionary<DateTime, string> ForYear(int year)
    {
        lock (Gate)
        {
            if (!Cache.TryGetValue(year, out var map))
            {
                map = Build(year);
                Cache[year] = map;
            }
            return new SortedDictionary<DateTime, string>(map);
        }
    }

    private static Dictionary<DateTime, string> Build(int y)
    {
        var map = new Dictionary<DateTime, string>
        {
            [new DateTime(y, 1, 1)] = "元日",
            [NthMonday(y, 1, 2)] = "成人の日",
            [new DateTime(y, 2, 11)] = "建国記念の日",
            [new DateTime(y, 2, 23)] = "天皇誕生日",
            [new DateTime(y, 3, VernalEquinoxDay(y))] = "春分の日",
            [new DateTime(y, 4, 29)] = "昭和の日",
            [new DateTime(y, 5, 3)] = "憲法記念日",
            [new DateTime(y, 5, 4)] = "みどりの日",
            [new DateTime(y, 5, 5)] = "こどもの日",
            [NthMonday(y, 7, 3)] = "海の日",
            [new DateTime(y, 8, 11)] = "山の日",
            [NthMonday(y, 9, 3)] = "敬老の日",
            [new DateTime(y, 9, AutumnalEquinoxDay(y))] = "秋分の日",
            [NthMonday(y, 10, 2)] = "スポーツの日",
            [new DateTime(y, 11, 3)] = "文化の日",
            [new DateTime(y, 11, 23)] = "勤労感謝の日",
        };

        // Citizens' holiday: a normal day (not Sunday) between two holidays.
        for (DateTime d = new DateTime(y, 1, 2); d < new DateTime(y, 12, 31); d = d.AddDays(1))
        {
            if (map.ContainsKey(d) || d.DayOfWeek == DayOfWeek.Sunday) continue;
            if (map.ContainsKey(d.AddDays(-1)) && map.ContainsKey(d.AddDays(1)))
                map[d] = "国民の休日";
        }

        // Substitute holiday: a holiday on Sunday moves to the next day that is not a holiday.
        foreach (DateTime holiday in map.Keys.Where(k => k.DayOfWeek == DayOfWeek.Sunday).ToList())
        {
            DateTime substitute = holiday.AddDays(1);
            while (map.ContainsKey(substitute)) substitute = substitute.AddDays(1);
            map[substitute] = "振替休日";
        }
        return map;
    }

    private static DateTime NthMonday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1);
        int offset = ((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    // Standard approximation formulas, exact for 1980..2099.
    private static int VernalEquinoxDay(int y) => (int)(20.8431 + 0.242194 * (y - 1980) - (int)((y - 1980) / 4.0));

    private static int AutumnalEquinoxDay(int y) => (int)(23.2488 + 0.242194 * (y - 1980) - (int)((y - 1980) / 4.0));
}

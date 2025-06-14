using Microsoft.SemanticKernel;
using System;
using System.ComponentModel;
using System.Globalization;

namespace MovieTracker.Backend.Prompts
{
    public class DateTimeKernelFunctions
    {
        // ---------- POINTS ----------
        [KernelFunction, Description("Today in ISO format (YYYY-MM-DD)")]
        public static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

        [KernelFunction, Description("This month (first day) in ISO format (YYYY-MM)")]
        public static string ThisMonth() => DateTime.UtcNow.ToString("yyyy-MM");

        [KernelFunction, Description("This year in ISO format (YYYY)")]
        public static string ThisYear() => DateTime.UtcNow.Year.ToString();

        // ---------- RANGES ----------
        [KernelFunction]
        [Description("ISO-8601 interval for the past <years> full years up to today. " +
                     "Example: PastYearsRange(3) → 2022-06-13/2025-06-13")]
        public static string PastYearsRange(
            [Description("Number of years to look back (e.g. 3 = last three years)")]
            int years)
        {
            var end = DateTime.UtcNow.Date;                 // inclusive
            var start = end.AddYears(-years);
            return $"{start:yyyy-MM-dd}/{end:yyyy-MM-dd}";
        }

        [KernelFunction]
        [Description("ISO-8601 interval for the past <months> full months up to today. " +
                     "Example: PastMonthsRange(6) → 2024-12-13/2025-06-13")]
        public static string PastMonthsRange(int months)
        {
            var end = DateTime.UtcNow.Date;
            var start = end.AddMonths(-months);
            return $"{start:yyyy-MM-dd}/{end:yyyy-MM-dd}";
        }

        [KernelFunction]
        [Description("ISO-8601 interval for the past <days> days up to today.")]
        public static string PastDaysRange(int days)
        {
            var end = DateTime.UtcNow.Date;
            var start = end.AddDays(-days);
            return $"{start:yyyy-MM-dd}/{end:yyyy-MM-dd}";
        }

        // ---------- GENERIC ----------
        [KernelFunction]
        [Description("Offset any ISO date (YYYY-MM-DD) by N units. Units = d, m, y. " +
                     "Example: OffsetDate(\"2022-05-20\", 10, \"d\")")]
        public static string OffsetDate(
            string isoDate,
            int amount,
            [Description("Unit (d=days, m=months, y=years)")] string unit)
        {
            var dt = DateTime.Parse(isoDate, CultureInfo.InvariantCulture);
            var shifted = unit switch
            {
                "d" => dt.AddDays(amount),
                "m" => dt.AddMonths(amount),
                "y" => dt.AddYears(amount),
                _   => throw new ArgumentException("unit must be d, m, or y")
            };
            return shifted.ToString("yyyy-MM-dd");
        }
    }
}
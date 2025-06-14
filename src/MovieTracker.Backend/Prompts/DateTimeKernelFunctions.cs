using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace MovieTracker.Backend.Prompts;

public class DateTimeKernelFunctions
{
    [KernelFunction]
    [Description("Gets a year relative to the current year. 0 = current year, 1 = last year, 2 = two years ago, -1 = next year")]
    [return: Description("The calculated year")]
    public string Year(int yearsFromCurrent)
    {
        var targetYear = DateTime.Now.Year - yearsFromCurrent;
        return targetYear.ToString();
    }

    [KernelFunction]
    [Description("Gets a month relative to the current month. 0 = current month, 1 = last month, 2 = two months ago, -1 = next month")]
    [return: Description("The calculated month and year in YYYY-MM format")]
    public string Month(int monthsFromCurrent)
    {
        var targetDate = DateTime.Now.AddMonths(-monthsFromCurrent);
        return targetDate.ToString("yyyy-MM");
    }

    [KernelFunction]
    [Description("Gets a day relative to today. 0 = today, 1 = yesterday, 2 = two days ago, -1 = tomorrow")]
    [return: Description("The calculated date in YYYY-MM-DD format")]
    public string Day(int daysFromCurrent)
    {
        var targetDate = DateTime.Now.AddDays(-daysFromCurrent);
        return targetDate.ToString("yyyy-MM-dd");
    }
}
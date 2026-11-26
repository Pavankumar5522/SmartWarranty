using SmartWarranty_2.Models;

namespace SmartWarranty_2.Services;

public static class WarrantyStatusService
{
    public const int ExpiringSoonThresholdDays = 30;

    public static string GetStatus(Warranty warranty)
    {
        var today = DateTime.UtcNow.Date;
        var startDate = warranty.StartDate.Date;
        var expiryDate = warranty.ExpiryDate.Date;

        if (expiryDate < today)
        {
            return "Expired";
        }

        if (startDate > today)
        {
            return "Not Started";
        }

        if (expiryDate <= today.AddDays(ExpiringSoonThresholdDays))
        {
            return "Expiring Soon";
        }

        return "Active";
    }

    public static bool IsEligible(Warranty warranty)
    {
        var today = DateTime.UtcNow.Date;
        return warranty.StartDate.Date <= today && warranty.ExpiryDate.Date >= today;
    }

    public static string GetTimeRemainingLabel(Warranty warranty)
    {
        var today = DateTime.UtcNow.Date;
        var startDate = warranty.StartDate.Date;
        var expiryDate = warranty.ExpiryDate.Date;

        if (expiryDate < today)
        {
            return FormatDayCount((today - expiryDate).Days, "Expired ", " ago");
        }

        if (startDate > today)
        {
            return FormatDayCount((startDate - today).Days, "Starts in ", string.Empty);
        }

        var daysRemaining = (expiryDate - today).Days;
        return daysRemaining == 0
            ? "Expires today"
            : FormatDayCount(daysRemaining, string.Empty, " left");
    }

    private static string FormatDayCount(int days, string prefix, string suffix)
    {
        var unit = days == 1 ? "day" : "days";
        return $"{prefix}{days} {unit}{suffix}";
    }
}

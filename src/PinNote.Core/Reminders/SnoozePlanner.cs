namespace PinNote.Core.Reminders;

public enum SnoozePreset
{
    FiveMinutes,
    ThirtyMinutes,
    OneHour,
    TomorrowMorning
}

public static class SnoozePlanner
{
    public static DateTimeOffset GetDue(SnoozePreset preset, DateTimeOffset now) => preset switch
    {
        SnoozePreset.FiveMinutes => now.AddMinutes(5),
        SnoozePreset.ThirtyMinutes => now.AddMinutes(30),
        SnoozePreset.OneHour => now.AddHours(1),
        SnoozePreset.TomorrowMorning => TomorrowMorning(now),
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    private static DateTimeOffset TomorrowMorning(DateTimeOffset now)
    {
        var local = now.LocalDateTime.Date.AddDays(1).AddHours(9);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}

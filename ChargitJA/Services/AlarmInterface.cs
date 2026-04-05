namespace ChargitJA.Services;

internal static class AlarmInterface
{
	public static void SetAlarm(DateTime alarmTime, string content)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(content);

#if ANDROID
		var alarmIntent = new Android.Content.Intent(Android.Provider.AlarmClock.ActionSetAlarm)
			.PutExtra(Android.Provider.AlarmClock.ExtraHour, alarmTime.Hour)
			.PutExtra(Android.Provider.AlarmClock.ExtraMinutes, alarmTime.Minute + 1)
			.PutExtra(Android.Provider.AlarmClock.ExtraMessage, content)
			.PutExtra(Android.Provider.AlarmClock.ExtraSkipUi, true)
			.AddFlags(Android.Content.ActivityFlags.NewTask);

		Android.App.Application.Context.StartActivity(alarmIntent);
#elif IOS
		var notificationContent = new UserNotifications.UNMutableNotificationContent
		{
			Title = "Battery Alarm",
			Body = content,
			Sound = UserNotifications.UNNotificationSound.Default
		};

		var triggerDate = new Foundation.NSDateComponents
		{
			Hour = alarmTime.Hour,
			Minute = alarmTime.Minute,
			Second = alarmTime.Second
		};

		var trigger = UserNotifications.UNCalendarNotificationTrigger.CreateTrigger(triggerDate, false);
		var request = UserNotifications.UNNotificationRequest.FromIdentifier($"battery-alarm-{Guid.NewGuid():N}", notificationContent, trigger);

		var center = UserNotifications.UNUserNotificationCenter.Current;
		center.RequestAuthorization(
			UserNotifications.UNAuthorizationOptions.Alert | UserNotifications.UNAuthorizationOptions.Sound,
			(granted, error) =>
			{
				if (!granted || error is not null)
				{
					return;
				}

				center.AddNotificationRequest(request, null);
			});
#else
		throw new PlatformNotSupportedException("Alarm scheduling is only available on Android and iOS.");
#endif
	}
}

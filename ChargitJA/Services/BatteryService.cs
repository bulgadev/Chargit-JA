using Microsoft.Extensions.Logging;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

using StackExchange.Redis;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ChargitJA.Services;

internal sealed class BatteryService : INotifyPropertyChanged, IDisposable
{
  private static TimeOnly ScheduledAlarmTime;
	UserSettings? currentSettings;
	private int MinimumAlarmBatteryPercentage;
	private const string RedisConnectionString = "192.168.0.133:6379";
	private const string GuestUsername = "bulga";
	private const string GuestEmail = "guest@example.com";
	private const string GuestUserId = "0";
	private const string SettingsFileName = "settings.json";

	private readonly ILogger<BatteryService> _logger;
    private readonly UserSessions _userSessions;
    private readonly ConnectionMultiplexer? _redisConnection;
	private readonly IDatabase? _database;
	private readonly string _deviceName;
    private readonly string _deviceId;
	private readonly string _createdAt;
  private readonly Timer _alarmCheckTimer;
	private int _lastPushedBatteryPercentage = -1;
	private DateOnly? _lastAlarmDate;

	private double _batteryLevel;
	private string _batteryStatus = string.Empty;
	private IReadOnlyList<UserDevice> _devices = Array.Empty<UserDevice>();

 public BatteryService(ILogger<BatteryService> logger, UserSessions userSessions)
	{
        _logger = logger;
      _userSessions = userSessions;
		_deviceName = DeviceInfo.Current.Name;
		var normalizedDeviceId = new string(_deviceName
			.ToLowerInvariant()
			.Select(character => char.IsLetterOrDigit(character) ? character : '_')
			.ToArray())
			.Trim('_');
		_deviceId = $"{normalizedDeviceId}_id";
		_createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd");

       try
		{
			var configurationOptions = ConfigurationOptions.Parse(RedisConnectionString);
			configurationOptions.AbortOnConnectFail = false;
			configurationOptions.ConnectTimeout = 1000;
			configurationOptions.SyncTimeout = 1000;
			_redisConnection = ConnectionMultiplexer.Connect(configurationOptions);
			_database = _redisConnection.GetDatabase();
		}
		catch (RedisConnectionException exception)
		{
			_logger.LogWarning(exception, "Redis is not reachable at startup. Battery sync will run locally only.");
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Redis initialization failed. Battery sync will run locally only.");
		}

		UpdateBatteryInfo(pushToRedis: true);
		Battery.Default.BatteryInfoChanged += OnBatteryInfoChanged;
       _userSessions.PropertyChanged += OnUserSessionChanged;
		_alarmCheckTimer = new Timer(_ => CheckAndTriggerAlarm(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

		currentSettings = GetSettings();
      MinimumAlarmBatteryPercentage = GetConfiguredMinimumBatteryPercentage();
		ScheduledAlarmTime = GetConfiguredAlarmTime(currentSettings);
	}

	public double BatteryLevel
	{
		get => _batteryLevel;
		private set => SetProperty(ref _batteryLevel, value);
	}

	public string BatteryStatus
	{
		get => _batteryStatus;
		private set => SetProperty(ref _batteryStatus, value);
	}

	public IReadOnlyList<UserDevice> Devices
	{
		get => _devices;
		private set => SetProperty(ref _devices, value);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public void Dispose()
	{
		Battery.Default.BatteryInfoChanged -= OnBatteryInfoChanged;
       _userSessions.PropertyChanged -= OnUserSessionChanged;
		_alarmCheckTimer.Dispose();
     _redisConnection?.Dispose();
	}

	public void TriggerAlarmCheckForDebug()
	{
		CheckAndTriggerAlarm(forceAlarm: true);
	}

	private void OnBatteryInfoChanged(object? sender, BatteryInfoChangedEventArgs e)
	{
        UpdateBatteryInfo();
	}

	private void OnUserSessionChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(UserSessions.IsAuthenticated)
			or nameof(UserSessions.Username)
			or nameof(UserSessions.Email)
			or nameof(UserSessions.UserId))
		{
			UpdateBatteryInfo(pushToRedis: true);
		}
	}

	private class UserSettings
	{
       public TimeOnly? bedtime { get; set; }
		public int? minBattery { get; set; }
		public TimeOnly? bedtimeJ { get; set; }
		public int? minBatteryJ { get; set; }
	}

	private UserSettings? GetSettings()
	{
      var settingsPath = Path.Combine(FileSystem.AppDataDirectory, SettingsFileName);
		if (!File.Exists(settingsPath))
		{
			return null;
		}

		try
		{
			var jsonString = File.ReadAllText(settingsPath);
			if (string.IsNullOrWhiteSpace(jsonString))
			{
				return null;
			}

			return JsonSerializer.Deserialize<UserSettings>(jsonString);
		}
		catch (Exception exception) when (exception is JsonException or IOException)
		{
			_logger.LogWarning(exception, "Failed to read settings from {SettingsPath}. Using defaults.", settingsPath);
			return null;
		}
	}

	private void CheckAndTriggerAlarm(bool forceAlarm = false)
	{
		UpdateBatteryInfo();
		 
		var now = DateTime.Now;
		if (!forceAlarm)
		{
			if (now.Hour != ScheduledAlarmTime.Hour || now.Minute != ScheduledAlarmTime.Minute)
			{
				return;
			}

			var currentDate = DateOnly.FromDateTime(now);
			if (_lastAlarmDate == currentDate)
			{
				return;
			}
		}

		var batteryPercentage = (int)Math.Round(BatteryLevel * 100, MidpointRounding.AwayFromZero);
		if (batteryPercentage >= MinimumAlarmBatteryPercentage)
		{
			return;
		}

		var alarmTime = forceAlarm
			? DateTime.Now.AddSeconds(2)
			: now.Date + ScheduledAlarmTime.ToTimeSpan();

		AlarmInterface.SetAlarm(alarmTime, $"Your {_deviceName} is not charging!");

		if (!forceAlarm)
		{
			_lastAlarmDate = DateOnly.FromDateTime(now);
		}
	}

    private void UpdateBatteryInfo(bool pushToRedis = false)
	{
		BatteryLevel = Battery.Default.ChargeLevel;
		BatteryStatus = Battery.Default.State.ToString();

		if (_database is null)
		{
			Devices = Array.Empty<UserDevice>();
			return;
		}

		var batteryPercentage = (int)Math.Round(BatteryLevel * 100, MidpointRounding.AwayFromZero);
      try
		{
          if (pushToRedis || _lastPushedBatteryPercentage == -1 || batteryPercentage < _lastPushedBatteryPercentage)
			{
				PushBatteryToRedis(batteryPercentage);
			}

			LoadDevicesFromRedis();
		}
		catch (RedisException exception)
		{
			_logger.LogWarning(exception, "Redis operation failed while updating battery info.");
			Devices = Array.Empty<UserDevice>();
		}
	}

  private void PushBatteryToRedis(int batteryPercentage)
	{
      EnsureUserDocumentExists();

     var nowUtc = DateTime.UtcNow.ToString("O");
		var devicePath = $"$.devices[?(@.id=='{_deviceId}')]";
		var existingDevice = _database.Execute("JSON.GET", RedisKey, devicePath).ToString();

        if (string.IsNullOrWhiteSpace(existingDevice) || existingDevice == "[]")
		{
            var devicePayload = JsonSerializer.Serialize(new
			{
				id = _deviceId,
				name = _deviceName,
				battery = batteryPercentage,
				status = BatteryStatus,
				last_sync = nowUtc
			});

			_database.Execute("JSON.ARRAPPEND", RedisKey, "$.devices", devicePayload);
		}
		else
		{
           _database.Execute("JSON.SET", RedisKey, $"{devicePath}.name", JsonSerializer.Serialize(_deviceName));
			_database.Execute("JSON.SET", RedisKey, $"{devicePath}.battery", JsonSerializer.Serialize(batteryPercentage));
			_database.Execute("JSON.SET", RedisKey, $"{devicePath}.status", JsonSerializer.Serialize(BatteryStatus));
			_database.Execute("JSON.SET", RedisKey, $"{devicePath}.last_sync", JsonSerializer.Serialize(nowUtc));
		}

		_lastPushedBatteryPercentage = batteryPercentage;
		_logger.LogDebug("Battery level synced to Redis: {BatteryPercentage}%", batteryPercentage);
	}

	private void LoadDevicesFromRedis()
	{
		EnsureUserDocumentExists();

		var json = _database.Execute("JSON.GET", RedisKey, "$.devices").ToString();
		if (string.IsNullOrWhiteSpace(json) || json == "[]")
		{
			Devices = Array.Empty<UserDevice>();
			return;
		}

		try
		{
			using var document = JsonDocument.Parse(json);
			var devicesElement = GetDevicesElement(document.RootElement);
			if (devicesElement is null || devicesElement.Value.ValueKind is not JsonValueKind.Array)
			{
				Devices = Array.Empty<UserDevice>();
				return;
			}

			var devices = new List<UserDevice>();
			foreach (var device in devicesElement.Value.EnumerateArray())
			{
				if (device.ValueKind is not JsonValueKind.Object)
				{
					continue;
				}

				var id = TryGetString(device, "id") ?? string.Empty;
				var name = TryGetString(device, "name") ?? string.Empty;
				var status = TryGetString(device, "status") ?? string.Empty;
				var lastSync = TryGetString(device, "last_sync");
				var battery = TryGetInt(device, "battery");

				if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
				{
					continue;
				}

				devices.Add(new UserDevice(id, name, battery, status, lastSync));
			}

			Devices = devices;
		}
		catch (JsonException exception)
		{
			_logger.LogWarning(exception, "Could not parse devices JSON from Redis for key {RedisKey}.", RedisKey);
			Devices = Array.Empty<UserDevice>();
		}
	}

	private void EnsureUserDocumentExists()
	{
      if (_database is null)
		{
			return;
		}

      var username = GetSessionUsername();
		var email = GetSessionEmail();
		var userId = GetSessionUserId();
		var redisKey = $"user:{username}";

        if (_database.KeyExists(redisKey))
		{
         _database.Execute("JSON.SET", redisKey, "$.user_info.username", JsonSerializer.Serialize(username));
			_database.Execute("JSON.SET", redisKey, "$.user_info.email", JsonSerializer.Serialize(email));
			_database.Execute("JSON.SET", redisKey, "$.user_info.id", JsonSerializer.Serialize(userId));
			return;
		}

		var payload = JsonSerializer.Serialize(new
		{
			user_info = new
			{
                username = username,
				email = email,
				created_at = _createdAt,
                id = userId
			},
			devices = Array.Empty<object>(),
			settings = new
			{
				theme = "dark",
				notifications = true
			}
		});

        _database.Execute("JSON.SET", redisKey, "$", payload, "NX");
	}

	private string RedisKey => $"user:{GetSessionUsername()}";

	private int GetConfiguredMinimumBatteryPercentage() =>
		Math.Clamp(currentSettings?.minBattery ?? currentSettings?.minBatteryJ ?? 20, 1, 100);

	private static TimeOnly GetConfiguredAlarmTime(UserSettings? settings)
	{
		var configuredTime = settings?.bedtime ?? settings?.bedtimeJ;
		return configuredTime ?? new TimeOnly(22, 0);
	}

	private string GetSessionUsername() =>
		string.IsNullOrWhiteSpace(_userSessions.Username) ? GuestUsername : _userSessions.Username;

	private string GetSessionEmail() =>
		string.IsNullOrWhiteSpace(_userSessions.Email) ? GuestEmail : _userSessions.Email;

	private string GetSessionUserId() =>
		string.IsNullOrWhiteSpace(_userSessions.UserId) ? GuestUserId : _userSessions.UserId;

	private static JsonElement? GetDevicesElement(JsonElement root)
	{
		if (root.ValueKind is JsonValueKind.Array)
		{
			if (root.GetArrayLength() is 0)
			{
				return null;
			}

			var first = root[0];
			if (first.ValueKind is JsonValueKind.Array)
			{
				return first;
			}

			if (first.ValueKind is JsonValueKind.Object && first.TryGetProperty("devices", out var nestedDevices))
			{
				return nestedDevices;
			}

			return root;
		}

		if (root.ValueKind is JsonValueKind.Object && root.TryGetProperty("devices", out var devices))
		{
			return devices;
		}

		return null;
	}

	private static string? TryGetString(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var value))
		{
			return null;
		}

		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString(),
			JsonValueKind.Number => value.GetRawText(),
			JsonValueKind.True => bool.TrueString,
			JsonValueKind.False => bool.FalseString,
			_ => null
		};
	}

	private static int TryGetInt(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var value))
		{
			return 0;
		}

		if (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var intValue))
		{
			return intValue;
		}

		if (value.ValueKind is JsonValueKind.String && int.TryParse(value.GetString(), out var parsedValue))
		{
			return parsedValue;
		}

		return 0;
	}

	private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
	{
		if (Equals(storage, value))
		{
			return;
		}

		storage = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

internal sealed record UserDevice(string Id, string Name, int Battery, string Status, string? LastSync);

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Devices;
using StackExchange.Redis;

namespace ChargitJA.Services;

internal sealed class BatteryService : INotifyPropertyChanged, IDisposable
{
  private const string RedisConnectionString = "192.168.100.86:6379";
	private string RedisKey => $"user:{Username}";
	private const string Username = "bulga";
   private const string Email = "marcelo@example.com";
	private const int UserId = 1;

	private readonly ILogger<BatteryService> _logger;
	private readonly ConnectionMultiplexer _redisConnection;
	private readonly IDatabase _database;
	private readonly string _deviceName;
  private readonly string _deviceId;
	private readonly string _createdAt;
	private int _lastPushedBatteryPercentage = -1;

	private double _batteryLevel;
	private string _batteryStatus = string.Empty;

  public BatteryService(ILogger<BatteryService> logger)
	{
        _logger = logger;
		_deviceName = DeviceInfo.Current.Name;
		var normalizedDeviceId = new string(_deviceName
			.ToLowerInvariant()
			.Select(character => char.IsLetterOrDigit(character) ? character : '_')
			.ToArray())
			.Trim('_');
		_deviceId = $"{normalizedDeviceId}_id";
		_createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd");

		var configurationOptions = ConfigurationOptions.Parse(RedisConnectionString);
		configurationOptions.AbortOnConnectFail = false;
		_redisConnection = ConnectionMultiplexer.Connect(configurationOptions);
		_database = _redisConnection.GetDatabase();

		UpdateBatteryInfo(pushToRedis: true);
		Battery.Default.BatteryInfoChanged += OnBatteryInfoChanged;
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

	public event PropertyChangedEventHandler? PropertyChanged;

	public void Dispose()
	{
		Battery.Default.BatteryInfoChanged -= OnBatteryInfoChanged;
      _redisConnection.Dispose();
	}

	private void OnBatteryInfoChanged(object? sender, BatteryInfoChangedEventArgs e)
	{
        UpdateBatteryInfo();
	}

    private void UpdateBatteryInfo(bool pushToRedis = false)
	{
		BatteryLevel = Battery.Default.ChargeLevel;
		BatteryStatus = Battery.Default.State.ToString();

		var batteryPercentage = (int)Math.Round(BatteryLevel * 100, MidpointRounding.AwayFromZero);
		if (pushToRedis || _lastPushedBatteryPercentage == -1 || batteryPercentage < _lastPushedBatteryPercentage)
		{
			PushBatteryToRedis(batteryPercentage);
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

	private void EnsureUserDocumentExists()
	{
		if (_database.KeyExists(RedisKey))
		{
			return;
		}

		var payload = JsonSerializer.Serialize(new
		{
			user_info = new
			{
				username = Username,
				email = Email,
				created_at = _createdAt,
				id = UserId
			},
			devices = Array.Empty<object>(),
			settings = new
			{
				theme = "dark",
				notifications = true
			}
		});

		_database.Execute("JSON.SET", RedisKey, "$", payload, "NX");
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

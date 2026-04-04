using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace ChargitJA.Services;

internal sealed class AuthService
{
	private const string CallbackScheme = "chargitja";
	private const string CallbackPath = "auth-callback";
	private const string RefreshTokenStorageKey = "zitadel_refresh_token";
	private const string Scope = "openid profile email offline_access";

	private readonly HttpClient _httpClient;
	private readonly ILogger<AuthService> _logger;
	private readonly UserSessions _userSessions;
	private ZitadelAuthOptions? _options;

	public AuthService(HttpClient httpClient, ILogger<AuthService> logger, UserSessions userSessions)
	{
		_httpClient = httpClient;
		_logger = logger;
		_userSessions = userSessions;
	}

	public async Task InitializeAsync()
	{
		if (!await TryLoadOptionsAsync().ConfigureAwait(false))
		{
			return;
		}

		var refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenStorageKey).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(refreshToken))
		{
			return;
		}

		await RefreshAndLoadUserAsync(refreshToken).ConfigureAwait(false);
	}

	public async Task LoginAsync()
	{
		await AuthenticateInteractiveAsync(isRegister: false).ConfigureAwait(false);
	}

	public async Task RegisterAsync()
	{
		await AuthenticateInteractiveAsync(isRegister: true).ConfigureAwait(false);
	}

	public async Task LogoutAsync()
	{
		SecureStorage.Default.Remove(RefreshTokenStorageKey);
		_userSessions.Clear();
	}

	private async Task AuthenticateInteractiveAsync(bool isRegister)
	{
		if (!await TryLoadOptionsAsync().ConfigureAwait(false))
		{
			return;
		}

		var pkce = CreatePkcePair();
		var callbackUri = new Uri($"{CallbackScheme}://{CallbackPath}");
		var authorizationUri = BuildAuthorizationUri(pkce.CodeChallenge, isRegister);

			// Log the authorization URI and whether this is a register flow so we can
			// verify that the `screen_hint=signup` parameter is present when requested.
			_logger.LogInformation("Starting interactive auth. isRegister={IsRegister}, AuthorizationUri={AuthorizationUri}, CallbackUri={CallbackUri}",
				isRegister, authorizationUri, callbackUri);

		try
		{
			var authResult = await WebAuthenticator.Default.AuthenticateAsync(authorizationUri, callbackUri).ConfigureAwait(false);
			if (!TryGetProperty(authResult.Properties, "code", out var authorizationCode))
			{
				throw new InvalidOperationException("Authorization code was not returned by the identity provider.");
			}

			var tokenResponse = await ExchangeAuthorizationCodeAsync(authorizationCode, pkce.CodeVerifier).ConfigureAwait(false);
			await CompleteSignInAsync(tokenResponse).ConfigureAwait(false);
		}
		catch (TaskCanceledException)
		{
			_logger.LogInformation("Authentication flow was canceled by the user.");
		}
	}

	private async Task RefreshAndLoadUserAsync(string refreshToken)
	{
		if (_options is null)
		{
			throw new InvalidOperationException("Authentication options are not initialized.");
		}

		var content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["grant_type"] = "refresh_token",
			["refresh_token"] = refreshToken,
			["client_id"] = _options.ClientId,
			["client_secret"] = _options.ClientSecret
		});

		using var response = await _httpClient.PostAsync(_options.TokenEndpoint, content).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			_logger.LogWarning("Refresh token authentication failed with status {StatusCode}.", response.StatusCode);
			SecureStorage.Default.Remove(RefreshTokenStorageKey);
			_userSessions.Clear();
			return;
		}

		var tokenResponse = await ReadTokenResponseAsync(response).ConfigureAwait(false);
		await CompleteSignInAsync(tokenResponse).ConfigureAwait(false);
	}

	private async Task CompleteSignInAsync(TokenResponse tokenResponse)
	{
		if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
		{
			throw new InvalidOperationException("Access token was not returned.");
		}

		if (!string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
		{
			await SecureStorage.Default.SetAsync(RefreshTokenStorageKey, tokenResponse.RefreshToken).ConfigureAwait(false);
		}

		var userInfo = await GetUserInfoAsync(tokenResponse.AccessToken).ConfigureAwait(false);
		_userSessions.SetAuthenticatedUser(userInfo.Id, userInfo.Username, userInfo.Email);
	}

	private async Task<TokenResponse> ExchangeAuthorizationCodeAsync(string code, string codeVerifier)
	{
		if (_options is null)
		{
			throw new InvalidOperationException("Authentication options are not initialized.");
		}

		var content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = code,
			["redirect_uri"] = $"{CallbackScheme}://{CallbackPath}",
			//["redirect_uri"] = $"http://localhost:3000/api/auth/callback/zitadel",
			["client_id"] = _options.ClientId,
			["client_secret"] = _options.ClientSecret,
			["code_verifier"] = codeVerifier
		});

		using var response = await _httpClient.PostAsync(_options.TokenEndpoint, content).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		return await ReadTokenResponseAsync(response).ConfigureAwait(false);
	}

	private async Task<UserInfo> GetUserInfoAsync(string accessToken)
	{
		if (_options is null)
		{
			throw new InvalidOperationException("Authentication options are not initialized.");
		}

		using var request = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoEndpoint);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();

		await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
		var root = document.RootElement;

		var id = TryGetString(root, "sub") ?? Guid.NewGuid().ToString("N");
		var username = TryGetString(root, "preferred_username") ?? TryGetString(root, "name") ?? "unknown";
		var email = TryGetString(root, "email") ?? "unknown@example.com";

		return new UserInfo(id, username, email);
	}

	private Uri BuildAuthorizationUri(string codeChallenge, bool isRegister)
	{
		if (_options is null)
		{
			throw new InvalidOperationException("Authentication options are not initialized.");
		}

		var query = new List<string>
		{
			$"client_id={Uri.EscapeDataString(_options.ClientId)}",
			"response_type=code",
			$"redirect_uri={Uri.EscapeDataString($"{CallbackScheme}://{CallbackPath}")}",
			$"scope={Uri.EscapeDataString(Scope)}",
			$"code_challenge={Uri.EscapeDataString(codeChallenge)}",
			"code_challenge_method=S256"
		};

		if (isRegister)
		{
			query.Add("screen_hint=signup");
           query.Add("prompt=create");
		}

		return new Uri($"{_options.AuthorizeEndpoint}?{string.Join("&", query)}");
	}

	private async Task<bool> TryLoadOptionsAsync()
	{
		if (_options is not null)
		{
			return true;
		}

		var values = await LoadSettingsAsync().ConfigureAwait(false);
		var hasValues = ZitadelAuthOptions.TryCreate(values, out var options, out var missingKeys);
		if (!hasValues || options is null)
		{
			_logger.LogWarning("Authentication is not configured. Missing env keys: {MissingKeys}", string.Join(", ", missingKeys));
			return false;
		}

		_options = options;
		return true;
	}

	private static async Task<Dictionary<string, string>> LoadSettingsAsync()
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var key in ZitadelAuthOptions.RequiredKeys)
		{
			var envValue = Environment.GetEnvironmentVariable(key);
			if (!string.IsNullOrWhiteSpace(envValue))
			{
				values[key] = envValue;
			}
		}

		try
		{
            await using var stream = await FileSystem.Current.OpenAppPackageFileAsync("env.config").ConfigureAwait(false);
			using var reader = new StreamReader(stream);
			while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
				{
					continue;
				}

				var separatorIndex = line.IndexOf('=');
				if (separatorIndex <= 0)
				{
					continue;
				}

				var key = line[..separatorIndex].Trim();
				if (values.ContainsKey(key))
				{
					continue;
				}

				var rawValue = line[(separatorIndex + 1)..].Trim();
				values[key] = rawValue.Trim('"');
			}
		}
		catch (FileNotFoundException)
		{
		}
		catch (DirectoryNotFoundException)
		{
		}

		return values;
	}

   private static PkcePair CreatePkcePair()
	{
		var verifierBytes = RandomNumberGenerator.GetBytes(32);
		var verifier = ToBase64Url(verifierBytes);
		using var sha = SHA256.Create();
		var challenge = ToBase64Url(sha.ComputeHash(Encoding.UTF8.GetBytes(verifier)));
      return new PkcePair(challenge, verifier);
	}

	private static string ToBase64Url(byte[] input)
	{
		return Convert.ToBase64String(input)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private static bool TryGetProperty(IDictionary<string, string> values, string key, out string value)
	{
		if (values.TryGetValue(key, out value!))
		{
			return true;
		}

		var match = values.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(match.Value))
		{
			value = match.Value;
			return true;
		}

		value = string.Empty;
		return false;
	}

	private static string? TryGetString(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
		{
			return null;
		}

		return property.GetString();
	}

	private readonly record struct UserInfo(string Id, string Username, string Email);
	private readonly record struct PkcePair(string CodeChallenge, string CodeVerifier);

   private readonly record struct TokenResponse(string AccessToken, string RefreshToken);

	private sealed record ZitadelAuthOptions(
		string AuthorizeEndpoint,
		string TokenEndpoint,
		string UserInfoEndpoint,
		string ClientId,
		string ClientSecret)
	{
		public static readonly string[] RequiredKeys =
		[
			"AUTO_ENDPOINT",
			"TOKEN_ENDPOINT",
			"USERINFO_ENDPOINT",
			"ZITADEL_CLIENT_ID",
			"ZITADEL_CLIENT_SECRET"
		];

		public static bool TryCreate(Dictionary<string, string> values, out ZitadelAuthOptions? options, out List<string> missingKeys)
		{
			missingKeys = RequiredKeys.Where(key => !values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).ToList();
			if (missingKeys.Count > 0)
			{
				options = null;
				return false;
			}

			options = new ZitadelAuthOptions(
				values["AUTO_ENDPOINT"],
				values["TOKEN_ENDPOINT"],
				values["USERINFO_ENDPOINT"],
				values["ZITADEL_CLIENT_ID"],
				values["ZITADEL_CLIENT_SECRET"]);
			return true;
		}
	}

	private static async Task<TokenResponse> ReadTokenResponseAsync(HttpResponseMessage response)
	{
		await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
		var root = document.RootElement;
		return new TokenResponse(
			TryGetString(root, "access_token") ?? string.Empty,
			TryGetString(root, "refresh_token") ?? string.Empty);
	}
}

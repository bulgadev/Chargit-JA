using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChargitJA.Services;

internal sealed class UserSessions : INotifyPropertyChanged
{
	private bool _isAuthenticated;
	private string? _userId;
	private string? _username;
	private string? _email;

	public bool IsAuthenticated
	{
		get => _isAuthenticated;
		private set => SetProperty(ref _isAuthenticated, value);
	}

	public string? UserId
	{
		get => _userId;
		private set => SetProperty(ref _userId, value);
	}

	public string? Username
	{
		get => _username;
		private set => SetProperty(ref _username, value);
	}

	public string? Email
	{
		get => _email;
		private set => SetProperty(ref _email, value);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public void SetAuthenticatedUser(string userId, string username, string email)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(userId);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentException.ThrowIfNullOrWhiteSpace(email);

		UserId = userId;
		Username = username;
		Email = email;
		IsAuthenticated = true;
	}

	public void Clear()
	{
		UserId = null;
		Username = null;
		Email = null;
		IsAuthenticated = false;
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

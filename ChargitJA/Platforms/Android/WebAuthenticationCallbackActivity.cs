using Android.App;
using Android.Content;
using Microsoft.Maui.Authentication;

namespace ChargitJA;

[Activity(NoHistory = true, Exported = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
[IntentFilter(
	[Intent.ActionView],
	Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
	DataScheme = "chargitja",
	DataHost = "auth-callback")]
internal sealed class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}

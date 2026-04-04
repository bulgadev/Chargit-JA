using Microsoft.Extensions.Logging;
using ChargitJA.Services;
using Plugin.LocalNotification;
using Microsoft.Extensions.DependencyInjection;

namespace ChargitJA
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseLocalNotification() //Notification plugin thing
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
			builder.Services.AddSingleton<BatteryService>();

#if DEBUG
			builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            var app = builder.Build();
            _ = app.Services.GetRequiredService<BatteryService>();

            return app;
        }
    }
}

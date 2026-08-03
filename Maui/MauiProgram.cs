using Microsoft.Extensions.Logging;
using MealApp.Services;

namespace MealApp.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		builder.Services.AddHttpClient();

		builder.Services.AddScoped<LocalStorageService>();
		builder.Services.AddScoped<LocalizationService>();
		builder.Services.AddScoped<AuthService>();
		builder.Services.AddScoped<SupabaseRestClient>();
		builder.Services.AddScoped<AppState>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

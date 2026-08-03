using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MealApp;
using MealApp.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddHttpClient();

builder.Services.AddScoped<LocalStorageService>();
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<SupabaseRestClient>();
builder.Services.AddScoped<AppState>();

await builder.Build().RunAsync();

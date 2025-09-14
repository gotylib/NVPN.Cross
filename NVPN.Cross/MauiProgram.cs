using Microsoft.Extensions.Logging;
using NVPN.Cross.BL.Services;
using NVPN.Cross.BL.Services.Interfaces;
using NVPN.Cross.Dal;


// using MudBlazor.Services;

namespace NVPN.Cross
{
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

            // Регистрируем DbContext в DI контейнере
            builder.Services.AddDbContext<VpnDbContext>();
            
            // builder.Services.AddMudServices(); // Временно отключено

            builder.Services.AddScoped<IVlessConfigService, VlessConfigService>();

#if WINDOWS
            builder.Services.AddScoped<IVpnConnectService, Platforms.Windows.WindowsServices.WindowsVpnConnectService>();
#endif
#if ANDROID
            builder.Services.AddScoped<IVpnConnectService, Platforms.Android.Services.AndroidVpnConnectService>();
#endif

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            var app = builder.Build();
            
            // Инициализируем базу данных после создания приложения
            Task.Run(async () =>
            {
                try
                {
                    using var scope = app.Services.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<VpnDbContext>();
                    await dbContext.Database.EnsureCreatedAsync();
                }
                catch (Exception ex)
                {
                    // Логируем ошибку, но не падаем
                    System.Diagnostics.Debug.WriteLine($"Database initialization error: {ex.Message}");
                }
            });

            return app;
        }
    }
}

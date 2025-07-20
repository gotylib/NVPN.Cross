using Microsoft.Extensions.Logging;
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

            var dbContext = new VpnDbContext();
            dbContext.Database.EnsureCreated();
            // builder.Services.AddMudServices(); // Временно отключено

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

            return builder.Build();
        }
    }
}

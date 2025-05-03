using Microsoft.Extensions.Logging;
using NVPN.Cross;
using NVPN.Native.Services;
using NVPN.Native.Services.Interfaces;

namespace NVPN.Native
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

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            // Регистрация сервисов
            RegisterServices(builder.Services);

            return builder.Build();
        }

        private static void RegisterServices(IServiceCollection services)
        {

            // Платформо-зависимая регистрация сервиса VPN
#if ANDROID
            services.AddSingleton<IVpnService, Platforms.Android.Services.AndroidVpnService>();
#endif
        }
    }
}
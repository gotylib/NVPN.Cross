using Android.App;
using Android.Content.PM;
using Android.OS;

namespace NVPN.Cross
{
    // Workaround для Java.Lang.IllegalArgumentException "No view found for id .../left"
    // (MAUI Blazor Hybrid Android fragment bug: dotnet/maui#24774, #30719)
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}

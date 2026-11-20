using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Mmopal.Desktop;

public partial class App : System.Windows.Application
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private string _themeMode = "System";

    public bool IsDarkTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(_themeMode);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        base.OnExit(e);
    }

    public void ApplyTheme(string mode)
    {
        _themeMode = mode;
        var isDark = mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDark()
        };
        IsDarkTheme = isDark;

        SetBrush("AppBackgroundBrush", isDark ? "#0D1117" : "#F3F6FA");
        SetBrush("SidebarBrush", isDark ? "#121822" : "#EAF0F7");
        SetBrush("SurfaceBrush", isDark ? "#161E29" : "#FFFFFF");
        SetBrush("SurfaceRaisedBrush", isDark ? "#1E2836" : "#F6F9FC");
        SetBrush("SurfaceHoverBrush", isDark ? "#253244" : "#E9F0F8");
        SetBrush("InputBrush", isDark ? "#0F151E" : "#FFFFFF");
        SetBrush("PopupBackgroundBrush", isDark ? "#1A2330" : "#FFFFFF");
        SetBrush("BorderBrush", isDark ? "#2B394C" : "#D3DCE6");
        SetBrush("BorderSubtleBrush", isDark ? "#202B3A" : "#E4EBF2");
        SetBrush("TextPrimaryBrush", isDark ? "#E9EFF7" : "#151D28");
        SetBrush("TextSecondaryBrush", isDark ? "#9FB0C4" : "#4E5D70");
        SetBrush("TextMutedBrush", isDark ? "#72849B" : "#7B8A9C");
        SetBrush("AccentBrush", isDark ? "#2F9BFF" : "#1677CC");
        SetBrush("AccentHoverBrush", isDark ? "#4AA9FF" : "#1163AC");
        SetBrush("AccentSoftBrush", isDark ? "#1A324B" : "#DCEBFB");
        SetBrush("GoldAccentBrush", isDark ? "#F0B94A" : "#B87A00");
        SetBrush("ButtonBrush", isDark ? "#222E3E" : "#E8EFF6");
        SetBrush("ButtonHoverBrush", isDark ? "#2D3D52" : "#D8E5F2");
        SetBrush("DangerBrush", isDark ? "#D6454A" : "#C53038");
        SetBrush("DangerHoverBrush", isDark ? "#E5595E" : "#A7262D");
        SetBrush("SuccessBrush", isDark ? "#3BB273" : "#1E824C");
        SetBrush("SuccessHoverBrush", isDark ? "#4BC684" : "#196E40");
        SetBrush("WarningBrush", isDark ? "#E5A74F" : "#B56A0A");
        SetBrush("ScrollBarTrackBrush", isDark ? "#121822" : "#EDF2F7");
        SetBrush("ScrollBarThumbBrush", isDark ? "#334459" : "#BCC9D8");

        foreach (Window window in Windows)
        {
            ApplyWindowTitleBar(window);
        }
    }

    public void ApplyWindowTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = IsDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_themeMode == "System")
        {
            Dispatcher.Invoke(() => ApplyTheme("System"));
        }
    }

    private static bool IsSystemDark()
    {
        return Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1) is int lightTheme && lightTheme == 0;
    }

    private void SetBrush(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        Resources[key] = brush;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace QuicFileSharing.GUI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    // 1. Import macOS's native dlopen
    [DllImport("libSystem.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    private const int RTLD_NOW = 0x00002;
    private const int RTLD_GLOBAL = 0x00008;

    [STAThread]
    public static void Main(string[] args)
    {
        var appDir = AppContext.BaseDirectory;

        // FORCE MACOS TO USE LOCAL OPENSSL GLOBALLY
        if (OperatingSystem.IsMacOS())
        {
            // Load OpenSSL globally so MSQuic can see the cryptography symbols
            dlopen(Path.Combine(appDir, "libcrypto.3.dylib"), RTLD_NOW | RTLD_GLOBAL);
            dlopen(Path.Combine(appDir, "libssl.3.dylib"), RTLD_NOW | RTLD_GLOBAL);
            
            // Now load MSQuic
            NativeLibrary.TryLoad(Path.Combine(appDir, "libmsquic.dylib"), out _);
        }
        else if (OperatingSystem.IsWindows())
        {
            NativeLibrary.TryLoad(Path.Combine(appDir, "msquic.dll"), out _);
        }
        else if (OperatingSystem.IsLinux())
        {
            NativeLibrary.TryLoad(Path.Combine(appDir, "libmsquic.so"), out _);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

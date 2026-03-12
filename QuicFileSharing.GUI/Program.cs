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
    [STAThread]
    public static void Main(string[] args)
    {
        var appDir = AppContext.BaseDirectory;

        // FORCE MACOS TO USE LOCAL OPENSSL AND MSQUIC
        if (OperatingSystem.IsMacOS())
        {
            NativeLibrary.TryLoad(Path.Combine(appDir, "libcrypto.3.dylib"), out _);
            NativeLibrary.TryLoad(Path.Combine(appDir, "libssl.3.dylib"), out _);
            NativeLibrary.TryLoad(Path.Combine(appDir, "libmsquic.dylib"), out _);
        }
        
        // FORCE WINDOWS 11 TO USE OUR OPENSSL-COMPILED MSQUIC
        if (OperatingSystem.IsWindows())
        {
            // By giving it the absolute path to our local folder, Windows is forced
            // to load this exact file into RAM, overriding the System32 version.
            NativeLibrary.TryLoad(Path.Combine(appDir, "msquic.dll"), out _);
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

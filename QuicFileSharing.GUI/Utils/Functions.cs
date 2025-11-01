using System;

namespace QuicFileSharing.GUI.Utils;

public static class StaticUtils
{
    public static bool IsValidWebSocketUri(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var tempUri)) return false;
        return tempUri.Scheme == Uri.UriSchemeWs || tempUri.Scheme == Uri.UriSchemeWss;
    }
    public static bool IsValidHttpUrl(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var tempUri)) return false;
        return tempUri.Scheme == Uri.UriSchemeHttp || tempUri.Scheme == Uri.UriSchemeHttps;
    }
    public static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}h {time.Minutes}m {time.Seconds}s remaining";
        return time.TotalMinutes >= 1 ? $"{time.Minutes}m {time.Seconds}s remaining" : $"{time.Seconds}s remaining";
    }
    public static string FormatTimeShort(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}h {time.Minutes}m {time.Seconds}s";
        return time.TotalMinutes >= 1 ? $"{time.Minutes}m {time.Seconds}s" : $"{time.Seconds}s";
    }
}
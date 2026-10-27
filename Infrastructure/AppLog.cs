using System;
using System.IO;
using System.Text;

namespace QuietIsland;

internal static class AppLog
{
    private static readonly object Sync = new();
    private const int MaximumBytes = 128 * 1024;

    public static void Write(Exception exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var path = Path.Combine(AppPaths.DataDirectory, "errors.log");
                var message = exception.Message;
                if (message.Length > 16 * 1024) message = message[..(16 * 1024)] + "…";
                var line = DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine;
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                    File.WriteAllText(path, "");
                File.AppendAllText(path, line);
            }
        }
        catch { }
    }
}

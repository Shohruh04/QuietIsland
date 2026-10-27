using System;
using System.IO;
using System.Text.Json;

namespace QuietIsland;

internal sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly string _path;
    private readonly Action<Exception> _log;

    public JsonSettingsStore(string? dataDirectory = null, Action<Exception>? log = null)
    {
        _directory = dataDirectory ?? AppPaths.DataDirectory;
        _log = log ?? AppLog.Write;
        _path = Path.Combine(_directory, "settings.json");
    }

    public AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new(); }
        catch { return new(); }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? temporaryPath = null;
        try
        {
            // Complete serialization before touching the previous settings file.
            var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            Directory.CreateDirectory(_directory);
            temporaryPath = Path.Combine(_directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename replaces the complete file; readers never see partial JSON.
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception ex) { _log(ex); }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) { _log(ex); }
            }
        }
    }
}

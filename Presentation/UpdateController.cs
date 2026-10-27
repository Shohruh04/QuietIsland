using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace QuietIsland;

/// <summary>Schedules quiet checks; downloads/installation always require a user action.</summary>
internal sealed class UpdateController : IDisposable
{
    private sealed record CheckState(DateTimeOffset LastCheckUtc = default, string? NotifiedVersion = null);
    private readonly GitHubUpdateService _service;
    private readonly UpdateInstaller _installer;
    private readonly AppSettings _settings;
    private readonly StartupRegistration _startup;
    private readonly Action _saveSettings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _directory;
    private Task? _scheduler, _manual;
    private Task<UpdateRelease?>? _check;
    private CheckState _state;
    private bool _disposed;
    public bool IsBusy { get; private set; }
    public bool IsChecking => _check != null;
    public Version? AvailableVersion { get; private set; }
    public event Action? Changed;
    public event Action<string>? NotificationRequested;

    public UpdateController(GitHubUpdateService service, UpdateInstaller installer, AppSettings settings, StartupRegistration startup, Action saveSettings, string? stateDirectory = null)
    {
        _service = service; _installer = installer; _settings = settings; _startup = startup; _saveSettings = saveSettings;
        _directory = stateDirectory ?? Path.Combine(AppPaths.DataDirectory, "Updates");
        try { _state = JsonSerializer.Deserialize<CheckState>(File.ReadAllText(StatePath)) ?? new(); }
        catch { _state = new(); }
    }
    private string StatePath => Path.Combine(_directory, "checks.json");
    public void Start() { if (!_disposed) _scheduler ??= ScheduleAsync(); }
    public void ToggleAutomatic() { if (!_disposed) { _settings.AutoCheckUpdates = !_settings.AutoCheckUpdates; _saveSettings(); Changed?.Invoke(); } }
    private async Task ScheduleAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
            try { await Task.Run(() => GitHubUpdateService.CleanOldDownloads(_directory, DateTime.UtcNow), _lifetime.Token); }
            catch (Exception exception) { AppLog.Write(exception); }
            while (!_disposed)
            {
                await CheckAutomaticallyAsync();
                await Task.Delay(TimeSpan.FromHours(1), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
    }
    internal static bool IsCheckDue(DateTimeOffset lastCheck, DateTimeOffset now) => lastCheck > now || now - lastCheck >= TimeSpan.FromDays(1);
    internal async Task CheckAutomaticallyAsync()
    {
        if (_disposed || !_settings.AutoCheckUpdates || IsBusy || !IsCheckDue(_state.LastCheckUtc, DateTimeOffset.UtcNow)) return;
        _state = _state with { LastCheckUtc = DateTimeOffset.UtcNow }; SaveState();
        try
        {
            var release = await CheckSharedAsync();
            if (_disposed) return;
            AvailableVersion = release?.Version; Changed?.Invoke();
            if (release != null && _settings.AutoCheckUpdates && !IsBusy && _state.NotifiedVersion != release.Tag)
            {
                _state = _state with { NotifiedVersion = release.Tag }; SaveState();
                NotificationRequested?.Invoke(string.Format(UiText.T("Доступна QuietIsland {0}. Выберите «Проверить обновления» в меню значка."), release.Version.ToString(3)));
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { AppLog.Write(exception); }
    }
    internal async Task<UpdateRelease?> CheckSharedAsync()
    {
        var task = _check ??= _service.CheckAsync(GitHubUpdateService.CurrentVersion, _lifetime.Token);
        Changed?.Invoke();
        try { return await task; }
        finally { if (ReferenceEquals(_check, task)) { _check = null; if (!_disposed) Changed?.Invoke(); } }
    }
    public Task CheckManuallyAsync(IntPtr owner)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        return _manual = CheckManuallyCoreAsync(owner);
    }
    private async Task CheckManuallyCoreAsync(IntPtr ownerHandle)
    {
        IsBusy = true; Changed?.Invoke();
        var owner = new DialogOwner(ownerHandle);
        string? download = null;
        var launched = false;
        try
        {
            var release = await CheckSharedAsync();
            if (_disposed) return;
            _state = _state with { LastCheckUtc = DateTimeOffset.UtcNow }; SaveState();
            AvailableVersion = release?.Version; Changed?.Invoke();
            if (release == null) { ShowMessage(owner, UiText.T("Установлена последняя версия QuietIsland.")); return; }
            _state = _state with { NotifiedVersion = release.Tag }; SaveState();
            if (_installer.RegisteredDirectory is null || release.Installer is null)
            {
                if (Forms.MessageBox.Show(owner, string.Format(UiText.T("Доступна QuietIsland {0}. Открыть страницу загрузки? Portable-версию нужно распаковать вручную."), release.Version.ToString(3)), "QuietIsland", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Information, Forms.MessageBoxDefaultButton.Button2) == Forms.DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true });
                return;
            }
            if (Forms.MessageBox.Show(owner, string.Format(UiText.T("Доступна QuietIsland {0}. Скачать обновление и открыть установщик? Настройки сохранятся."), release.Version.ToString(3)), "QuietIsland", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Question, Forms.MessageBoxDefaultButton.Button2) != Forms.DialogResult.Yes) return;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            using (var dialog = new DownloadDialog(cancellation))
            {
                dialog.Show(owner);
                download = await _service.DownloadAsync(release, _directory, new Progress<int>(dialog.Report), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                dialog.Close();
            }
            if (_disposed) return;
            _installer.Launch(download, release.Installer, _startup.IsEnabled);
            launched = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            if (!_disposed) ShowMessage(owner, UiText.T("Не удалось проверить или установить обновление. Проверьте подключение и повторите позже."), true);
        }
        finally
        {
            if (!launched && download != null) { try { File.Delete(download); } catch { } }
            IsBusy = false; if (!_disposed) Changed?.Invoke();
        }
    }
    private static void ShowMessage(Forms.IWin32Window owner, string message, bool error = false) => Forms.MessageBox.Show(owner, message, "QuietIsland", Forms.MessageBoxButtons.OK, error ? Forms.MessageBoxIcon.Warning : Forms.MessageBoxIcon.Information);
    private void SaveState()
    {
        try { Directory.CreateDirectory(_directory); File.WriteAllText(StatePath, JsonSerializer.Serialize(_state)); }
        catch (Exception exception) { AppLog.Write(exception); }
    }
    private sealed record DialogOwner(IntPtr Handle) : Forms.IWin32Window;
    private sealed class DownloadDialog : Forms.Form
    {
        private readonly System.Drawing.Icon _icon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "QuietIsland.ico"));
        private readonly CancellationTokenSource _cancellation;
        private readonly Forms.ProgressBar _progress = new() { Left = 20, Top = 46, Width = 320, Height = 18 };
        private readonly Forms.Label _label = new() { Left = 20, Top = 18, Width = 320, Height = 22 };
        public DownloadDialog(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            Text = "QuietIsland"; Icon = _icon; ClientSize = new System.Drawing.Size(360, 120);
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = Forms.FormStartPosition.CenterScreen; ShowInTaskbar = true;
            _label.Text = UiText.T("Загрузка обновления…");
            var cancel = new Forms.Button { Text = UiText.T("Отмена"), Left = 250, Top = 82, Width = 90 };
            cancel.Click += (_, _) => Close();
            Controls.AddRange(new Forms.Control[] { _label, _progress, cancel }); CancelButton = cancel;
        }
        public void Report(int percent)
        {
            if (IsDisposed || Disposing) return;
            _progress.Value = Math.Clamp(percent, 0, 100);
            _label.Text = percent < 100 ? UiText.T("Загрузка обновления…") + " " + percent + "%" : UiText.T("Проверка файла…");
        }
        protected override void OnFormClosing(Forms.FormClosingEventArgs e) { _cancellation.Cancel(); base.OnFormClosing(e); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) _icon.Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); Changed = null; NotificationRequested = null;
        _ = DisposeAfterWorkAsync();
    }
    private async Task DisposeAfterWorkAsync()
    {
        try { await Task.WhenAll(_scheduler ?? Task.CompletedTask, _manual ?? Task.CompletedTask, _check ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLog.Write(exception); }
        finally { _service.Dispose(); _lifetime.Dispose(); }
    }
}

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace QuietIsland;

public partial class App : Application
{
    private Mutex? _instance;
    private CancellationTokenSource _shutdown = new();
    private static string InstanceName => "QuietIsland." + WindowsIdentity.GetCurrent().User?.Value;
    protected override async void OnStartup(StartupEventArgs e)
    {
        // A 480x48 widget updates at 1 Hz: software composition avoids loading a
        // full GPU/shader pipeline for this tiny surface and has predictable cost.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { AppLog.Write(a.Exception); a.Handled = true; };
        if (e.Args.Contains("--self-test"))
        {
            var exitCode = 1;
            try { exitCode = await Verification.RunAsync(!e.Args.Contains("--stdout")) ? 0 : 1; }
            catch (Exception exception) { AppLog.Write(exception); Console.Error.WriteLine(exception.Message); }
            finally { Shutdown(exitCode); }
            return;
        }
        _instance = new Mutex(true, "Local\\" + InstanceName, out var first);
        var command = e.Args.FirstOrDefault(a => a.StartsWith("--")) ?? "--open";
        if (!first)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.InOut);
                await pipe.ConnectAsync(2000);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true) { AutoFlush = true };
                await writer.WriteLineAsync(command);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var timeout = new CancellationTokenSource(5000);
                var response = await reader.ReadLineAsync(timeout.Token);
                if (command == "--status-json")
                {
                    if (response?.StartsWith("{", StringComparison.Ordinal) == true) Console.WriteLine(response);
                    else { Console.Error.WriteLine("The running copy does not support JSON status."); Environment.ExitCode = 1; }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                if (command == "--status-json") { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            }
            Shutdown(Environment.ExitCode); return;
        }
        if (command == "--status-json") { Console.Error.WriteLine("QuietIsland is not running."); Shutdown(1); return; }
        if (command is "--exit" or "--disable-startup")
        {
            if (command == "--disable-startup") new StartupRegistration().SetEnabled(false);
            Shutdown(); return;
        }
        var viewModel = CreateViewModel(Dispatcher);
        var updates = new UpdateController(new GitHubUpdateService(), new UpdateInstaller(), viewModel.Settings, viewModel.Startup, viewModel.SaveSettings);
        var island = new IslandWindow(viewModel, new TaskbarSpace(), updates);
        MainWindow = island;
        island.Show();
        NativeWindow.ShowWindow(island.Handle, 4);
        if (command != "--startup") island.HandleCommand(command);
        _ = ListenAsync(island, _shutdown.Token);
        updates.Start();
    }

    internal static IslandViewModel CreateViewModel(Dispatcher dispatcher)
    {
        var store = new JsonSettingsStore();
        return new IslandViewModel(store.Load(), store, new StartupRegistration(), new MediaService(dispatcher),
            new AudioService(dispatcher), new LyricsService(), new FocusTimer(), dispatcher);
    }

    private async Task ListenAsync(IslandWindow island, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                var command = await reader.ReadLineAsync(token);
                if (command == "--status-json")
                {
                    var json = await Dispatcher.InvokeAsync(island.GetStatusJson);
                    using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true) { AutoFlush = true };
                    await writer.WriteLineAsync(json);
                }
                else if (command == "--exit")
                {
                    using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true) { AutoFlush = true };
                    await writer.WriteLineAsync("ok");
                    await Dispatcher.InvokeAsync(() => island.HandleCommand(command));
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => island.HandleCommand(command ?? "--open"));
                    using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true) { AutoFlush = true };
                    await writer.WriteLineAsync("ok");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AppLog.Write(ex); await Task.Delay(1000, token); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

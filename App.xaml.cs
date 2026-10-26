using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using System.Text;
using System.Windows;

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
        DispatcherUnhandledException += (_, a) => { AppSettings.Log(a.Exception); a.Handled = true; };
        if (e.Args.Contains("--self-test")) { await Verification.RunAsync(); Shutdown(); return; }
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
                await reader.ReadLineAsync(timeout.Token);
            }
            catch (Exception ex) { AppSettings.Log(ex); }
            Shutdown(); return;
        }
        if (command is "--exit" or "--disable-startup")
        {
            if (command == "--disable-startup") AppSettings.SetStartup(false);
            Shutdown(); return;
        }
        var island = new IslandWindow();
        MainWindow = island;
        island.Show();
        NativeWindow.ShowWindow(island.Handle, 4);
        if (command != "--startup") island.HandleCommand(command);
        _ = ListenAsync(island, _shutdown.Token);
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
                if (command == "--exit")
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
            catch (Exception ex) { AppSettings.Log(ex); await Task.Delay(1000, token); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

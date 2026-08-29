using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Launches a preinstalled WebDriverAgent through go-ios, which speaks the
/// iOS 17+ CoreDevice tunnel and testmanagerd protocols. A runner app links
/// XCTest, which iOS only mounts while a developer session is active, so the
/// app must be started through this channel instead of being tapped on the
/// device (a manual launch aborts in dyld with "Library not loaded").
/// </summary>
internal sealed class GoIosLauncher : IAsyncDisposable
{
    private const string WdaConfigName = "WebDriverAgentRunner";
    private const string TunnelInfoPortArg = "--tunnel-info-port=28100";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _tunnelAgent;
    private Process? _wdaProcess;
    private string? _wdaLogPath;

    internal static string ResolveExePath() => Path.Combine(
        AppContext.BaseDirectory, "tools", "go-ios", "ios.exe");

    internal bool IsAvailable => File.Exists(ResolveExePath());

    /// <summary>
    /// Ensures the developer tunnel is up and starts the preinstalled WDA
    /// runner. Returns false with a non-empty <paramref name="error"/>
    /// describing the exact failing step.
    /// </summary>
    internal async Task<(bool Ok, string? Error)> LaunchAsync(
        string udid, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return (false, "goios_missing");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_wdaProcess is { HasExited: false })
                return (true, "wda_already_running");

            var (tunnelOk, tunnelError) = await EnsureTunnelAsync(
                udid, cancellationToken).ConfigureAwait(false);
            if (!tunnelOk) return (false, tunnelError);

            var (hostBundle, testBundle, discoverError) = await DiscoverWdaBundlesAsync(
                udid, cancellationToken).ConfigureAwait(false);
            if (discoverError is not null) return (false, discoverError);
            if (hostBundle is null || testBundle is null)
                return (false, "wda_not_installed");

            return await StartWdaAsync(udid, hostBundle, testBundle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return (false, $"goios_error:{error.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void Stop()
    {
        TryKill(_wdaProcess);
        _wdaProcess = null;
        TryKill(_tunnelAgent);
        _tunnelAgent = null;
        if (_wdaLogPath is not null)
        {
            try { File.Delete(_wdaLogPath); } catch { /* temp log cleanup */ }
            _wdaLogPath = null;
        }
    }

    private async Task<(bool Ok, string? Error)> EnsureTunnelAsync(
        string udid, CancellationToken cancellationToken)
    {
        // The tunnel agent keeps a registration in its info API; a second
        // start is harmless but a healthy listing avoids stacking agents.
        // Only an entry with a real RSD endpoint counts; the JSON wrapper
        // always contains the word "tunnel" even when the list is empty.
        var (healthy, _) = await TunnelListingAsync(udid, cancellationToken)
            .ConfigureAwait(false);
        if (healthy) return (true, null);

        _tunnelAgent = StartDetached(["tunnel", "start", "--userspace",
            TunnelInfoPortArg, $"--udid={udid}"]);
        if (_tunnelAgent is null) return (false, "goios_tunnel_start_failed");
        // Give the agent time to establish the CoreDevice tunnel; a fresh
        // pairing may wait for the trust prompt on the device.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            if (_tunnelAgent.HasExited)
            {
                var error = $"goios_tunnel_exited:{_tunnelAgent.ExitCode}";
                TryKill(_tunnelAgent);
                _tunnelAgent = null;
                return (false, error);
            }
            var (ok, _) = await TunnelListingAsync(udid, cancellationToken)
                .ConfigureAwait(false);
            if (ok) return (true, null);
        }
        return (false, "goios_tunnel_timeout");
    }

    private async Task<(bool Healthy, string Output)> TunnelListingAsync(
        string udid, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunCaptureAsync(
            ["tunnel", "ls", TunnelInfoPortArg, $"--udid={udid}"],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var healthy = exitCode == 0 && (output.Contains("rsdPort") ||
            output.Contains("RsdAddress"));
        return (healthy, output);
    }

    private async Task<(string? HostBundle, string? TestBundle, string? Error)>
        DiscoverWdaBundlesAsync(string udid, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunCaptureAsync(["apps", $"--udid={udid}"],
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            return (null, null, $"goios_apps_query_failed:{exitCode}");
        var ids = Regex.Matches(output,
                "\"bundleId\"\\s*:\\s*\"([^\"]*WebDriverAgentRunner[^\"]*)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var host = ids.FirstOrDefault(id =>
            id.Contains("xctrunner", StringComparison.OrdinalIgnoreCase));
        var test = ids.FirstOrDefault(id =>
            !id.Contains("xctrunner", StringComparison.OrdinalIgnoreCase));
        return (host, test, null);
    }

    private async Task<(bool Ok, string? Error)> StartWdaAsync(
        string udid, string hostBundle, string testBundle,
        CancellationToken cancellationToken)
    {
        _wdaLogPath = Path.Combine(Path.GetTempPath(),
            $"iPhoneMirror-wda-{Environment.TickCount64:x}.log");
        _wdaProcess = StartDetached([
            "runwda",
            $"--bundleid={hostBundle}",
            $"--testrunnerbundleid={testBundle}",
            $"--xctestconfig={WdaConfigName}",
            $"--udid={udid}",
            $"--log-output={_wdaLogPath}",
        ]);
        if (_wdaProcess is null) return (false, "goios_runwda_start_failed");
        // runwda keeps running for the whole WDA session. An early exit means
        // the launch was rejected; its log file carries the actual reason.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            if (_wdaProcess.HasExited)
            {
                var exitTail = await ReadLogTailAsync(_wdaLogPath).ConfigureAwait(false);
                return (false,
                    $"goios_runwda_exited:{_wdaProcess.ExitCode}:{exitTail}");
            }
            var tail = await ReadLogTailAsync(_wdaLogPath).ConfigureAwait(false);
            if (tail.Contains("Server started", StringComparison.OrdinalIgnoreCase))
                return (true, "wda_server_started");
        }
        var finalTail = await ReadLogTailAsync(_wdaLogPath).ConfigureAwait(false);
        return (true, string.IsNullOrEmpty(finalTail)
            ? "wda_process_alive_no_output"
            : $"wda_process_alive tail={finalTail}");
    }

    private static async Task<string> ReadLogTailAsync(string? path)
    {
        if (path is null || !File.Exists(path)) return string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[2048];
            var offset = (int)Math.Max(0, stream.Length - buffer.Length);
            stream.Seek(offset, SeekOrigin.Begin);
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            return Encoding.UTF8.GetString(buffer, 0, read)
                .Replace("\r", " ").Replace("\n", " ").Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private Process? StartDetached(string[] arguments)
    {
        var exe = ResolveExePath();
        if (!File.Exists(exe)) return null;
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo);
    }

    private async Task<(int ExitCode, string Output)> RunCaptureAsync(
        string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exe = ResolveExePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(exitTask,
            Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != exitTask)
        {
            TryKill(process);
            return (-1, "goios_command_timeout");
        }
        output.Append(await stdout.ConfigureAwait(false));
        output.Append(await stderr.ConfigureAwait(false));
        return (process.ExitCode, output.ToString());
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        catch
        {
            // The process may have already exited or be unkillable mid-teardown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

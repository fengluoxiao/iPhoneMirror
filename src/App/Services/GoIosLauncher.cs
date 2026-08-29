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
    private string? _agentLogPath;

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
        // A tunnel agent left over from an earlier session may still own the
        // info port without having any tunnel; stop it before starting fresh.
        var (healthy, listing) = await TunnelListingAsync(udid, cancellationToken)
            .ConfigureAwait(false);
        if (healthy) return (true, null);
        if (listing.Contains("tunnel", StringComparison.OrdinalIgnoreCase))
        {
            await RunCaptureAsync(["tunnel", "stopagent"], TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        // Userspace tunnel first: no admin rights and no wintun requirement.
        var userspace = await StartAgentAndAwaitAsync(udid, userspaceTun: true,
            attempts: 30, cancellationToken).ConfigureAwait(false);
        if (userspace.Ok) return (true, null);

        // Fall back to the kernel tunnel (needs wintun.dll + elevation); its
        // failure mode is still reported so the cause stays visible.
        var kernel = await StartAgentAndAwaitAsync(udid, userspaceTun: false,
            attempts: 15, cancellationToken).ConfigureAwait(false);
        if (kernel.Ok) return (true, null);

        var (_, finalListing) = await TunnelListingAsync(udid, cancellationToken)
            .ConfigureAwait(false);
        return (false,
            $"goios_tunnel_timeout userspace=[{userspace.Error}] kernel=[{kernel.Error}] ls={finalListing}");
    }

    private async Task<(bool Ok, string? Error)> StartAgentAndAwaitAsync(
        string udid, bool userspaceTun, int attempts,
        CancellationToken cancellationToken)
    {
        var mode = userspaceTun ? "userspace" : "kernel";
        var arguments = userspaceTun
            ? new[] { "tunnel", "start", "--userspace", TunnelInfoPortArg, $"--udid={udid}" }
            : new[] { "tunnel", "start", TunnelInfoPortArg, $"--udid={udid}" };

        TryKill(_tunnelAgent);
        // Known go-ios issue: the userspace tunnel listener binds a fixed
        // port, so a stale agent from a crashed session makes every new
        // agent exit immediately. Clear all go-ios processes first; the
        // launch path never reaches here while a WDA process is alive.
        try
        {
            await RunProcessCaptureAsync("taskkill",
                ["/F", "/IM", "ios.exe", "/T"], TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // taskkill failing (nothing to kill) is not an error.
        }
        _tunnelAgent = StartDetachedWithCapture(arguments, out var agentOutput);
        if (_tunnelAgent is null) return (false, $"{mode}:agent_start_failed");
        try
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
                if (_tunnelAgent.HasExited)
                {
                    var exitTail = agentOutput().Trim();
                    var exitCode = SafeExitCode(_tunnelAgent);
                    var exitFile = DumpAgentOutput(exitTail);
                    var exitMsg = ExtractLastLogMessage(exitTail);
                    var error = $"{mode}:agent_exited:{exitCode} last_msg={exitMsg ?? "none"} agent_log={exitFile}";
                    TryKill(_tunnelAgent);
                    _tunnelAgent = null;
                    return (false, error);
                }
                var (healthy, _) = await TunnelListingAsync(udid, cancellationToken)
                    .ConfigureAwait(false);
                if (healthy) return (true, null);
            }
            var (_, pendingListing) = await TunnelListingAsync(udid, cancellationToken)
                .ConfigureAwait(false);
            var agentTail = agentOutput().Trim();
            var agentFile = DumpAgentOutput(agentTail);
            var lastMsg = ExtractLastLogMessage(agentTail);
            return (false,
                $"{mode}:timeout last_msg={lastMsg ?? "none"} agent_log={agentFile} ls={pendingListing}");
        }
        finally
        {
            if (_tunnelAgent is { HasExited: true } exited)
            {
                exited.Dispose();
                _tunnelAgent = null;
            }
        }
    }

    private Process? StartDetachedWithCapture(string[] arguments, out Func<string> outputTail)
    {
        outputTail = () => string.Empty;
        var exe = ResolveExePath();
        if (!File.Exists(exe)) return null;
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo);
        if (process is null) return null;
        var log = new StringBuilder();
        var sync = new object();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) log.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (sync) log.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        outputTail = () =>
        {
            string snapshot;
            lock (sync) snapshot = log.ToString();
            var flattened = snapshot
                .Replace(Convert.ToString(13), " ")
                .Replace(Convert.ToString(10), " ")
                .Trim();
            return flattened.Length <= 1024
                ? flattened
                : flattened[^1024..];
        };
        return process;
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
        _wdaProcess = StartDetachedWithCapture([
            "runwda",
            $"--bundleid={hostBundle}",
            $"--testrunnerbundleid={testBundle}",
            $"--xctestconfig={WdaConfigName}",
            $"--udid={udid}",
            $"--log-output={_wdaLogPath}",
        ], out _);
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
                var exitCode = SafeExitCode(_wdaProcess);
                return (false, $"goios_runwda_exited:{exitCode}:{exitTail}");
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

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
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
                .Replace(Convert.ToString(13), " ")
                .Replace(Convert.ToString(10), " ")
                .Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(int ExitCode, string Output)> RunCaptureAsync(
        string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await RunProcessCaptureAsync(ResolveExePath(), arguments, timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessCaptureAsync(
        string exe, string[] arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
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

    private string DumpAgentOutput(string output)
    {
        _agentLogPath = Path.Combine(Path.GetTempPath(),
            "iPhoneMirror-goios-agent.log");
        try { File.WriteAllText(_agentLogPath, output); }
        catch { return "unavailable"; }
        return _agentLogPath;
    }

    private static string? ExtractLastLogMessage(string output)
    {
        var marker = "\"msg\":\"";
        var index = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var start = index + marker.Length;
        var end = output.IndexOf('"', start);
        return end > start ? output[start..end] : null;
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

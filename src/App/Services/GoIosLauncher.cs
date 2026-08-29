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
    /// runner. Returns false with <see cref="LastError"/> set on failure.
    /// </summary>
    internal async Task<bool> LaunchAsync(string udid, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            LastError = "goios_missing";
            return false;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_wdaProcess is { HasExited: false }) return true;

            if (!await EnsureTunnelAsync(udid, cancellationToken).ConfigureAwait(false))
                return false;

            var (hostBundle, testBundle) = await DiscoverWdaBundlesAsync(
                udid, cancellationToken).ConfigureAwait(false);
            if (hostBundle is null || testBundle is null)
            {
                LastError = "wda_not_installed";
                return false;
            }

            return await StartWdaAsync(udid, hostBundle, testBundle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            LastError = $"goios_error:{error.Message}";
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal string? LastError { get; private set; }

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

    private async Task<bool> EnsureTunnelAsync(
        string udid, CancellationToken cancellationToken)
    {
        // The tunnel agent keeps a registration in its info API; a second
        // start is harmless but a healthy listing avoids stacking agents.
        var listing = await RunCaptureAsync(
            ["tunnel", "ls", TunnelInfoPortArg, $"--udid={udid}"],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (listing.ExitCode == 0 && (listing.Output.Contains("rsdPort") ||
            listing.Output.Contains("RsdAddress") || listing.Output.Contains("tunnel")))
            return true;

        _tunnelAgent = StartDetached(["tunnel", "start", "--userspace",
            TunnelInfoPortArg, $"--udid={udid}"]);
        if (_tunnelAgent is null)
        {
            LastError = "goios_tunnel_start_failed";
            return false;
        }
        // Give the agent time to establish the CoreDevice tunnel; a fresh
        // pairing may wait for the trust prompt on the device.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            if (_tunnelAgent.HasExited)
            {
                LastError = $"goios_tunnel_exited:{_tunnelAgent.ExitCode}";
                _tunnelAgent.Dispose();
                _tunnelAgent = null;
                return false;
            }
            listing = await RunCaptureAsync(
                ["tunnel", "ls", TunnelInfoPortArg, $"--udid={udid}"],
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (listing.ExitCode == 0 && (listing.Output.Contains("rsdPort") ||
                listing.Output.Contains("RsdAddress")))
                return true;
        }
        LastError = "goios_tunnel_timeout";
        return false;
    }

    private async Task<(string? HostBundle, string? TestBundle)> DiscoverWdaBundlesAsync(
        string udid, CancellationToken cancellationToken)
    {
        var listing = await RunCaptureAsync(["apps", $"--udid={udid}"],
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (listing.ExitCode != 0)
        {
            LastError = "goios_apps_query_failed";
            return (null, null);
        }
        var ids = Regex.Matches(listing.Output,
            "\"bundleId\"\\s*:\\s*\"([^\"]*WebDriverAgentRunner[^\"]*)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var host = ids.FirstOrDefault(id => id.Contains("xctrunner", StringComparison.OrdinalIgnoreCase));
        var test = ids.FirstOrDefault(id => !id.Contains("xctrunner", StringComparison.OrdinalIgnoreCase));
        return (host, test);
    }

    private async Task<bool> StartWdaAsync(
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
        if (_wdaProcess is null)
        {
            LastError = "goios_runwda_start_failed";
            return false;
        }
        // runwda keeps running for the whole WDA session. An early exit means
        // the launch was rejected; its log file carries the actual reason.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            if (_wdaProcess.HasExited)
            {
                var tail = await ReadLogTailAsync(_wdaLogPath).ConfigureAwait(false);
                LastError = $"goios_runwda_exited:{_wdaProcess.ExitCode}:{tail}";
                return false;
            }
            // Any output at all means testmanagerd accepted the session.
            if (new FileInfo(_wdaLogPath) is { Exists: true, Length: > 0 } log)
            {
                var tail = await ReadLogTailAsync(_wdaLogPath).ConfigureAwait(false);
                if (tail.Contains("Server started", StringComparison.OrdinalIgnoreCase) ||
                    tail.Contains("WebDriverAgent", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return true;
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
        var process = Process.Start(startInfo);
        return process;
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
            return (-1, output.ToString());
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

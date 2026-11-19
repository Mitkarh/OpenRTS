#if TOOLS
using System.Diagnostics;

namespace OpenRTS.Godot.Authoring;

internal sealed class PlaytestProcess
{
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly Queue<string> _output = new();
    private bool _ready, _stopping, _forced;
    private int? _exitCode;

    internal int Id { get; }
    internal Task<int> Completion { get; }
    internal (bool Ready, bool Stopping, bool Forced, int? ExitCode) State
    {
        get { lock (_gate) return (_ready, _stopping, _forced, _exitCode); }
    }
    internal string Diagnostics
    {
        get { lock (_gate) return string.Join("\n", _output); }
    }

    internal PlaytestProcess(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        _process = new() { StartInfo = start };
        _process.OutputDataReceived += (_, args) => Capture(args.Data, false);
        _process.ErrorDataReceived += (_, args) => Capture(args.Data, true);
        try
        {
            if (!_process.Start()) throw new IOException("Godot could not start the map player.");
            Id = _process.Id;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Completion = ObserveExit();
        }
        catch
        {
            if (Id != 0) Kill();
            _process.Dispose();
            throw;
        }
    }

    private void Capture(string? line, bool error)
    {
        if (line is null) return;
        lock (_gate)
        {
            if (!error && line == Sandbox.PlaytestReadyLine) _ready = true;
            AddLine((error ? "[stderr] " : "[stdout] ") + (line.Length <= 1024 ? line : line[..1024] + "…"));
        }
    }

    private void AddLine(string line)
    {
        if (_output.Count == 80) _output.Dequeue();
        _output.Enqueue(line);
    }

    private async Task<int> ObserveExit()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        lock (_gate)
        {
            int code = _process.ExitCode;
            _exitCode = code;
            _process.Dispose();
            return code;
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_exitCode.HasValue || _stopping) return;
            _stopping = true;
            // EOF asks the game to quit. It also arrives if the editor exits unexpectedly.
            try { _process.StandardInput.Close(); }
            catch (IOException error) { AddLine("[editor] " + error.Message); }
        }
        _ = StopUnresponsiveProcess();
    }

    private async Task StopUnresponsiveProcess()
    {
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (_exitCode.HasValue) return;
                _forced = Kill();
                if (_forced) AddLine("[editor] Godot did not respond to stop within 5 seconds and was terminated.");
            }
        }
    }

    private bool Kill()
    {
        try { _process.Kill(); return true; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception error)
        {
            lock (_gate) AddLine("[editor] Could not terminate Godot: " + error.Message);
            return false;
        }
    }
}
#endif

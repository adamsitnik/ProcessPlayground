using BenchmarkDotNet.Attributes;
using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.TBA;

namespace Benchmarks;

public class KillTree
{
    private Process? _process;
    private SafeChildProcessHandle? _childProcessHandle;

    [IterationSetup(Target = nameof(Process_Kill_EntireProcessTree))]
    public void Setup_Process()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new()
            {
                FileName = "powershell",
                Arguments = "-Command \"Start-Process timeout -ArgumentList '/t','30','/nobreak' -NoNewWindow; Start-Process timeout -ArgumentList '/t','30','/nobreak' -NoNewWindow; Start-Process timeout -ArgumentList '/t','30','/nobreak' -NoNewWindow; timeout /t 30 /nobreak\"",
                UseShellExecute = false,
                RedirectStandardInput = true
            }
            : new()
            {
                FileName = "sh",
                Arguments = "-c \"sleep 30 & sleep 30 & sleep 30 & sleep 30 & wait\"",
                UseShellExecute = false
            };

        _process = Process.Start(startInfo);
    }

    [Benchmark(Baseline = true)]
    public void Process_Kill_EntireProcessTree()
    {
        _process!.Kill(entireProcessTree: true);
        _process.WaitForExit();
    }

    [IterationSetup(Target = nameof(SafeProcessHandle_KillProcessGroup))]
    public void Setup_Sfh()
    {
        ProcessStartOptions options = OperatingSystem.IsWindows()
            ? new("powershell")
            {
                Arguments = { "-Command", "Start-Process timeout -ArgumentList '/t','30','/nobreak' -NoNewWindow; Start-Process timeout -ArgumentList '/t','30','/nobreak' -NoNewWindow; Start-Process timeout -ArgumentList '/t','30','/nobreak' -NoNewWindow; timeout /t 30 /nobreak" },
            }
            : new("sh")
            {
                Arguments = { "-c", "sleep 30 & sleep 30 & sleep 30 & sleep 30 & wait" },
            };
        options.CreateNewProcessGroup = true;

        SafeFileHandle? stdin = OperatingSystem.IsWindows() ? Console.OpenStandardInputHandle() : null;
        _childProcessHandle = SafeChildProcessHandle.Start(options, input: stdin, output: null, error: null);
    }

    [Benchmark]
    public void SafeProcessHandle_KillProcessGroup()
    {
        _childProcessHandle!.KillProcessGroup();
        _childProcessHandle.WaitForExit();
    }
}

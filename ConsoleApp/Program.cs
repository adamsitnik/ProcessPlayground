using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.TBA;

const int Count = 10_000;

using SafeFileHandle nullHandle = File.OpenNullFileHandle();

ProcessStartOptions pwd = OperatingSystem.IsWindows()
    ? new("cmd.exe") { Arguments = { "/c", "cd" } }
    : new("pwd");

Stopwatch stopwatch = Stopwatch.StartNew();

Parallel.For(0, Count, (_, _) =>
{
    using (Process process = new())
    {
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = "/c cd";
        }
        else
        {
            process.StartInfo.FileName = "pwd";
        }

        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;

        process.OutputDataReceived += (sender, e) => { };

        process.Start();

        process.BeginOutputReadLine();

        process.WaitForExit();
    }
});

Console.WriteLine($"Old sync:  {stopwatch.Elapsed}");
stopwatch.Restart();

Parallel.For(0, Count, (_, _) =>
{
    using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(pwd, nullHandle, nullHandle, nullHandle);
    procHandle.WaitForExit();
});

Console.WriteLine($"New sync:  {stopwatch.Elapsed}");
stopwatch.Restart();

await Parallel.ForAsync(0, Count, async (_, _) =>
{
    using (Process process = new())
    {
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = "/c cd";
        }
        else
        {
            process.StartInfo.FileName = "pwd";
        }

        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;

        process.OutputDataReceived += (sender, e) => { };
        process.Start();

        process.BeginOutputReadLine();

        await process.WaitForExitAsync();
    }
});

Console.WriteLine($"Old async: {stopwatch.Elapsed}");
stopwatch.Restart();

await Parallel.ForAsync(0, Count, async (_, _) =>
{
    using SafeChildProcessHandle procHandle = SafeChildProcessHandle.Start(pwd, nullHandle, nullHandle, nullHandle);
    await procHandle.WaitForExitAsync();
});

Console.WriteLine($"New async: {stopwatch.Elapsed}");
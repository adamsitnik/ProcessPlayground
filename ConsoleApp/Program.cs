// This spawns a grandchild that writes to stdout after a delay, then the child exits immediately
using System.Diagnostics;
using System.TBA;

ProcessStartOptions options = OperatingSystem.IsWindows()
    ? new("cmd.exe")
    {
        // Child writes "Child output", spawns grandchild to write after 3 seconds, then exits
        Arguments = { "/c", "echo Child output && start cmd.exe /c timeout /t 3 /nobreak && exit" }
    }
    : new("sh")
    {
        // Child writes "Child output", spawns grandchild to write after 3 seconds, then exits
        Arguments = { "-c", "echo 'Child output' && sleep 3 & exit" }
    };

Stopwatch started = Stopwatch.StartNew();

Task<ProcessOutput>[] tasks = Enumerable.Range(0, 10)
    .Select(_ => Task.Run(() => ChildProcess.CaptureOutput(options, timeout: TimeSpan.FromSeconds(5))))
    .ToArray();

await Task.WhenAll(tasks);

foreach (var task in tasks)
{
    ProcessOutput result = task.Result;

    // Should complete before the grandchild writes (which happens after 3 seconds)
    Console.WriteLine(started.Elapsed);
    Console.WriteLine($"Exit Code: {result.ExitCode}");
    Console.WriteLine($"Standard Output: '{result.StandardOutput}'");
}
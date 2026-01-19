using System.Diagnostics;
using System.TBA;

if (args.Length == 0)
{
    // No args specified: use Process.Start to run echo in parallel
    var tasks = Enumerable.Range(0, 2)
        .Select(i => Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/echo",
                Arguments = "hello!",
            };
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();
        }));

    await Task.WhenAll(tasks);
    Console.WriteLine("Completed using Process.Start");
}
else
{
    // Args specified: use ChildProcess.ExecuteAsync
    var tasks = Enumerable.Range(0, 2)
        .Select(i => Task.Run(async () =>
        {
            ProcessStartOptions options = new("/bin/echo")
            {
                Arguments = { "hello!" }
            };
            await ChildProcess.ExecuteAsync(options);
        }));

    await Task.WhenAll(tasks);
    Console.WriteLine("Completed using ChildProcess.ExecuteAsync");
}

// See https://aka.ms/new-console-template for more information
using Library;
using System.Diagnostics;

#pragma warning disable  // Local function is declared but never used

await StreamLongRunningWithTimeoutAsync();

static void LongRunningWithTimeout()
{
    CommandLineInfo info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnCancelKeyPress = true,
    };

    int exitCode = info.Execute(TimeSpan.FromSeconds(3));
    Console.WriteLine($"Process exited with: {exitCode}");
}

static async Task LongRunningWithTimeoutAsync()
{
    CommandLineInfo info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnCancelKeyPress = true,
    };

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
    int exitCode = await info.ExecuteAsync(cts.Token);
    Console.WriteLine($"Process exited with: {exitCode}");
}

static void LongRunningWithCtrlC()
{
    CommandLineInfo info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnCancelKeyPress = true,
    };

    int exitCode = info.Execute();
    Console.WriteLine($"Process exited with: {exitCode}");
}

static void StartAndWaitForExit()
{
    // If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
    ProcessStartInfo info = new()
    {
        FileName = "dotnet",
        ArgumentList = { "--help" },
    };

    using Process process = Process.Start(info)!;
    process.WaitForExit();
}

static void Execute()
{
    // If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
    CommandLineInfo info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    int exitCode = info.Execute();
    Console.WriteLine($"Process exited with: {exitCode}");
}

static async Task ExecuteAsync()
{
    // If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
    CommandLineInfo info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    int exitCode = await info.ExecuteAsync();
    Console.WriteLine($"Process exited with: {exitCode}");
}

static void RedirectToFileShell()
{
    using (Process process = new())
    {
        process.StartInfo.FileName = @"c:\windows\system32\cmd.exe";
        process.StartInfo.Arguments = $"/c \"dotnet --help > shell.txt\"";

        process.Start();

        process.WaitForExit();
    }
}

static void RedirectToFile()
{
    CommandLineInfo info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    info.RedirectToFiles(inputFile: null, outputFile: "custom.txt", errorFile: null);
}

static async Task StreamAsync()
{
    CommandLineInfo info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    var output = info.ReadOutputAsync();
    await foreach (var line in output)
    {
        if (line.StandardError)
        {
            Console.Error.WriteLine($"ERR: {line.Content}");
        }
        else
        {
            Console.WriteLine($"OUT: {line.Content}");
        }
    }
    Console.WriteLine($"Process {output.ProcessId} exited with: {output.ExitCode}");
}

static async Task StreamLongRunningWithTimeoutAsync()
{
    CommandLineInfo info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnCancelKeyPress = true,
    };

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
    await foreach (var line in info.ReadOutputAsync().WithCancellation(cts.Token))
    {
        Console.WriteLine(line.Content);
    }
}
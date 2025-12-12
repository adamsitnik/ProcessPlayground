// See https://aka.ms/new-console-template for more information
using System.TBA;
using System.Diagnostics;

#pragma warning disable  // Local function is declared but never used

ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

CombinedOutput output = ChildProcess.GetCombinedOutput(info, timeout: TimeSpan.FromSeconds(2));
Console.WriteLine(output.ExitCode);

static void LongRunningWithTimeout()
{
    ProcessStartOptions info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnParentDeath = true,
    };

    int exitCode = ChildProcess.Execute(info, TimeSpan.FromSeconds(3));
    Console.WriteLine($"Process exited with: {exitCode}");
}

static async Task LongRunningWithTimeoutAsync()
{
    ProcessStartOptions info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnParentDeath = true,
    };

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
    int exitCode = await ChildProcess.ExecuteAsync(info, cts.Token);
    Console.WriteLine($"Process exited with: {exitCode}");
}

static void LongRunningWithCtrlC()
{
    ProcessStartOptions info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnParentDeath = true,
    };

    int exitCode = ChildProcess.Execute(info);
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
    ProcessStartOptions info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    int exitCode = ChildProcess.Execute(info);
    Console.WriteLine($"Process exited with: {exitCode}");
}

static async Task ExecuteAsync()
{
    // If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
    ProcessStartOptions info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    int exitCode = await ChildProcess.ExecuteAsync(info);
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
    ProcessStartOptions info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    ChildProcess.RedirectToFiles(info, inputFile: null, outputFile: "custom.txt", errorFile: null);
}

static async Task StreamAsync()
{
    ProcessStartOptions info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    var output = ChildProcess.ReadOutputLines(info);
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

static void OldReprint()
{
    using (Process process = new())
    {
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.Arguments = "--help";
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.OutputDataReceived += static (sender, e) => Console.WriteLine($"OUT: {e.Data}");
        process.ErrorDataReceived += static (sender, e) => Console.Error.WriteLine($"ERR: {e.Data}");

        process.Start();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit();
    }
}

static void StreamSync()
{
    ProcessStartOptions info = new("dotnet")
    {
        Arguments = { "--help" },
    };

    var output = ChildProcess.ReadOutputLines(info);
    foreach (var line in output)
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
    ProcessStartOptions info = new("ping")
    {
        Arguments = { "microsoft.com", "-t" /* Ping the specified host until stopped */ },
        KillOnParentDeath = true,
    };

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
    await foreach (var line in ChildProcess.ReadOutputLines(info).WithCancellation(cts.Token))
    {
        Console.WriteLine(line.Content);
    }
}
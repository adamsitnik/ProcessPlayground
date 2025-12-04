// See https://aka.ms/new-console-template for more information
using Library;
using System.Diagnostics;

#pragma warning disable  // Local function is declared but never used

RedirectToFileShell();
//await StreamAllAsync();

static void NoRedirectionBuiltIn()
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

static void NoRedirectionCustom()
{
    // If you don’t set RedirectStandardOutput = true, .NET does not create a pipe for you. The child process simply uses the inherited handle.
    CommandLineInfo info = new(new("dotnet"))
    {
        Arguments = { "--help" },
    };

    info.Execute();
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
    CommandLineInfo info = new(new("dotnet"))
    {
        Arguments = { "--help" },
    };

    info.RedirectToFile("custom.txt");
}

static async Task StreamAsync()
{
    CommandLineInfo info = new(new("dotnet"))
    {
        Arguments = { "--help" },
    };

    await foreach (var (line, isError) in info.ReadLinesAsync())
    {
        if (isError)
        {
            Console.Error.WriteLine($"ERR: {line}");
        }
        else
        {
            Console.WriteLine($"OUT: {line}");
        }
    }
}

static async Task StreamAllAsync()
{
    CommandLineInfo info = new(new("dotnet"))
    {
        Arguments = { "--help" },
    };

    await foreach (string line in info.ReadAllLinesAsync())
    {
        Console.WriteLine($"ALL: {line}");
    }
}
// See https://aka.ms/new-console-template for more information
using Library;
using System.Diagnostics;

#pragma warning disable  // Local function is declared but never used

RedirectToFileShell();
RedirectToFile();

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
        process.StartInfo.Arguments = $"/k \"dotnet --help > shell.txt\"";

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
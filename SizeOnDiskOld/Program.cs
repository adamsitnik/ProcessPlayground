using System.Diagnostics;

ProcessStartInfo info = new()
{
    FileName = "dotnet",
    ArgumentList = { "--help" },
};

using Process process = Process.Start(info)!;
process.WaitForExit();

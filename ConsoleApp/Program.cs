using System;
using System.TBA;

Console.WriteLine("Starting test...");

try
{
    var options = new ProcessStartOptions("echo")
    {
        Arguments = { "Hello World!" }
    };
    
    Console.WriteLine("Created options");
    
    int exitCode = ChildProcess.Execute(options);
    Console.WriteLine($"Exit code: {exitCode}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex}");
}

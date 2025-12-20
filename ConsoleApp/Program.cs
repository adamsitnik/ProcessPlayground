using System.TBA;

ProcessStartOptions info = new("pwd");

int exitCode = await ChildProcess.ExecuteAsync(info);
Console.WriteLine(exitCode);

using System.TBA;

ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

return ChildProcess.Inherit(info).ExitCode;
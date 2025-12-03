using Microsoft.Win32.SafeHandles;

namespace Library;

public class CommandLineInfo
{
    private List<string>? _arguments;
    private Dictionary<string, string?>? _envVars;

    // More or less same as ProcessStartInfo
    public string FileName { get; }
    public IList<string> Arguments => _arguments ??= new();
    public IDictionary<string, string?> Environment => _envVars ??= new();
    public DirectoryInfo? WorkingDirectory { get; set; }

    // New
    public bool KillOnCtrlC { get; set; }

    // Need to ensure it's possible to implement on Unix
    public bool CreateNoWindow { get; set; }

    // Not ported on purpose
    // UseShellExecute: a lot of security concerns, not required for CLIs

    public CommandLineInfo(string fileName) => FileName = fileName;

    public void Execute()
    {
        using SafeFileHandle inputHandle = new(Interop.Kernel32.GetStdHandle(Interop.Kernel32.HandleTypes.STD_INPUT_HANDLE), false);
        using SafeFileHandle outputHandle = new(Interop.Kernel32.GetStdHandle(Interop.Kernel32.HandleTypes.STD_OUTPUT_HANDLE), false);
        using SafeFileHandle errorHandle = new(Interop.Kernel32.GetStdHandle(Interop.Kernel32.HandleTypes.STD_ERROR_HANDLE), false);

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        ProcessUtils.WaitForExit(procHandle, Timeout.Infinite);
    }

    public unsafe void DiscardOutput()
    {
        using SafeFileHandle nullHandle = Interop.Kernel32.CreateFile(
            "NUL",
            Interop.Kernel32.GenericOperations.GENERIC_WRITE,
            FileShare.ReadWrite,
            null,
            FileMode.Open,
            0,
            IntPtr.Zero);

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, nullHandle, nullHandle, nullHandle);
        ProcessUtils.WaitForExit(procHandle, Timeout.Infinite);
    }

    public void RedirectToFile(string outputFile)
    {
        using SafeFileHandle inputHandle = new(Interop.Kernel32.GetStdHandle(Interop.Kernel32.HandleTypes.STD_INPUT_HANDLE), false);
        using SafeFileHandle outputHandle = File.OpenHandle(outputFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Inheritable);
        using SafeFileHandle errorHandle = new(Interop.Kernel32.GetStdHandle(Interop.Kernel32.HandleTypes.STD_ERROR_HANDLE), false);

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        ProcessUtils.WaitForExit(procHandle, Timeout.Infinite);
    }
}

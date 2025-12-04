using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;

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
        using SafeFileHandle inputHandle = GetStdInputHandle();
        using SafeFileHandle outputHandle = GetStdOutputHandle();
        using SafeFileHandle errorHandle = GetStdErrorHandle();

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        ProcessUtils.WaitForExit(procHandle, Timeout.Infinite);
    }

    public void DiscardOutput()
    {
        using SafeFileHandle nullHandle = OpenNullHandle();

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, nullHandle, nullHandle, nullHandle);
        ProcessUtils.WaitForExit(procHandle, Timeout.Infinite);
    }

    public void RedirectToFile(string outputFile, string? errorFile = null)
    {
        using SafeFileHandle inputHandle = GetStdInputHandle();
        using SafeFileHandle outputHandle = File.OpenHandle(outputFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Inheritable);
        using SafeFileHandle errorHandle = errorFile switch
        {
            null => GetStdErrorHandle(),
            _ when errorFile == outputFile => outputHandle,
            _ => File.OpenHandle(errorFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Inheritable),
        };

        using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle, errorHandle);
        ProcessUtils.WaitForExit(procHandle, Timeout.Infinite);
    }

    // TODO: implement a class that implements IAsyncEnumerable<string> and exposes Process ID and Exit Code via properties
    public async IAsyncEnumerable<(string line, bool error)> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ProcessUtils.CreatePipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle, parentInputs: false);
        ProcessUtils.CreatePipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle, parentInputs: false);

        using SafeFileHandle inputHandle = GetStdInputHandle();
        using (parentOutputHandle)
        using (childOutputHandle)
        using (childErrorHandle)
        using (parentErrorHandle)
        {
            using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, childOutputHandle, childErrorHandle);

            // CRITICAL: Close the child handles in the parent process
            // so the pipe will signal EOF when the child exits
            childOutputHandle.Close();
            childErrorHandle.Close();

            using StreamReader outputReader = new(new FileStream(parentOutputHandle, FileAccess.Read, bufferSize: 0), detectEncodingFromByteOrderMarks: false);
            using StreamReader errorReader = new(new FileStream(parentErrorHandle, FileAccess.Read, bufferSize: 0), detectEncodingFromByteOrderMarks: false);

            Task<string?> readOutput = outputReader.ReadLineAsync(cancellationToken).AsTask();
            Task<string?> readError = errorReader.ReadLineAsync(cancellationToken).AsTask();

            while (true)
            {
                Task completedTask = await Task.WhenAny(readOutput, readError);

                bool isError = completedTask == readError;
                string? line = await (isError ? readError : readOutput);
                if (line is null)
                {
                    break;
                }

                yield return (line, isError);

                if (isError)
                {
                    readError = errorReader.ReadLineAsync(cancellationToken).AsTask();
                }
                else
                {
                    readOutput = outputReader.ReadLineAsync(cancellationToken).AsTask();
                }
            }
        }
    }

    public async IAsyncEnumerable<string> ReadAllLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ProcessUtils.CreatePipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle, parentInputs: false);

        using SafeFileHandle inputHandle = GetStdInputHandle();
        using (parentOutputHandle)
        using (childOutputHandle)
        {
            // Redirect both stdout and stderr to the same pipe!
            using SafeProcessHandle procHandle = ProcessUtils.StartCore(this, inputHandle, outputHandle: childOutputHandle, errorHandle: childOutputHandle);

            // CRITICAL: Close the child handles in the parent process
            // so the pipe will signal EOF when the child exits
            childOutputHandle.Close();

            using StreamReader outputReader = new(new FileStream(parentOutputHandle, FileAccess.Read, bufferSize: 0), detectEncodingFromByteOrderMarks: false);
            string? line = null;

            while ((line = await outputReader.ReadLineAsync(cancellationToken)) is not null)
            {
                yield return line;
            }
        }
    }

    private static SafeFileHandle GetStdErrorHandle() => GetStdHandle(Interop.Kernel32.HandleTypes.STD_ERROR_HANDLE);

    private static SafeFileHandle GetStdOutputHandle() => GetStdHandle(Interop.Kernel32.HandleTypes.STD_OUTPUT_HANDLE);

    private static SafeFileHandle GetStdInputHandle() => GetStdHandle(Interop.Kernel32.HandleTypes.STD_INPUT_HANDLE);

    private static SafeFileHandle GetStdHandle(int handleType) => new(Interop.Kernel32.GetStdHandle(handleType), false);

    private static unsafe SafeFileHandle OpenNullHandle() => Interop.Kernel32.CreateFile(
        "NUL",
        Interop.Kernel32.GenericOperations.GENERIC_WRITE,
        FileShare.ReadWrite | FileShare.Inheritable,
        null,
        FileMode.Open,
        0,
        IntPtr.Zero);
}

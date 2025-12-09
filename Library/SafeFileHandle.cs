namespace Microsoft.Win32.SafeHandles;

public static partial class SafeFileHandleExtensions
{
    extension(SafeFileHandle handle)
    {
        /// <summary>
        /// Returns true if the handle represents a socket, a named pipe, or an anonymous pipe.
        /// </summary>
        public bool IsPipe() => !handle.IsInvalid && !handle.IsClosed && IsPipeCore(handle);
    }
}

using System;

namespace System;

// Polyfill for .NET Framework to provide ArgumentNullException.ThrowIfNull extension member
public static partial class ArgumentNullExceptionExtensions
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull(object? argument, string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }
}

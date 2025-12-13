using System;

namespace System;

// Polyfill for .NET Framework to provide ArgumentException.ThrowIfNullOrEmpty extension member
public static partial class ArgumentExceptionExtensions
{
    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty(string? argument, string? paramName = null)
        {
            if (string.IsNullOrEmpty(argument))
            {
                throw new ArgumentException("Value cannot be null or empty.", paramName);
            }
        }
    }
}

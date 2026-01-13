using System;
using System.Runtime.CompilerServices;

namespace System;

// Polyfill for .NET Framework to provide ArgumentException.ThrowIfNullOrEmpty extension member
public static partial class ArgumentExceptionExtensions
{
    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty(string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
            
            if (argument.Length == 0)
            {
                throw new ArgumentException("Value cannot be empty.", paramName);
            }
        }
    }
}

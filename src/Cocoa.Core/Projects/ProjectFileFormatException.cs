using System;

namespace Cocoa.Projects
{
    public sealed class ProjectFileFormatException : Exception
    {
        public ProjectFileFormatException(string message)
            : base(message)
        {
        }

        public ProjectFileFormatException(string message, int lineNumber)
            : base($"{message} (line {lineNumber})")
        {
        }
    }
}

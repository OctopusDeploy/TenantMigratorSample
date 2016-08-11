using System;

namespace Octopus.TenantMigrator.Infrastructure
{
    public class CommandException : Exception
    {
        public CommandException(string message)
            : base(message)
        {
        }
    }
}
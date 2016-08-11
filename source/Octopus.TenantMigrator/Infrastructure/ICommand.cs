using System.IO;

namespace Octopus.TenantMigrator.Infrastructure
{
    public interface ICommand
    {
        void GetHelp(TextWriter writer);
        void Execute(string[] commandLineArguments);
    }
}
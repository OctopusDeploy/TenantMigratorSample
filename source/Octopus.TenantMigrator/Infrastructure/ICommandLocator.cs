namespace Octopus.TenantMigrator.Infrastructure
{
    public interface ICommandLocator
    {
        ICommandMetadata[] List();
        ICommand Find(string name);
    }
}
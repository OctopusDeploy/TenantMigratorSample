using Octopus.Client;

namespace Octopus.TenantMigrator.Integration
{
    public interface IOctopusRepositoryFactory
    {
        IOctopusRepository CreateRepository(OctopusServerEndpoint endpoint);
    }
}

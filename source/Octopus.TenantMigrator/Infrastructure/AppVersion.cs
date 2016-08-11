using System.Reflection;
using Octopus.TenantMigrator.Extensions;

namespace Octopus.TenantMigrator.Infrastructure
{
    public class AppVersion
    {
        readonly SemanticVersionInfo semanticVersionInfo;

        public AppVersion(Assembly assembly)
            : this(assembly.GetSemanticVersionInfo())
        {
        }

        public AppVersion(SemanticVersionInfo semanticVersionInfo)
        {
            this.semanticVersionInfo = semanticVersionInfo;
        }

        public override string ToString()
        {
            return semanticVersionInfo.NuGetVersion;
        }
    }
}
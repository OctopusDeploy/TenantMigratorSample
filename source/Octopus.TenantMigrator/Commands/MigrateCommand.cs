using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Octopus.Client;
using Octopus.Client.Editors;
using Octopus.Client.Editors.DeploymentProcess;
using Octopus.Client.Model;
using Octopus.Client.Model.Endpoints;
using Octopus.TenantMigrator.Infrastructure;
using Octopus.TenantMigrator.Integration;
using Serilog;

namespace Octopus.TenantMigrator.Commands
{
    [Command("migrate", Description = "Migrates environments pretending to be tenants into real-life tenants in Octopus.")]
    public class MigrateCommand : ApiCommand
    {
        private const int DefaultNumberOfProjects = 20;
        private const int DefaultNumberOfCustomers = 50;
        private const int DefaultNumberOfTesters = 10;

        private static readonly ILogger Log = Serilog.Log.ForContext<MigrateCommand>();

        private string Include = null;
        private string Exclude = null;

        public MigrateCommand(IOctopusRepositoryFactory octopusRepositoryFactory)
            : base(octopusRepositoryFactory)
        {
            var options = Options.For("Multi-tenant sample");
            options.Add("include=", $"[Optional] Include environments where the name matches this regex. Default is to migrate all environments.", v => Include = v);
            options.Add("exclude=", $"[Optional] Exclude environments where the name matches this regex. Default is to migrate all environments.", v => Exclude = v);
        }

        protected override void Execute()
        {
            // Step 1: Ensure the Multi-Tenant Deployments feature is enabled in Octopus Deploy
            EnsureMultitenancyFeature();

            // Step 2: Find the environments to migrate
            // In your situation you may want to filter based on a naming convention, or all environments in a certain Lifecycle Phase...
            var environmentsToMigrate = GetEnvironmentsToMigrate();

            // Step X: Make sure the target environments exist.
            // In this case we are building environments based on a naming convention where the Source Environments are named like "{TenantName} - {EnvironmentName}".
            // For example, "Customer 1 - Staging" and "Customer 1 - Production" would indicate there is a Tenant called "Customer 1" that will target two environments called "Staging" and "Production" respectively.
            // In your situation you may want to create a static set of environments... or something similar.
            var targetEnvironments = SetUpTargetEnvironments(environmentsToMigrate);

            // Step X: Inject the target environments into the appropriate Lifecycle Phases
            InjectTargetEnvironmentsIntoLifecycles(environmentsToMigrate, targetEnvironments);

            // Step 3: Make sure all of our tags are configured correctly
            // In your situation you may want different tags - these are just some ideas from our samples
            // For more information see http://g.octopushq.com/MultiTenantTags
            var allTags = SetUpTags();
            var getTag = new Func<string, TagResource>(name => allTags.FirstOrDefault(t => t.Name == name));

            // Step 4: Set up library variable templates and values
            // In your situation you will certainly need different variable templates and values! These examples show you how to set up your own.
            // For more information see http://g.octopushq.com/MultiTenantVariables
            var libraryVariableSets = SetUpLibraryVariableSets();
            
            // Step X: Set up the tenants, and tag them using some kind of convention
            var tenantEnvironmentMap = CreateTenantsFromEnvironments(environmentsToMigrate, getTag);

            // Step X: Connect each tenant to the correct projects and environments.
            // In this example we will find projects connected to the source environments via Lifecycle, and connect the resulting tenants to those projects.
            // This will have the same end-result as the original "environments pretending to be tenants" approach.
            var allProjects = Repository.Projects.GetAll().ToArray();
            ConnectTenantsToProjectsAndEnvironments(tenantEnvironmentMap, allProjects, targetEnvironments);

            // Step X: Set up dedicated hosting for tenants using any deployment targets belonging to the "environment pretending to be a tenant" environment
            // For more information see http://g.octopushq.com/MultiTenantHostingModel
            foreach (var customer in customers.Where(c => c.IsVIP()))
            {
                Log.Information("Setting up dedicated hosting for {VIP}...", customer.Name);
                var dedicatedHosts = Enumerable.Range(0, 2).Select(i => Repository.Machines.CreateOrModify(
                    $"{customer.Name} Host {i}",
                    new CloudRegionEndpointResource(),
                    GetEnvironmentsForCustomer(allEnvironments, customer),
                    new[] {"web-server"},
                    new[] {customer},
                    new TagResource[0]))
                    .ToArray();
            }

            Log.Information("Building {Count} sample projects...", NumberOfProjects);
            var projects = Enumerable.Range(0, NumberOfProjects)
                .Select(i => new { Name = ProjectNames[i], Alias = ProjectNames[i].ToLowerInvariant() })
                .Select((x, i) =>
                {
                    Log.Information("Setting up project {ProjectName}...", x.Name);
                    var projectEditor = Repository.Projects.CreateOrModify(x.Name, projectGroup, normalLifecycle, LipsumTheRaven.GenerateLipsum(2))
                    .SetLogo(SampleImageCache.GetRobotImage(x.Name))
                        .IncludingLibraryVariableSets(libraryVariableSets);

                    projectEditor.VariableTemplates
                        .Clear()
                        .AddOrUpdateSingleLineTextTemplate("Tenant.Database.Name", "Database name", $"{x.Alias}-#{{Environment.Alias}}-#{{Tenant.Alias}}", $"The environment-specific name of the {x.Name} database for this tenant.")
                        .AddOrUpdateSingleLineTextTemplate("Tenant.Database.UserID", "Database username", $"{x.Alias}-#{{Environment.Alias}}-#{{Tenant.Alias}}", "The User ID used to connect to the tenant database.")
                        .AddOrUpdateSensitiveTemplate(VariableKeys.ProjectTenantVariables.TenantDatabasePassword, "Database password", defaultValue: null, helpText: "The password used to connect to the tenant database.")
                        .AddOrUpdateSingleLineTextTemplate("Tenant.Domain.Name", "Domain name", $"#{{Tenant.Alias}}.{x.Alias}.com", $"The environment-specific domain name for the {x.Name} web application for this tenant.");

                    projectEditor.Variables
                        .AddOrUpdateVariableValue("DatabaseConnectionString", $"Server=db.{x.Alias}.com;Database=#{{Tenant.Database.Name}};User ID=#{{Tenant.Database.UserID}};Password=#{{Tenant.Database.Password}};")
                        .AddOrUpdateVariableValue("HostURL", "https://#{Tenant.Domain.Name}");

                    // Create the channels for the sample project
                    projectEditor.Channels.CreateOrModify("1.x Normal", "The channel for stable releases that will be deployed to our production customers.")
                        .SetAsDefaultChannel()
                        .RestrictToTenants(getTag("Tester"), getTag("Early adopter"), getTag("Stable"))
                        .Save();
                    var betaChannelEditor = projectEditor.Channels.CreateOrModify("2.x Beta", "The channel for beta releases that will be deployed to our beta customers.")
                        .UsingLifecycle(betaLifecycle)
                        .RestrictToTenants(getTag("2.x Beta"));

                    // Delete the "default channel" if it exists
                    projectEditor.Channels.Delete("Default");

                    // Rebuild the process from scratch
                    projectEditor.DeploymentProcess.ClearSteps();

                    projectEditor.DeploymentProcess.AddOrUpdateStep("Deploy Application")
                        .TargetingRoles("web-server")
                        .AddOrUpdateScriptAction("Deploy Application", new InlineScriptFromFileInAssembly("MultiTenantSample.Deploy.ps1"), ScriptTarget.Target);

                    projectEditor.DeploymentProcess.AddOrUpdateStep("Deploy 2.x Beta Component")
                        .TargetingRoles("web-server")
                        .AddOrUpdateScriptAction("Deploy 2.x Beta Component", new InlineScriptFromFileInAssembly("MultiTenantSample.DeployBetaComponent.ps1"), ScriptTarget.Target)
                        .ForChannels(betaChannelEditor.Instance);

                    projectEditor.DeploymentProcess.AddOrUpdateStep("Notify VIP Contact")
                        .AddOrUpdateScriptAction("Notify VIP Contact", new InlineScriptFromFileInAssembly("MultiTenantSample.NotifyContact.ps1"), ScriptTarget.Server)
                        .ForTenantTags(getTag("VIP"));

                    projectEditor.Save();

                    return projectEditor.Instance;
                })
            .ToArray();

            Log.Information("Created {CustomerCount} customers and {TesterCount} testers and {ProjectCount} projects.",
                customers.Length, testers.Length, projects.Length);
            Log.Information("Customer tagging conventions: Names with 'v' will become 'VIP' (with dedicated hosting), names with 'u' will become 'Trial', names with 'e' will become 'Early adopter', everyone else will be 'Standard' and assigned to a random shared server pool.");
        }

        private void InjectTargetEnvironmentsIntoLifecycles(EnvironmentResource[] sourceEnvironments, EnvironmentResource[] targetEnvironments)
        {
            // Where we find a source environment, we should add the resulting target environment to the same Lifecycle Phase
            var allLifecycles = Repository.Lifecycles.FindAll();
            foreach (var lifecycle in allLifecycles)
            {
                foreach (var phase in lifecycle.Phases)
                {
                    var autoSourceEnvironments =
                        sourceEnvironments.Where(source => phase.AutomaticDeploymentTargets
                            .Any(id => string.Equals(id, source.Id, StringComparison.OrdinalIgnoreCase)))
                            .ToArray();

                    var autoTargetEnvironmentNames =
                        autoSourceEnvironments.Select(GetTargetEnvironmentNameFromSourceEnvironment).Distinct();
                    var autoTargetEnvironments =
                        targetEnvironments.Where(target => autoTargetEnvironmentNames.Contains(target.Name, StringComparer.OrdinalIgnoreCase));
                        
                    foreach (var autoTarget in autoTargetEnvironments)
                    {
                        phase.WithAutomaticDeploymentTargets(autoTarget);
                    }

                    var optionalSourceEnvironments =
                        sourceEnvironments.Where(source => phase.AutomaticDeploymentTargets
                            .Any(id => string.Equals(id, source.Id, StringComparison.OrdinalIgnoreCase)))
                            .ToArray();

                    var optionalTargetEnvironmentNames =
                        optionalSourceEnvironments.Select(GetTargetEnvironmentNameFromSourceEnvironment).Distinct();
                    var optionalTargetEnvironments =
                        targetEnvironments.Where(target => optionalTargetEnvironmentNames.Contains(target.Name, StringComparer.OrdinalIgnoreCase));

                    foreach (var optionalTarget in optionalTargetEnvironments)
                    {
                        phase.WithAutomaticDeploymentTargets(optionalTarget);
                    }
                }

                Repository.Lifecycles.Modify(lifecycle);
            }
        }

        private void ConnectTenantsToProjectsAndEnvironments(
            Dictionary<TenantEditor, EnvironmentResource[]> tenantEnvironmentMap,
            ProjectResource[] allProjects,
            EnvironmentResource[] targetEnvironments)
        {
            var allChannels = Repository.Channels.FindAll();
            var allLifecycles = Repository.Lifecycles.FindAll();
            foreach (var project in allProjects)
            {
                var channels = allChannels.Where(c => c.ProjectId == project.Id).ToArray();
                var connectedLifecycleIds =
                    new[] {project.LifecycleId}.Concat(channels.Select(c => c.LifecycleId).Where(id => id != null))
                        .Distinct().ToArray();
                var connectedLifecycles = allLifecycles.Where(l => connectedLifecycleIds.Contains(l.Id)).ToArray();

                foreach (var tenantMap in tenantEnvironmentMap)
                {
                    if (connectedLifecycles.Any(l => LifecycleContainsAnyOfTheseEnvironments(l, tenantMap.Value)))
                    {
                        var targets = targetEnvironments.Where(e => connectedLifecycles.Any(l => LifecycleContainsAnyOfTheseEnvironments(l, e))).ToArray();
                        Log.Information("Connecting {Tenant} to {Project} deploying to {Environments}",
                            tenantMap.Key.Instance.Name, project, targets.Select(e => e.Name));
                        tenantMap.Key.ConnectToProjectAndEnvironments(project, targets);
                    }
                }
            }

            // Save all of the tenants now we've connected them to the project/environment combinations
            foreach (var tenantMap in tenantEnvironmentMap)
            {
                tenantMap.Key.Save();
            }
        }

        public bool LifecycleContainsAnyOfTheseEnvironments(LifecycleResource lifecycle, params EnvironmentResource[] environments)
        {
            return lifecycle.Phases.Any(p =>
                p.AutomaticDeploymentTargets.Intersect(environments.Select(e => e.Id)).Any() ||
                p.OptionalDeploymentTargets.Intersect(environments.Select(e => e.Id)).Any());
        }

        void EnsureMultitenancyFeature()
        {
            Log.Information("Ensuring multi-tenant deployments are enabled...");
            var features = Repository.FeaturesConfiguration.GetFeaturesConfiguration();
            features.IsMultiTenancyEnabled = true;
            Repository.FeaturesConfiguration.ModifyFeaturesConfiguration(features);
            Repository.Client.RefreshRootDocument();
        }

        private EnvironmentResource[] GetEnvironmentsToMigrate()
        {
            EnvironmentResource[] environmentsToMigrate;
            var allEnvironments = Repository.Environments.GetAll();
            if (Include == null && Exclude == null)
            {
                Log.Information("Migrating ALL environments...");
                environmentsToMigrate = allEnvironments.ToArray();
            }
            else
            {
                Log.Information($"Migrating matching environments: Include='{Include}' Exclude='{Exclude}'");
                var includeRegex = new Regex(Include ?? ".*",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
                var excludeRegex = new Regex(Exclude ?? "^$",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

                environmentsToMigrate =
                    allEnvironments.Where(e => includeRegex.IsMatch(e.Name) && !excludeRegex.IsMatch(e.Name)).ToArray();
                Log.Information($"Matching environments:{Environment.NewLine}{environmentsToMigrate.NewLineSeperate()}");
            }
            return environmentsToMigrate;
        }

        private EnvironmentResource[] SetUpTargetEnvironments(EnvironmentResource[] environmentsToMigrate)
        {
            var targetEnvironmentNames = environmentsToMigrate.Select(GetTargetEnvironmentNameFromSourceEnvironment).Distinct().OrderBy(x => x).ToArray();
            Log.Information("Setting up target environments {Environments}...", targetEnvironmentNames);
            return targetEnvironmentNames.Select(name => Repository.Environments.CreateOrModify(name).Instance).ToArray();
        }

        private TagResource[] SetUpTags()
        {
            Log.Information("Setting up tags...");
            var tagSetTenantType = Repository.TagSets.CreateOrModify("Tenant type", "Allows you to categorize tenants")
                .AddOrUpdateTag("Internal", "These are internal tenants, like our test team")
                .AddOrUpdateTag("External", "These are external tenants, our real customers",
                    TagResource.StandardColor.LightBlue)
                .Save().Instance;
            var tagSetImportance =
                Repository.TagSets.CreateOrModify("Tenant importance",
                    "Allows you to have different customers that we should pay more or less attention to")
                    .AddOrUpdateTag("VIP", "Very important tenant - pay attention!", TagResource.StandardColor.DarkRed)
                    .AddOrUpdateTag("Standard", "These are our standard customers")
                    .AddOrUpdateTag("Trial", "These are trial customers", TagResource.StandardColor.DarkPurple)
                    .Save().Instance;
            var tagSetRing =
                Repository.TagSets.CreateOrModify("Upgrade ring", "What kind of upgrade stability to these customers want")
                    .AddOrUpdateTag("Tester", "These are our internal test team members", TagResource.StandardColor.LightCyan)
                    .AddOrUpdateTag("Early adopter", "Upgrade these customers first", TagResource.StandardColor.LightYellow)
                    .AddOrUpdateTag("Stable", "Upgrade these customers last", TagResource.StandardColor.LightGreen)
                    .AddOrUpdateTag("Pinned", "Don't upgrade these customers until they come back to the stable ring",
                        TagResource.StandardColor.DarkRed)
                    .Save().Instance;
            var tagSetHosting =
                Repository.TagSets.CreateOrModify("Hosting", "Allows you to define where the tenant software should be hosted")
                    .AddOrUpdateTag("Internal-Shared-Farm", "The internal test server farm")
                    .AddOrUpdateTag("Shared-Farm-1", "Shared server farm 1", TagResource.StandardColor.LightGreen)
                    .AddOrUpdateTag("Shared-Farm-2", "Shared server farm 2", TagResource.StandardColor.LightGreen)
                    .AddOrUpdateTag("Shared-Farm-3", "Shared server farm 3", TagResource.StandardColor.LightGreen)
                    .AddOrUpdateTag("Shared-Farm-4", "Shared server farm 4", TagResource.StandardColor.LightGreen)
                    .AddOrUpdateTag("Dedicated", "This customer will have their own dedicated hardware",
                        TagResource.StandardColor.DarkRed)
                    .Save().Instance;

            var allTags = new TagResource[0]
                .Concat(tagSetTenantType.Tags)
                .Concat(tagSetHosting.Tags)
                .Concat(tagSetImportance.Tags)
                .Concat(tagSetRing.Tags)
                .ToArray();
            return allTags;
        }

        private LibraryVariableSetResource[] SetUpLibraryVariableSets()
        {
            var stdTenantVarEditor = Repository.LibraryVariableSets.CreateOrModify(
                "Standard tenant details",
                "The standard details we require for all tenants");
            stdTenantVarEditor.VariableTemplates
                .AddOrUpdateSingleLineTextTemplate(VariableKeys.StandardTenantDetails.TenantAlias, "Alias",
                    defaultValue: null, helpText: "This alias will be used to build convention-based settings for the tenant")
                .AddOrUpdateSelectTemplate(VariableKeys.StandardTenantDetails.TenantRegion, "Region",
                    Region.All.ToDictionary(x => x.Alias, x => x.DisplayName),
                    defaultValue: null, helpText: "The geographic region where this tenant will be hosted")
                .AddOrUpdateSingleLineTextTemplate(VariableKeys.StandardTenantDetails.TenantContactEmail, "Contact email",
                    defaultValue: null, helpText: "A comma-separated list of email addresses to send deployment notifications");
            stdTenantVarEditor.Save();

            return new[] {stdTenantVarEditor.Instance};
        }

        private Dictionary<TenantEditor, EnvironmentResource[]> CreateTenantsFromEnvironments(EnvironmentResource[] environmentsToMigrate, Func<string, TagResource> getTag)
        {
            // This method will build tenants based on a naming convention "{TenantName} - {TargetEnvironment}".
            // This is one way you could group multiple source environments into a single tenant with multiple target environments.
            var customerMap = environmentsToMigrate
                .Select(e => new { SourceEnvironment = e, TenantName = GetTenantNameFromSourceEnvironment(e), TargetEnvironmentName = GetTargetEnvironmentNameFromSourceEnvironment(e) })
                .GroupBy(x => x.TenantName)
                .Select(g =>
                {
                    Log.Information("Setting up tenant {TenantName} based on the environment(s) {SourceEnvironments}...", g.Key, g.Select(x => x.SourceEnvironment.Name));
                    var tenantEditor = Repository.Tenants.CreateOrModify(g.Key);
                    TenantResource tenant = tenantEditor.Instance;

                    // You will likely have another way of tagging your tenants, this is here as an example of how you could tag tenants.
                    var isVIP = new Func<TenantResource, bool>(t => t.Name.ToLowerInvariant().Contains("v"));
                    var isEarlyAdopter = new Func<TenantResource, bool>(t => t.Name.ToLowerInvariant().Contains("e"));
                    tenant.WithTag(getTag("External"));
                    tenant.WithTag(isVIP(tenant) ? getTag("VIP") : getTag("Standard"));
                    tenant.WithTag(isEarlyAdopter(tenant) ? getTag("Early adopter") : getTag("Stable"));

                    return new { TenantEditor = tenantEditor.Save(), SourceEnvironments = g.Select(x => x.SourceEnvironment).ToArray() };
                })
                .ToDictionary(x => x.TenantEditor, x => x.SourceEnvironments);
            return customerMap;
        }

        private static string GetTenantNameFromSourceEnvironment(EnvironmentResource source)
        {
            return source.Name.Split(new[] { '-' }, 2)[0].Trim();
        }

        private static string GetTargetEnvironmentNameFromSourceEnvironment(EnvironmentResource source)
        {
            var split = source.Name.Split(new[] { '-' }, 2);
            return split.Length == 2 ? split[1].Trim() : "Production";
        }

        private void FillOutTenantVariablesByConvention(
            TenantEditor tenantEditor,
            ProjectResource[] projects,
            EnvironmentResource[] environments,
            LibraryVariableSetResource[] libraryVariableSets)
        {
            var tenant = tenantEditor.Instance;
            var projectLookup = projects.ToDictionary(p => p.Id);
            var libraryVariableSetLookup = libraryVariableSets.ToDictionary(l => l.Id);
            var environmentLookup = environments.ToDictionary(e => e.Id);

            var tenantVariables = tenantEditor.Variables.Instance;

            // Library variables
            foreach (var libraryVariable in tenantVariables.LibraryVariables)
            {
                foreach (var template in libraryVariableSetLookup[libraryVariable.Value.LibraryVariableSetId].Templates)
                {
                    var value = TryFillLibraryVariableByConvention(template, tenant);
                    if (value != null)
                    {
                        libraryVariable.Value.Variables[template.Id] = value;
                    }
                }
            }

            // Project variables
            foreach (var projectVariable in tenantVariables.ProjectVariables)
            {

                foreach (var template in projectLookup[projectVariable.Value.ProjectId].Templates)
                {
                    foreach (var connectedEnvironmentId in tenant.ProjectEnvironments[projectVariable.Value.ProjectId])
                    {
                        var environment = environmentLookup[connectedEnvironmentId];
                        var value = TryFillProjectVariableByConvention(template, tenant, environment);
                        if (value != null)
                        {
                            projectVariable.Value.Variables[connectedEnvironmentId][template.Id] = value;
                        }
                    }
                }
            }
        }

        private PropertyValueResource TryFillLibraryVariableByConvention(ActionTemplateParameterResource template, TenantResource tenant)
        {
            if (template.Name == VariableKeys.StandardTenantDetails.TenantAlias) return new PropertyValueResource(tenant.Name.Replace(" ", "-").ToLowerInvariant());
            if (template.Name == VariableKeys.StandardTenantDetails.TenantRegion) return new PropertyValueResource(Region.All.GetRandom().Alias);
            if (template.Name == VariableKeys.StandardTenantDetails.TenantContactEmail) return new PropertyValueResource(tenant.Name.Replace(" ", ".").ToLowerInvariant() + "@test.com");

            return null;
        }

        private PropertyValueResource TryFillProjectVariableByConvention(ActionTemplateParameterResource template, TenantResource tenant, EnvironmentResource environment)
        {
            if (template.Name == VariableKeys.ProjectTenantVariables.TenantDatabasePassword) return new PropertyValueResource(RandomStringGenerator.Generate(16), isSensitive: true);

            return null;
        }

        public class Region
        {
            public Region(string @alias, string displayName)
            {
                Alias = alias;
                DisplayName = displayName;
            }

            public string Alias { get; set; }
            public string DisplayName { get; set; }

            public static Region[] All =
            {
                new Region("AustraliaEast", "Australia East"),
                new Region("SoutheastAsia", "South East Asia"),
                new Region("WestUS", "West US"),
                new Region("EastUS", "East US"),
                new Region("WestEurope", "West Europe"),
            };
        }

        public static class VariableKeys
        {
            public static class StandardTenantDetails
            {
                public static readonly string TenantAlias = "Tenant.Alias";
                public static readonly string TenantRegion = "Tenant.Region";
                public static readonly string TenantContactEmail = "Tenant.ContactEmail";
            }

            public static class ProjectTenantVariables
            {
                public static readonly string TenantDatabasePassword = "Tenant.Database.Password";
            }
        }
    }
}
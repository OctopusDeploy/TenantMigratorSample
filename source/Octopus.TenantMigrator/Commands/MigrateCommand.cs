using System;
using System.Linq;
using System.Text.RegularExpressions;
using Octopus.Client;
using Octopus.Client.Editors;
using Octopus.Client.Model;
using Octopus.TenantMigrator.Infrastructure;
using Octopus.TenantMigrator.Integration;
using Serilog;

namespace Octopus.TenantMigrator.Commands
{
    [Command("migrate", Description = "Migrates environments pretending to be tenants into real-life tenants in Octopus.")]
    public class MigrateCommand : ApiCommand
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<MigrateCommand>();

        private string include;
        private string exclude;

        public MigrateCommand(IOctopusRepositoryFactory octopusRepositoryFactory)
            : base(octopusRepositoryFactory)
        {
            var options = Options.For("Multi-tenant sample");
            options.Add("include=", "[Optional] Include environments where the name matches this regex. Default is to migrate all environments.", v => include = v);
            options.Add("exclude=", "[Optional] Exclude environments where the name matches this regex. Default is to migrate all environments.", v => exclude = v);
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
            var environmentMap = SetUpEnvironments(environmentsToMigrate);

            // Step X: Inject the target environments into the appropriate Lifecycle Phases
            InjectTargetEnvironmentsIntoLifecycles(environmentMap);

            // Step 3: Make sure all of our tags are configured correctly
            // In your situation you may want different tags - these are just some ideas from our samples
            // For more information see http://g.octopushq.com/MultiTenantTags
            var allTags = SetUpTags();
            var getTag = new Func<string, TagResource>(name => allTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));

            // Step X: Set up the tenants, and tag them using some kind of convention
            var tenantEnvironmentMap = CreateTenantsFromEnvironments(environmentsToMigrate);

            // Step X: Set up library variable templates and values
            // In your situation you will certainly need different variable templates and values! These examples show you how to set up your own.
            // For more information see http://g.octopushq.com/MultiTenantVariables
            var libraryVariableSets = SetUpCommonVariableTemplates();

            // Step X: Connect each tenant to the correct projects and environments.
            // In this example we will find projects connected to the source environments via Lifecycle, and connect the resulting tenants to those projects.
            // This will have the same end-result as the original "environments pretending to be tenants" approach.
            var allProjects = Repository.Projects.GetAll().ToArray();
            ConnectTenantsToProjectsAndEnvironments(allProjects, tenantEnvironmentMap, environmentMap);

            // Step X: Set up hosting for tenants using any deployment targets belonging to the "environment pretending to be a tenant" environment
            // This will allocate tenants directly to deployment targets
            // For more information see http://g.octopushq.com/MultiTenantHostingModel
            var allDeploymentTargets = Repository.Machines.FindAll();
            foreach (var target in allDeploymentTargets)
            {
                var matchingSourceEnvironments =
                    environmentsToMigrate.Where(e => target.EnvironmentIds.Contains(e.Id))
                        .ToArray();

                var matchingTargetEnvironments =
                    environmentMap.GetTargetEnvironmentsForSources(matchingSourceEnvironments)
                        .ToArray();

                target.AddOrUpdateEnvironments(matchingTargetEnvironments);

                var matchingTenants = tenantEnvironmentMap.GetTenantsForSourceEnvironments(matchingSourceEnvironments);
                target.AddOrUpdateTenants(matchingTenants);

                Repository.Machines.Modify(target);
            }
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
            if (include == null && exclude == null)
            {
                Log.Information("Migrating ALL environments...");
                environmentsToMigrate = allEnvironments.ToArray();
            }
            else
            {
                Log.Information($"Migrating matching environments: Include='{include}' Exclude='{exclude}'");
                var includeRegex = new Regex(include ?? ".*",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
                var excludeRegex = new Regex(exclude ?? "^$",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

                environmentsToMigrate =
                    allEnvironments.Where(e => includeRegex.IsMatch(e.Name) && !excludeRegex.IsMatch(e.Name)).ToArray();
                Log.Information($"Matching environments:{Environment.NewLine}{environmentsToMigrate.NewLineSeperate()}");
            }
            return environmentsToMigrate;
        }

        private SourceToTargetEnvironmentMap SetUpEnvironments(EnvironmentResource[] environmentsToMigrate)
        {
            var existingEnvironments = Repository.Environments.GetAll().ToArray();
            var preEnvironmentMap = new SourceToTargetEnvironmentMap(environmentsToMigrate, existingEnvironments);
            if (!preEnvironmentMap.MissingTargetEnvironmentNames.Any())
            {
                Log.Information("All target environments already exist!");
                return preEnvironmentMap;
            }

            Log.Information("Setting up target environments {Environments}...", preEnvironmentMap.MissingTargetEnvironmentNames);
            var newEnvironments = preEnvironmentMap.MissingTargetEnvironmentNames.Select(name => Repository.Environments.CreateOrModify(name).Instance).ToArray();
            return new SourceToTargetEnvironmentMap(environmentsToMigrate, existingEnvironments.Concat(newEnvironments).ToArray());
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

        private LibraryVariableSetResource[] SetUpCommonVariableTemplates()
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

        private MigrateCommand.SourceEnvironmentToTenantMap CreateTenantsFromEnvironments(EnvironmentResource[] environmentsToMigrate)
        {
            // This method will build tenants based on a naming convention "{TenantName} - {TargetEnvironment}".
            // This is one way you could group multiple source environments into a single tenant with multiple target environments.
            var allTenants = Repository.Tenants.GetAll().ToArray();
            var preTenantMap = new MigrateCommand.SourceEnvironmentToTenantMap(environmentsToMigrate, allTenants);
            if (!preTenantMap.MissingTenantNames.Any())
            {
                Log.Information("All of the required tenants already exist!");
                return preTenantMap;
            }

            var newTenants = preTenantMap.MissingTenantNames
                .Select(name => new { TenantName = name, SourceEnvironments = preTenantMap.GetSourceEnvironmentsForTenantNames(name)})
                .Select(x =>
                {
                    Log.Information("Setting up tenant {TenantName} based on the environment(s) {SourceEnvironments}...", x.TenantName, x.SourceEnvironments.Select(e => e.Name));
                    var tenantEditor = Repository.Tenants.CreateOrModify(x.TenantName);
                    
                    // TODO: Move all the thingS!
                    //TenantResource tenant = tenantEditor.Instance;

                    //// You will likely have another way of tagging your tenants, this is here as an example of how you could tag tenants.
                    //var isVIP = new Func<TenantResource, bool>(t => t.Name.ToLowerInvariant().Contains("v"));
                    //var isEarlyAdopter = new Func<TenantResource, bool>(t => t.Name.ToLowerInvariant().Contains("e"));
                    //tenant.WithTag(getTag("External"));
                    //tenant.WithTag(isVIP(tenant) ? getTag("VIP") : getTag("Standard"));
                    //tenant.WithTag(isEarlyAdopter(tenant) ? getTag("Early adopter") : getTag("Stable"));

                    return tenantEditor.Save().Instance;
                }).ToArray();

            return new MigrateCommand.SourceEnvironmentToTenantMap(environmentsToMigrate, allTenants.Concat(newTenants).ToArray());
        }

        private void InjectTargetEnvironmentsIntoLifecycles(MigrateCommand.SourceToTargetEnvironmentMap sourceToTargetEnvironmentMap)
        {
            // Where we find a source environment, we should add the resulting target environment to the same Lifecycle Phase
            var allLifecycles = Repository.Lifecycles.FindAll();
            foreach (var lifecycle in allLifecycles)
            {
                foreach (var phase in lifecycle.Phases)
                {
                    phase.WithAutomaticDeploymentTargets(sourceToTargetEnvironmentMap.GetTargetEnvironmentsForSourceIds(phase.AutomaticDeploymentTargets.ToArray()));
                    phase.WithOptionalDeploymentTargets(sourceToTargetEnvironmentMap.GetTargetEnvironmentsForSourceIds(phase.OptionalDeploymentTargets.ToArray()));
                }

                Repository.Lifecycles.Modify(lifecycle);
            }
        }

        private void ConnectTenantsToProjectsAndEnvironments(
            ProjectResource[] allProjects, SourceEnvironmentToTenantMap sourceEnvironmentToTenantEnvironmentMap, SourceToTargetEnvironmentMap sourceToTargetEnvironmentMap)
        {
            var allChannels = Repository.Channels.FindAll();
            var allLifecycles = Repository.Lifecycles.FindAll();
            var tenants = Repository.Tenants.Get(sourceEnvironmentToTenantEnvironmentMap.TenantIds);
            foreach (var project in allProjects)
            {
                var projectChannels = allChannels.Where(c => c.ProjectId == project.Id);
                var projectChannelLifecycleIds = projectChannels.Select(c => c.LifecycleId).Where(id => id != null);
                var connectedLifecycleIds = new[] { project.LifecycleId }.Concat(projectChannelLifecycleIds).Distinct().ToArray();
                var connectedLifecycles = allLifecycles.Where(l => connectedLifecycleIds.Contains(l.Id)).ToArray();

                foreach (var tenant in tenants)
                {
                    // Figure out if any "environments pretending to be this tenant" were connected to this project
                    var connectedSourceEnvironmentsForTenant = sourceEnvironmentToTenantEnvironmentMap.GetSourceEnvironmentsForTenants(tenant)
                        .Where(source => connectedLifecycles.Any(l => LifecycleContainsAnyOfTheseEnvironments(l, source)))
                        .ToArray();

                    if (connectedSourceEnvironmentsForTenant.Any())
                    {
                        var targetEnvironmentsForTenant = sourceToTargetEnvironmentMap.GetTargetEnvironmentsForSources(connectedSourceEnvironmentsForTenant);
                        Log.Information("Connecting {Tenant} to {Project} deploying to {Environments}",
                            tenant.Name, project.Name, targetEnvironmentsForTenant.Select(e => e.Name));
                        tenant.ConnectToProjectAndEnvironments(project, targetEnvironmentsForTenant);
                    }
                }
            }

            // Ensure each tenanted project is configured for tenanted deployments
            // NOTE: Do this before attempting to connect tenants to avoid validation issues
            var tenantedProjects = allProjects.Where(p => tenants.SelectMany(t => t.ProjectEnvironments.Select(pe => pe.Key)).Contains(p.Id)).ToArray();
            foreach (var tenantedProject in tenantedProjects)
            {
                if (tenantedProject.TenantedDeploymentMode == ProjectTenantedDeploymentMode.Untenanted)
                {
                    tenantedProject.TenantedDeploymentMode = ProjectTenantedDeploymentMode.TenantedOrUntenanted;
                    Log.Information("Changing {Project} TenantedDeploymentMode to {Mode}", tenantedProject.Name, tenantedProject.TenantedDeploymentMode);
                    Repository.Projects.Modify(tenantedProject);
                }
            }

            // Save all of the tenants now we've connected them to the project/environment combinations
            foreach (var tenant in tenants)
            {
                Repository.Tenants.Modify(tenant);
            }
        }

        class SourceToTargetEnvironmentMap
        {
            private readonly SourceAndTargetEnvironment[] sourcesAndTargets;

            class SourceAndTargetEnvironment
            {
                public EnvironmentResource Source { get; }
                public EnvironmentResource Target { get; }

                public SourceAndTargetEnvironment(EnvironmentResource source, EnvironmentResource target)
                {
                    Source = source;
                    Target = target;
                }
            }

            public SourceToTargetEnvironmentMap(EnvironmentResource[] environmentsToMigrate, EnvironmentResource[] existingEnvironments)
            {
                var expectedTargetEnvironmentNames =
                    environmentsToMigrate.Select(Conventions.BuildTargetEnvironmentNameFromSourceEnvironment)
                        .Distinct()
                        .OrderBy(name => name)
                        .ToArray();
                var targetEnvironments = existingEnvironments.Where(
                    e => expectedTargetEnvironmentNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                MissingTargetEnvironmentNames =
                    expectedTargetEnvironmentNames.Except(targetEnvironments.Select(e => e.Name)).ToArray();

                if (!MissingTargetEnvironmentNames.Any())
                {
                    sourcesAndTargets = environmentsToMigrate
                        .Select(source => new SourceAndTargetEnvironment(source, targetEnvironments.FirstOrDefault(target => string.Equals(target.Name, Conventions.BuildTargetEnvironmentNameFromSourceEnvironment(source), StringComparison.OrdinalIgnoreCase))))
                        .ToArray();
                }
            }

            public string[] MissingTargetEnvironmentNames { get; }

            public EnvironmentResource[] GetTargetEnvironmentsForSources(params EnvironmentResource[] sources)
            {
                return GetTargetEnvironmentsForSourceIds(sources.Select(e => e.Id).ToArray());
            }

            public EnvironmentResource[] GetTargetEnvironmentsForSourceIds(params string[] sourceEnvironmentIds)
            {
                AssertNoMissingEnvironments();
                return sourcesAndTargets.Where(x => sourceEnvironmentIds.Contains(x.Source.Id)).Select(x => x.Target).ToArray();
            }

            public EnvironmentResource[] GetTargetEnvironmentsForSourceNames(params string[] sourceEnvironmentNames)
            {
                AssertNoMissingEnvironments();
                return sourcesAndTargets.Where(x => sourceEnvironmentNames.Contains(x.Source.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Target).ToArray();
            }

            public EnvironmentResource[] GetSourceEnvironmentsForTargets(params EnvironmentResource[] targets)
            {
                return GetSourceEnvironmentsForTargetIds(targets.Select(e => e.Id).ToArray());
            }

            public EnvironmentResource[] GetSourceEnvironmentsForTargetIds(params string[] targetEnvironmentIds)
            {
                AssertNoMissingEnvironments();
                return sourcesAndTargets.Where(x => targetEnvironmentIds.Contains(x.Target.Id)).Select(x => x.Source).ToArray();
            }

            public EnvironmentResource[] GetSourceEnvironmentsForTargetNames(params string[] targetEnvironmentNames)
            {
                AssertNoMissingEnvironments();
                return sourcesAndTargets.Where(x => targetEnvironmentNames.Contains(x.Target.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Source).ToArray();
            }

            private void AssertNoMissingEnvironments()
            {
                if (MissingTargetEnvironmentNames.Any())
                    throw new InvalidOperationException(
                        $"The following environments are missing and should be created before expecting this map to be complete: {MissingTargetEnvironmentNames.CommaSeperate()}");
            }
        }

        class SourceEnvironmentToTenantMap
        {
            private readonly SourceEnvironmentAndTenant[] sourcesAndTenants;

            class SourceEnvironmentAndTenant
            {
                public EnvironmentResource SourceEnvironment { get; }
                public TenantResource Tenant { get; }

                public SourceEnvironmentAndTenant(EnvironmentResource sourceEnvironment, TenantResource tenant)
                {
                    SourceEnvironment = sourceEnvironment;
                    Tenant = tenant;
                }
            }

            public SourceEnvironmentToTenantMap(EnvironmentResource[] environmentsToMigrate, TenantResource[] existingTenants)
            {
                var expectedTenantNames = environmentsToMigrate.Select(Conventions.BuildTenantNameFromSourceEnvironment).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var tenants = existingTenants.Where(t => expectedTenantNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
                MissingTenantNames = expectedTenantNames.Except(tenants.Select(t => t.Name), StringComparer.OrdinalIgnoreCase).ToArray();
                if (!MissingTenantNames.Any())
                {
                    sourcesAndTenants = environmentsToMigrate
                        .Select(e => new SourceEnvironmentAndTenant(e, tenants.Single(t => string.Equals(t.Name, Conventions.BuildTenantNameFromSourceEnvironment(e), StringComparison.OrdinalIgnoreCase))))
                        .ToArray();
                }
            }

            public string[] MissingTenantNames { get; }

            private void AssertNoMissingTenants()
            {
                if (MissingTenantNames.Any())
                    throw new InvalidOperationException($"There are tenants that need to be created before this map can be complete: {MissingTenantNames.CommaSeperate()}");
            }

            public TenantResource[] GetAllTenants()
            {
                AssertNoMissingTenants();
                return sourcesAndTenants.Select(x => x.Tenant).ToArray();
            }

            public EnvironmentResource[] GetSourceEnvironmentsForTenants(params TenantResource[] tenants)
            {
                return GetSourceEnvironmentsForTenantIds(tenants.Select(t => t.Id).ToArray());
            }

            public EnvironmentResource[] GetSourceEnvironmentsForTenantIds(params string[] tenantIds)
            {
                AssertNoMissingTenants();
                return sourcesAndTenants.Where(x => tenantIds.Contains(x.Tenant.Id)).Select(x => x.SourceEnvironment).ToArray();
            }

            public EnvironmentResource[] GetSourceEnvironmentsForTenantNames(params string[] tenantNames)
            {
                AssertNoMissingTenants();
                return sourcesAndTenants.Where(x => tenantNames.Contains(x.Tenant.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.SourceEnvironment).ToArray();
            }

            public TenantResource[] GetTenantsForSourceEnvironments(params EnvironmentResource[] sourceEnvironments)
            {
                return GetTenantsForSourceEnvironmentIds(sourceEnvironments.Select(e => e.Id).ToArray());
            }

            public TenantResource[] GetTenantsForSourceEnvironmentIds(params string[] sourceEnvironmentIds)
            {
                AssertNoMissingTenants();
                return sourcesAndTenants.Where(x => sourceEnvironmentIds.Contains(x.SourceEnvironment.Id)).Select(x => x.Tenant).ToArray();
            }

            public TenantResource[] GetTenantsForSourceEnvironmentNames(params string[] sourceEnvironmentNames)
            {
                AssertNoMissingTenants();
                return sourcesAndTenants.Where(x => sourceEnvironmentNames.Contains(x.SourceEnvironment.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Tenant).ToArray();
            }
        }

        class TenantToProjectAndTargetEnvironmentsMap
        {
            class TenantToProjectAndTargetEnvironments
            {
                public TenantToProjectAndTargetEnvironments(TenantResource tenant, ProjectResource project, EnvironmentResource[] environments)
                {
                    Tenant = tenant;
                    Project = project;
                    Environments = environments;
                }

                public TenantResource Tenant { get; }
                public ProjectResource Project { get; }
                public EnvironmentResource[] Environments { get; }
            }

            private readonly TenantToProjectAndTargetEnvironments[] map;

            public TenantToProjectAndTargetEnvironmentsMap(IOctopusRepository repository, SourceEnvironmentToTenantMap sourceEnvironmentToTenantEnvironmentMap, SourceToTargetEnvironmentMap sourceToTargetEnvironmentMap)
            {
                var allProjects = repository.Projects.GetAll();
                var allChannels = repository.Channels.FindAll();
                var allLifecycles = repository.Lifecycles.FindAll();
                var tenants = sourceEnvironmentToTenantEnvironmentMap.GetAllTenants();

                map = allProjects.Select(project =>
                {
                    var projectChannels = allChannels.Where(c => c.ProjectId == project.Id);
                    var projectChannelLifecycleIds = projectChannels.Select(c => c.LifecycleId).Where(id => id != null);
                    var connectedLifecycleIds = new[] { project.LifecycleId }.Concat(projectChannelLifecycleIds).Distinct().ToArray();
                    var connectedLifecycles = allLifecycles.Where(l => connectedLifecycleIds.Contains(l.Id)).ToArray();

                    tenants.Select(tenant =>
                    {
                        // Figure out if any "environments pretending to be this tenant" were connected to this project
                        var connectedSourceEnvironmentsForTenant = sourceEnvironmentToTenantEnvironmentMap.GetSourceEnvironmentsForTenants(tenant)
                            .Where(source => connectedLifecycles.Any(l => LifecycleContainsAnyOfTheseEnvironments(l, source)))
                            .ToArray();

                        if (connectedSourceEnvironmentsForTenant.Any())
                        {
                            var targetEnvironmentsForTenant = sourceToTargetEnvironmentMap.GetTargetEnvironmentsForSources(connectedSourceEnvironmentsForTenant);

                            Log.Information("Connecting {Tenant} to {Project} deploying to {Environments}",
                                tenant.Name, project.Name, targetEnvironmentsForTenant.Select(e => e.Name));
                            tenant.ConnectToProjectAndEnvironments(project, targetEnvironmentsForTenant);
                        }
                    }
                }
            }

            static bool LifecycleContainsAnyOfTheseEnvironments(LifecycleResource lifecycle, params EnvironmentResource[] environments)
            {
                return lifecycle.Phases.Any(p =>
                    p.AutomaticDeploymentTargets.Intersect(environments.Select(e => e.Id)).Any() ||
                    p.OptionalDeploymentTargets.Intersect(environments.Select(e => e.Id)).Any());
            }
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

            public static MigrateCommand.Region[] All =
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

        static class Conventions
        {
            public static string BuildTenantNameFromSourceEnvironment(EnvironmentResource source)
            {
                return source.Name.Split(new[] { '-' }, 2)[0].Trim();
            }

            public static string BuildTargetEnvironmentNameFromSourceEnvironment(EnvironmentResource source)
            {
                var split = source.Name.Split(new[] { '-' }, 2);
                return split.Length == 2 ? split[1].Trim() : "Production";
            }
        }
    }
}
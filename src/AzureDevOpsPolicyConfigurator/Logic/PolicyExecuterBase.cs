﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AzureDevOpsPolicyConfigurator.Data;
using log4net;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace AzureDevOpsPolicyConfigurator.Logic
{
    /// <summary>
    /// ApplyPolicyset class.
    /// </summary>
    internal abstract class PolicyExecuterBase : ILogicExecuter<ExecuterSettings>
    {
        /// <summary>
        /// Logger
        /// </summary>
        protected static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IFileReader reader;
        private readonly IConnectionProvider connectionProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="PolicyExecuterBase"/> class.
        /// </summary>
        /// <param name="serializer">JsonSerialzer</param>
        /// <param name="reader">FileReader</param>
        /// <param name="connectionProvider">Connection provider</param>
        public PolicyExecuterBase(IJsonSerializer serializer, IFileReader reader, IConnectionProvider connectionProvider)
        {
            this.Serializer = serializer;
            this.reader = reader;
            this.connectionProvider = connectionProvider;
        }

        /// <summary>
        /// Gets serializer
        /// </summary>
        protected IJsonSerializer Serializer { get; }

        /// <summary>
        /// Executer
        /// </summary>
        /// <param name="arguments">Arguments</param>
        public void Execute(ExecuterSettings arguments)
        {
            var content = this.reader.GetFileContent(arguments.Input);
            var policyDefinition = this.Serializer.Deserialize<PolicyDefinition>(content);

            using (var connection = this.connectionProvider.GetConnection(arguments))
            using (var projectClient = connection.GetClient<ProjectHttpClient>())
            using (var policyClient = connection.GetClient<PolicyHttpClient>())
            using (var gitRepositoryClient = connection.GetClient<GitHttpClient>())
            {
                var projects = projectClient.GetProjects();

                foreach (var project in projects.Result)
                {
                    if (!policyDefinition.IsProjectAllowed(project))
                    {
                        Log.Debug($"Skipping project \"{project.Name}\", because it's not listed in the allowed projects list");
                        continue;
                    }

                    Log.Info($"Starting project: {project.Name}");

                    var types = policyClient.GetPolicyTypesAsync(project.Id).Result;

                    var policyConfigurations = policyClient.GetPolicyConfigurationsAsync(project.Id);
                    var grouppedRepositories =
                        policyConfigurations.Result
                            .Where(x => !policyDefinition.IgnoreType(x.Type.Id, types))
                            .GroupBy(x => x.GetRepositoryId());

                    var repositories = gitRepositoryClient.GetRepositoriesAsync(project.Id);

                    foreach (var repository in repositories.Result)
                    {
                        if (!policyDefinition.IsRepositoryAllowed(repository))
                        {
                            Log.Debug($"Skipping repository \"{repository.Name}\", because it's not listed in the allowed repositories list");
                            continue;
                        }

                        var relevantPolicies = this.GetPoliciesForRepository(policyDefinition.Policies, project, repository);
                        var serverPolicy = grouppedRepositories.FirstOrDefault(x => x.Key == repository.Id.ToString());

                        this.HandleChanges(policyDefinition.AllowDeletion, policyClient, project.Id, repository, relevantPolicies, serverPolicy, types);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a policy in Azure DevOps.
        /// </summary>
        /// <param name="policyClient">Policy client</param>
        /// <param name="types">Types</param>
        /// <param name="projectId">Team project id</param>
        /// <param name="repository">Git repository</param>
        /// <param name="currentPolicy">Branch policy</param>
        /// <param name="policy">Policy</param>
        protected abstract void CreatePolicy(
            PolicyHttpClient policyClient,
            IEnumerable<PolicyType> types,
            Guid projectId,
            GitRepository repository,
            BranchPolicies currentPolicy,
            Policy policy);

        /// <summary>
        /// Updates a policy in Azure DevOps.
        /// </summary>
        /// <param name="policyClient">Policy client</param>
        /// <param name="types">Types</param>
        /// <param name="projectId">Team project id</param>
        /// <param name="repository">Git repository</param>
        /// <param name="currentPolicy">Branch policy</param>
        /// <param name="policy">Policy</param>
        /// <param name="serverPolicy">Azure DevOps policy</param>
        protected abstract void UpdatePolicy(
            PolicyHttpClient policyClient,
            IEnumerable<PolicyType> types,
            Guid projectId,
            GitRepository repository,
            BranchPolicies currentPolicy,
            Policy policy,
            PolicyConfiguration serverPolicy);

        /// <summary>
        /// Deletes a policy in Azure DevOps.
        /// </summary>
        /// <param name="policyClient">Policy client</param>
        /// <param name="projectId">Team project id</param>
        /// <param name="policy">Policy</param>
        protected abstract void DeletePolicy(PolicyHttpClient policyClient, Guid projectId, PolicyConfiguration policy);

        private void HandleChanges(
            bool allowDeletion,
            PolicyHttpClient policyClient,
            Guid projectId,
            GitRepository repository,
            IEnumerable<BranchPolicies> relevantPolicies,
            IEnumerable<PolicyConfiguration> serverPolicy,
            IEnumerable<PolicyType> types)
        {
            List<int> handledServerPolicies = new List<int>();

            foreach (var currentPolicy in relevantPolicies)
            {
                this.HandleBranchChanges(policyClient, projectId, repository, serverPolicy, types, handledServerPolicies, currentPolicy);
            }

            this.RemoveNoMatchServerPolicies(allowDeletion, policyClient, projectId, serverPolicy, handledServerPolicies);
        }

        private void HandleBranchChanges(
            PolicyHttpClient policyClient,
            Guid projectId,
            GitRepository repository,
            IEnumerable<PolicyConfiguration> serverPolicies,
            IEnumerable<PolicyType> types,
            List<int> handledServerPolicies,
            BranchPolicies currentPolicy)
        {
            foreach (var policy in currentPolicy.Policies)
            {
                bool hasMatch = false;

                var typeId = policy.Type.GetPolicyTypeId(types);

                if (typeId == null)
                {
                    Log.Warn($"Type not found. (Branch: {currentPolicy.Branch}, Type: {policy.Type})");
                }

                if (serverPolicies != null)
                {
                    foreach (var serverPolicy in serverPolicies)
                    {
                        if (typeId == serverPolicy.Type.Id &&
                                ((string.IsNullOrEmpty(serverPolicy.GetBranch()) && string.IsNullOrEmpty(policy.Branch)) ||
                                (!string.IsNullOrEmpty(serverPolicy.GetBranch()) && !string.IsNullOrEmpty(policy.Branch) &&
                                    serverPolicy.GetBranch() == policy.Branch && serverPolicy.GetMatchKind() == policy.MatchKind)))
                        {
                            hasMatch = true;
                            handledServerPolicies.Add(serverPolicy.Id);

                            if (policy.PolicyEquals(serverPolicy))
                            {
                                Log.Info($"Policy is uptodate. (Branch: {currentPolicy.Branch}, Type: {policy.Type})");
                            }
                            else
                            {
                                this.UpdatePolicy(policyClient, types, projectId, repository, currentPolicy, policy, serverPolicy);
                            }

                            break;
                        }
                    }
                }

                if (!hasMatch)
                {
                    this.CreatePolicy(policyClient, types, projectId, repository, currentPolicy, policy);
                }
            }
        }

        private void RemoveNoMatchServerPolicies(bool allowDeletion, PolicyHttpClient policyClient, Guid projectId, IEnumerable<PolicyConfiguration> serverPolicy, List<int> handledServerPolicies)
        {
            if (serverPolicy != null)
            {
                var removeables = serverPolicy.Where(x => handledServerPolicies.All(htp => x.Id != htp));

                foreach (var removeable in removeables)
                {
                    if (!allowDeletion)
                    {
                        Log.Info($"Policy would be removed (Repository: {removeable.GetRepositoryId()}, Type: {removeable.Type.DisplayName}, Branch: {removeable.GetBranch()})");
                        continue;
                    }

                    this.DeletePolicy(policyClient, projectId, removeable);
                }
            }
        }

        private IEnumerable<BranchPolicies> GetPoliciesForRepository(
            IEnumerable<Policy> policies,
            TeamProjectReference project,
            GitRepository repository)
        {
            return policies
                .Where(x =>
                        string.IsNullOrEmpty(x.Project) || // global or branch specific
                        (x.Project.ToLower() == project.Name.ToLower() || x.Project.ToLower() == project.Id.ToString().ToLower()) || // project or project branch specific
                        (x.Repository.ToLower() == repository.Name.ToLower() || x.Repository.ToLower() == repository.Id.ToString().ToLower())) // repository
                .GroupBy(
                    x => x.Branch,
                    (e, ee) => new BranchPolicies(e, ee.GroupBy(x => x.Type, (te, tee) => tee.OrderBy(x => x, new PolicyPriorityComparer()).First())));
        }

        /// <summary>
        /// Policy priority comparer.
        /// </summary>
        private class PolicyPriorityComparer : IComparer<Policy>
        {
            public int Compare(Policy x, Policy y)
            {
                // Repository
                if (x.Repository != null)
                {
                    return -1;
                }

                // Repository
                if (y.Repository != null)
                {
                    return 1;
                }

                // Global project
                if (x.Project != null)
                {
                    return -1;
                }

                // Global project
                if (y.Project != null)
                {
                    return 1;
                }

                return -1;
            }
        }
    }
}
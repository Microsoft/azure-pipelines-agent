// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Agent.Plugins;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace Agent.Plugins.PipelineArtifact
{
    public abstract class PipelineArtifactTaskPluginBaseV2 : IAgentTaskPlugin
    {
        public abstract Guid Id { get; }
        protected virtual string DownloadPath => "path";
        protected virtual string RunId => "runId";
        protected IAppTraceSource tracer;

        public string Stage => "main";

        public Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            this.tracer = context.CreateArtifactsTracer();

            return this.ProcessCommandInternalAsync(context, token);
        }

        protected abstract Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token);

        // Properties set by tasks
        protected static class ArtifactEventProperties
        {
            public static readonly string SourceRun = "source";
            public static readonly string Project = "project";
            public static readonly string PipelineDefinition = "pipeline";
            public static readonly string PipelineTriggering = "preferTriggeringPipeline";
            public static readonly string PipelineVersionToDownload = "runVersion";
            public static readonly string BranchName = "runBranch";
            public static readonly string Tags = "tags";
            public static readonly string AllowPartiallySucceededBuilds = "allowPartiallySucceededBuilds";
            public static readonly string AllowFailedBuilds = "allowFailedBuilds";
            public static readonly string AllowCanceledBuilds = "allowCanceledBuilds";
            public static readonly string ArtifactName = "artifact";
            public static readonly string ItemPattern = "patterns";
        }
    }

    // Can be invoked from a build run or a release run should a build be set as the artifact. 
    public class DownloadPipelineArtifactTaskV2_0_0 : PipelineArtifactTaskPluginBaseV2
    {
        // Same as https://github.com/Microsoft/vsts-tasks/blob/master/Tasks/DownloadPipelineArtifactV1/task.json
        public override Guid Id => PipelineArtifactPluginConstants.DownloadPipelineArtifactTaskId;
        static readonly string sourceRunCurrent = "current";
        static readonly string sourceRunSpecific = "specific";
        static readonly string pipelineVersionToDownloadLatest = "latest";
        static readonly string pipelineVersionToDownloadSpecific = "specific";
        static readonly string pipelineVersionToDownloadLatestFromBranch = "latestFromBranch";
        static readonly string pipelineVersionToDownloadPullRequestMergeTarget = "pullRequestMergeTarget";

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            string artifactName = context.GetInput(ArtifactEventProperties.ArtifactName, required: false);
            string branchName = context.GetInput(ArtifactEventProperties.BranchName, required: false);
            string pipelineDefinition = context.GetInput(ArtifactEventProperties.PipelineDefinition, required: false);
            string sourceRun = context.GetInput(ArtifactEventProperties.SourceRun, required: true);
            string pipelineTriggering = context.GetInput(ArtifactEventProperties.PipelineTriggering, required: false);
            string pipelineVersionToDownload = context.GetInput(ArtifactEventProperties.PipelineVersionToDownload, required: false);
            string targetPath = context.GetInput(DownloadPath, required: true);
            string environmentBuildId = context.Variables.GetValueOrDefault(BuildVariables.BuildId)?.Value ?? string.Empty; // BuildID provided by environment.
            string itemPattern = context.GetInput(ArtifactEventProperties.ItemPattern, required: false);
            string projectName = context.GetInput(ArtifactEventProperties.Project, required: false);
            string tags = context.GetInput(ArtifactEventProperties.Tags, required: false);
            string allowPartiallySucceededBuilds = context.GetInput(ArtifactEventProperties.AllowPartiallySucceededBuilds, required: false);
            string allowFailedBuilds = context.GetInput(ArtifactEventProperties.AllowFailedBuilds, required: false);
            string allowCanceledBuilds = context.GetInput(ArtifactEventProperties.AllowCanceledBuilds, required: false);
            string userSpecifiedRunId = context.GetInput(RunId, required: false);
            string defaultWorkingDirectory = context.Variables.GetValueOrDefault("system.defaultworkingdirectory").Value;

            targetPath = Path.IsPathFullyQualified(targetPath) ? targetPath : Path.GetFullPath(Path.Combine(defaultWorkingDirectory, targetPath));
            context.Debug($"TargetPath: {targetPath}");

            bool onPrem = !String.Equals(context.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.ServerType)?.Value, "Hosted", StringComparison.OrdinalIgnoreCase);
            
            if (onPrem)
            {
                throw new InvalidOperationException(StringUtil.Loc("OnPremIsNotSupported"));
            }

            if (!PipelineArtifactPathHelper.IsValidArtifactName(artifactName))
            {
                throw new ArgumentException(StringUtil.Loc("ArtifactNameIsNotValid", artifactName));
            }
            context.Debug($"ArtifactName: {artifactName}");

            string[] minimatchPatterns = itemPattern.Split(
                new[] { "\n" },
                StringSplitOptions.RemoveEmptyEntries
            );

            string[] tagsInput = tags.Split(
                new[] { "," },
                StringSplitOptions.None
            );

            if (!bool.TryParse(allowPartiallySucceededBuilds, out var allowPartiallySucceededBuildsBool))
            {
                allowPartiallySucceededBuildsBool = false;
            }
            if (!bool.TryParse(allowFailedBuilds, out var allowFailedBuildsBool))
            {
                allowFailedBuildsBool = false;
            }
            if (!bool.TryParse(allowCanceledBuilds, out var allowCanceledBuildsBool))
            {
                allowCanceledBuildsBool = false;
            }
            var resultFilter = GetResultFilter(allowPartiallySucceededBuildsBool, allowFailedBuildsBool, allowCanceledBuildsBool);
            context.Debug($"BuildResult: {resultFilter.ToString()}");

            PipelineArtifactServer server = new PipelineArtifactServer(tracer);
            ArtifactDownloadParameters downloadParameters;

            if (sourceRun == sourceRunCurrent)
            {
                context.Debug("Run: CurrentRun");
                // TODO: use a constant for project id, which is currently defined in Microsoft.VisualStudio.Services.Agent.Constants.Variables.System.TeamProjectId (Ting)
                string projectIdStr = context.Variables.GetValueOrDefault("system.teamProjectId")?.Value;
                if (String.IsNullOrEmpty(projectIdStr))
                {
                    throw new ArgumentNullException(StringUtil.Loc("CannotBeNullOrEmpty"), "Project ID");
                }
                
                Guid projectId = Guid.Parse(projectIdStr);
                ArgUtil.NotEmpty(projectId, nameof(projectId));
                context.Debug($"ProjectId: {projectId.ToString()}");

                int pipelineId = 0;
                if (int.TryParse(environmentBuildId, out pipelineId) && pipelineId != 0)
                {
                    OutputBuildInfo(context, pipelineId);
                }
                else
                {
                    string hostType = context.Variables.GetValueOrDefault("system.hosttype")?.Value;
                    if (string.Equals(hostType, "Release", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(hostType, "DeploymentGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(StringUtil.Loc("BuildIdIsNotAvailable", hostType ?? string.Empty, hostType ?? string.Empty));
                    }
                    else if (!string.Equals(hostType, "Build", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(StringUtil.Loc("CannotDownloadFromCurrentEnvironment", hostType ?? string.Empty));
                    }
                    else
                    {
                        // This should not happen since the build id comes from build environment. But a user may override that so we must be careful.
                        throw new ArgumentException(StringUtil.Loc("BuildIdIsNotValid", environmentBuildId));
                    }
                }

                downloadParameters = new ArtifactDownloadParameters
                {
                    ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectId,
                    ProjectId = projectId,
                    PipelineId = pipelineId,
                    ArtifactName = artifactName,
                    TargetDirectory = targetPath,
                    MinimatchFilters = minimatchPatterns,
                    MinimatchFilterWithArtifactName = true
                };
            }
            else if (sourceRun == sourceRunSpecific)
            {
                context.Debug("Run: Specific");
                if (String.IsNullOrEmpty(projectName))
                {
                    throw new ArgumentNullException(StringUtil.Loc("CannotBeNullOrEmpty"), "Project Name");
                }
                Guid projectId; 
                bool isProjGuid = Guid.TryParse(projectName, out projectId);
                if (!isProjGuid) 
                {
                    projectId = await GetProjectIdAsync(context, projectName);
                }
                context.Debug($"ProjectId: {projectId.ToString()}");
                // Set the default pipelineId to 0, which is an invalid build id and it has to be reassigned to a valid build id.
                int pipelineId = 0;

                bool pipelineTriggeringBool;
                if (bool.TryParse(pipelineTriggering, out pipelineTriggeringBool) && pipelineTriggeringBool)
                {
                    context.Debug("TrigerringPipeline: true");
                    string hostType = context.Variables.GetValueOrDefault("system.hostType").Value;
                    string triggeringPipeline = null;
                    if (!string.IsNullOrWhiteSpace(hostType) && !hostType.Equals("build", StringComparison.OrdinalIgnoreCase)) // RM env.
                    {
                        context.Debug("Environment: Release");
                        var releaseAlias = context.Variables.GetValueOrDefault("release.triggeringartifact.alias")?.Value;
                        var definitionIdTriggered = context.Variables.GetValueOrDefault("release.artifacts." + releaseAlias ?? string.Empty + ".definitionId")?.Value;
                        if (!string.IsNullOrWhiteSpace(definitionIdTriggered) && definitionIdTriggered.Equals(pipelineDefinition, StringComparison.OrdinalIgnoreCase))
                        {
                            triggeringPipeline = context.Variables.GetValueOrDefault("release.artifacts." + releaseAlias ?? string.Empty + ".buildId")?.Value;
                            context.Debug($"TrigerringPipeline: {triggeringPipeline}");
                        }
                    }
                    else
                    {
                        context.Debug("Environment: Build");
                        var definitionIdTriggered = context.Variables.GetValueOrDefault("build.triggeredBy.definitionId")?.Value;
                        if (!string.IsNullOrWhiteSpace(definitionIdTriggered) && definitionIdTriggered.Equals(pipelineDefinition, StringComparison.OrdinalIgnoreCase))
                        {
                            triggeringPipeline = context.Variables.GetValueOrDefault("build.triggeredBy.buildId")?.Value;
                            context.Debug($"TrigerringPipeline: {triggeringPipeline}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(triggeringPipeline))
                    {
                        pipelineId = int.Parse(triggeringPipeline);
                    }
                    context.Debug($"PipelineId from trigerringBuild: {pipelineId}");
                }

                if (pipelineId == 0)
                {
                    context.Debug($"PipelineVersionToDownload: {pipelineVersionToDownload}");
                    if (pipelineVersionToDownload == pipelineVersionToDownloadLatest)
                    {
                        pipelineId = await this.GetPipelineIdAsync(context, pipelineDefinition, pipelineVersionToDownload, projectId.ToString(), tagsInput, resultFilter, null, cancellationToken: token);
                    }
                    else if (pipelineVersionToDownload == pipelineVersionToDownloadSpecific)
                    {
                        bool isPipelineIdNum = Int32.TryParse(userSpecifiedRunId, out pipelineId);
                        if(!isPipelineIdNum)
                        {
                            throw new ArgumentException(StringUtil.Loc("RunIDNotValid", userSpecifiedRunId));
                        }
                    }
                    else if (pipelineVersionToDownload == pipelineVersionToDownloadLatestFromBranch)
                    {
                        pipelineId = await this.GetPipelineIdAsync(context, pipelineDefinition, pipelineVersionToDownload, projectId.ToString(), tagsInput, resultFilter, branchName, cancellationToken: token);
                    }
                    else if (pipelineVersionToDownload == pipelineVersionToDownloadPullRequestMergeTarget)
                    {
                        branchName = context.Variables.GetValueOrDefault("system.pullrequest.targetbranch")?.Value;
                        if (branchName == null)
                        {
                            context.Warning($"Build version to download '{pipelineVersionToDownloadPullRequestMergeTarget}' is only supported in builds triggered by pull requests. Falling back to '{pipelineVersionToDownloadLatest}'.");
                            pipelineVersionToDownload = pipelineVersionToDownloadLatest;
                        }
                        pipelineId = await this.GetPipelineIdAsync(context, pipelineDefinition, pipelineVersionToDownload, projectId.ToString(), tagsInput, resultFilter, branchName: branchName, cancellationToken: token);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unreachable code!");
                    }
                    context.Debug($"PipelineId from non-trigerringBuild: {pipelineId}");
                }

                OutputBuildInfo(context, pipelineId);

                downloadParameters = new ArtifactDownloadParameters
                {
                    ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectName,
                    ProjectName = projectName,
                    ProjectId = projectId,
                    PipelineId = pipelineId,
                    ArtifactName = artifactName,
                    TargetDirectory = targetPath,
                    MinimatchFilters = minimatchPatterns,
                    MinimatchFilterWithArtifactName = true
                };
            }
            else
            {
                throw new InvalidOperationException($"Build type '{sourceRun}' is not recognized.");
            }

            string fullPath = this.CreateDirectoryIfDoesntExist(targetPath);

            DownloadOptions downloadOptions;
            if (string.IsNullOrEmpty(downloadParameters.ArtifactName))
            {
                downloadOptions = DownloadOptions.MultiDownload;
            }
            else
            {
                downloadOptions = DownloadOptions.SingleDownload;
            }

            context.Output(StringUtil.Loc("DownloadArtifactTo", targetPath));
            await server.DownloadAsyncV2(context, downloadParameters, downloadOptions, token);
            context.Output(StringUtil.Loc("DownloadArtifactFinished"));
        }

        protected virtual string GetArtifactName(AgentTaskPluginExecutionContext context)
        {
            return context.GetInput(ArtifactEventProperties.ArtifactName, required: true);
        }

        private string CreateDirectoryIfDoesntExist(string targetPath)
        {
            string fullPath = Path.GetFullPath(targetPath);
            bool dirExists = Directory.Exists(fullPath);
            if (!dirExists)
            {
                Directory.CreateDirectory(fullPath);
            }
            return fullPath;
        }

        private async Task<int> GetPipelineIdAsync(AgentTaskPluginExecutionContext context, string pipelineDefinition, string pipelineVersionToDownload, string project, string[] tagFilters, BuildResult resultFilter = BuildResult.Succeeded, string branchName = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if(String.IsNullOrWhiteSpace(pipelineDefinition)) 
            {
                throw new InvalidOperationException(StringUtil.Loc("CannotBeNullOrEmpty", "Pipeline Definition"));
            }

            VssConnection connection = context.VssConnection;
            BuildHttpClient buildHttpClient = connection.GetClient<BuildHttpClient>();

            var isDefinitionNum = Int32.TryParse(pipelineDefinition, out int definition);
            if (!isDefinitionNum)
            {
                var definitionRef = (await buildHttpClient.GetDefinitionsAsync(new System.Guid(project), pipelineDefinition, cancellationToken: cancellationToken)).FirstOrDefault();
                if (definitionRef == null)
                {
                    throw new ArgumentException(StringUtil.Loc("PipelineDoesNotExist", pipelineDefinition));
                }
                else
                {
                    definition = definitionRef.Id;
                }
            }
            var definitions = new List<int>() { definition };

            List<Build> list = await buildHttpClient.GetBuildsAsync(project, definitions, branchName: branchName, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending, resultFilter: resultFilter);

            if (pipelineVersionToDownload == pipelineVersionToDownloadPullRequestMergeTarget)
            {
                if (!Int32.TryParse(context.Variables.GetValueOrDefault("system.pullrequest.pullrequestid").Value, out int pullreuestId))
                {
                    throw new InvalidOperationException($"Expected system.pullrequest.pullrequestid to be set.");
                }
                GitHttpClient gitHttpClient = connection.GetClient<GitHttpClient>();
                var pullRequest = await gitHttpClient.GetPullRequestByIdAsync(project, pullreuestId);
                var targetCommitId = pullRequest.LastMergeTargetCommit.CommitId;

                var build = list.FirstOrDefault(build => build.SourceVersion == targetCommitId);
                if (build == null)
                {
                    // No completed builds match. Try searching in-progress builds in case the artifact was published but the build hasn't finished
                    build = (await buildHttpClient.GetBuildsAsync(project, definitions, branchName: branchName, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending, statusFilter: BuildStatus.InProgress))
                            .FirstOrDefault(build => build.SourceVersion == targetCommitId);
                }
                list.Clear();
                if (build != null)
                {
                    list.Add(build);
                }
            }
 
            if (list.Count > 0)
            {
                return list.First().Id;
            }
            else
            {
                throw new ArgumentException(StringUtil.Loc("BuildsDoesNotExist"));
            }
        }

        private BuildResult GetResultFilter(bool allowPartiallySucceededBuilds, bool allowFailedBuilds, bool allowCanceledBuilds)
        {
            var result = BuildResult.Succeeded;

            if (allowPartiallySucceededBuilds)
            {
                result |= BuildResult.PartiallySucceeded;
            }

            if (allowFailedBuilds)
            {
                result |= BuildResult.Failed;
            }

            if (allowCanceledBuilds)
            {
                result |= BuildResult.Canceled;
            }

            return result;
        }
      
        private async Task<Guid> GetProjectIdAsync(AgentTaskPluginExecutionContext context, string projectName)
        {
            VssConnection connection = context.VssConnection;
            var projectClient = connection.GetClient<ProjectHttpClient>();

            TeamProject proj = null;

            try
            {
                proj = await projectClient.GetProject(projectName);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Get project failed " + projectName + " , exception: " + ex);
            }

            return proj.Id;
        }

        private void OutputBuildInfo(AgentTaskPluginExecutionContext context, int? pipelineId){
            context.Output(StringUtil.Loc("DownloadingFromBuild", pipelineId));
            // populate output variable 'BuildNumber' with buildId
            context.SetVariable("BuildNumber", pipelineId.ToString());
        }
    }
}
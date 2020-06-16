// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Agent.Sdk;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;

namespace Agent.Plugins.PipelineArtifact
{
    public abstract class PipelineArtifactTaskPluginBaseV1 : IAgentTaskPlugin
    {
        public abstract Guid Id { get; }
        protected virtual string TargetPath => "targetPath";
        protected virtual string PipelineId => "pipelineId";
        protected CallbackAppTraceSource tracer;
        public string Stage => "main";

        public Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            this.tracer = new CallbackAppTraceSource(str => context.Output(str), System.Diagnostics.SourceLevels.Information);
            return this.ProcessCommandInternalAsync(context, token);
        }

        protected abstract Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token);

        // Properties set by tasks
        protected static class ArtifactEventProperties
        {
            public static readonly string BuildType = "buildType";
            public static readonly string Project = "project";
            public static readonly string BuildPipelineDefinition = "definition";
            public static readonly string BuildTriggering = "specificBuildWithTriggering";
            public static readonly string BuildVersionToDownload = "buildVersionToDownload";
            public static readonly string BranchName = "branchName";
            public static readonly string Tags = "tags";
            public static readonly string ArtifactName = "artifactName";
            public static readonly string ItemPattern = "itemPattern";
            public static readonly string ArtifactType = "artifactType";
            public static readonly string FileSharePath = "fileSharePath";
        }
    }

    // Caller: PublishPipelineArtifact task
    // Can be invoked from a build run or a release run should a build be set as the artifact.
    public class PublishPipelineArtifactTaskV1 : PipelineArtifactTaskPluginBaseV1
    {
        public override Guid Id => PipelineArtifactPluginConstants.PublishPipelineArtifactTaskId;
        protected override string TargetPath => "path";
        private static readonly Regex jobIdentifierRgx = new Regex("[^a-zA-Z0-9 - .]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private const string pipelineType = "pipeline";
        private const string fileShareType = "filepath";

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            string artifactName = context.GetInput(ArtifactEventProperties.ArtifactName, required: false);
            if (string.IsNullOrEmpty(artifactName))
            {
                context.Output($"Artifact name was not inserted for publishing.");
            }
            else
            {
                context.Output($"Artifact name input: {artifactName}");
            }
            string targetPath = context.GetInput(TargetPath, required: true);
            string artifactType = context.GetInput(ArtifactEventProperties.ArtifactType, required: false);
            artifactType = string.IsNullOrEmpty(artifactType) ? pipelineType : artifactType.ToLower();

            string defaultWorkingDirectory = context.Variables.GetValueOrDefault("system.defaultworkingdirectory").Value;

            bool onPrem = !String.Equals(context.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.ServerType)?.Value, "Hosted", StringComparison.OrdinalIgnoreCase);
            if (onPrem)
            {
                throw new InvalidOperationException(StringUtil.Loc("OnPremIsNotSupported"));
            }

            targetPath = Path.IsPathFullyQualified(targetPath) ? targetPath : Path.GetFullPath(Path.Combine(defaultWorkingDirectory, targetPath));

            // Project ID
            var teamProjectId = context.Variables.GetValueOrDefault(BuildVariables.TeamProjectId)?.Value;
            Guid projectId = teamProjectId != null ? new Guid(teamProjectId) : Guid.Empty;

            ArgUtil.NotEmpty(projectId, nameof(projectId));

            // Build ID
            string buildIdStr = context.Variables.GetValueOrDefault(BuildVariables.BuildId)?.Value ?? string.Empty;
            if (!int.TryParse(buildIdStr, out int buildId))
            {
                // This should not happen since the build id comes from build environment. But a user may override that so we must be careful.
                throw new ArgumentException(StringUtil.Loc("BuildIdIsNotValid", buildIdStr));
            }

            if (artifactType == pipelineType)
            {
                string hostType = context.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.HostType)?.Value;
                if (!string.Equals(hostType, "Build", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        StringUtil.Loc("CannotUploadFromCurrentEnvironment", hostType ?? string.Empty));
                }

                if (String.IsNullOrWhiteSpace(artifactName))
                {
                    string jobIdentifier = context.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.JobIdentifier).Value;
                    var normalizedJobIdentifier = NormalizeJobIdentifier(jobIdentifier);
                    artifactName = normalizedJobIdentifier;
                }

                if (!PipelineArtifactPathHelper.IsValidArtifactName(artifactName))
                {
                    throw new ArgumentException(StringUtil.Loc("ArtifactNameIsNotValid", artifactName));
                }

                string fullPath = Path.GetFullPath(targetPath);
                bool isFile = File.Exists(fullPath);
                bool isDir = Directory.Exists(fullPath);
                if (!isFile && !isDir)
                {
                    // if local path is neither file nor folder
                    throw new FileNotFoundException(StringUtil.Loc("PathDoesNotExist", targetPath));
                }

                // Upload to VSTS BlobStore, and associate the artifact with the build.
                context.Output(StringUtil.Loc("UploadingPipelineArtifact", fullPath, buildId));
                PipelineArtifactServer server = new PipelineArtifactServer(tracer);
                await server.UploadAsync(context, projectId, buildId, artifactName, fullPath, token);
                context.Output(StringUtil.Loc("UploadArtifactFinished"));

            }
            else if (artifactType == fileShareType)
            {
                string fileSharePath = context.GetInput(ArtifactEventProperties.FileSharePath, required: true);

                fileSharePath = Path.IsPathFullyQualified(fileSharePath) ? fileSharePath : Path.GetFullPath(Path.Combine(defaultWorkingDirectory, fileSharePath));

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    FilePathServer server = new FilePathServer();
                    await server.UploadAsync(context, projectId, buildId, artifactName, targetPath, fileSharePath, token);
                }
                else
                {
                    // file share artifacts are not currently supported on OSX/Linux.
                    throw new InvalidOperationException(StringUtil.Loc("FileShareOperatingSystemNotSupported"));
                }
            }
        }

        private string NormalizeJobIdentifier(string jobIdentifier)
        {
            jobIdentifier = jobIdentifierRgx.Replace(jobIdentifier, string.Empty).Replace(".default", string.Empty);
            return jobIdentifier;
        }
    }

    // Can be invoked from a build run or a release run should a build be set as the artifact.
    public class DownloadPipelineArtifactTaskV1 : PipelineArtifactTaskPluginBaseV1
    {
        // Same as https://github.com/Microsoft/vsts-tasks/blob/master/Tasks/DownloadPipelineArtifactV1/task.json
        public override Guid Id => PipelineArtifactPluginConstants.DownloadPipelineArtifactTaskId;
        static readonly string buildTypeCurrent = "current";
        static readonly string buildTypeSpecific = "specific";
        static readonly string buildVersionToDownloadLatest = "latest";
        static readonly string buildVersionToDownloadSpecific = "specific";
        static readonly string buildVersionToDownloadLatestFromBranch = "latestFromBranch";

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            string artifactName = this.GetArtifactName(context);
            string branchName = context.GetInput(ArtifactEventProperties.BranchName, required: false);
            string buildPipelineDefinition = context.GetInput(ArtifactEventProperties.BuildPipelineDefinition, required: false);
            string buildType = context.GetInput(ArtifactEventProperties.BuildType, required: true);
            string buildTriggering = context.GetInput(ArtifactEventProperties.BuildTriggering, required: false);
            string buildVersionToDownload = context.GetInput(ArtifactEventProperties.BuildVersionToDownload, required: false);
            string targetPath = context.GetInput(TargetPath, required: true);
            string environmentBuildId = context.Variables.GetValueOrDefault(BuildVariables.BuildId)?.Value ?? string.Empty; // BuildID provided by environment.
            string itemPattern = context.GetInput(ArtifactEventProperties.ItemPattern, required: false);
            string projectName = context.GetInput(ArtifactEventProperties.Project, required: false);
            string tags = context.GetInput(ArtifactEventProperties.Tags, required: false);
            string userSpecifiedpipelineId = context.GetInput(PipelineId, required: false);

            string[] minimatchPatterns = itemPattern.Split(
                new[] { "\n" },
                StringSplitOptions.RemoveEmptyEntries
            );

            string[] tagsInput = tags.Split(
                new[] { "," },
                StringSplitOptions.None
            );

            PipelineArtifactServer server = new PipelineArtifactServer(tracer);
            PipelineArtifactDownloadParameters downloadParameters;

            if (buildType == buildTypeCurrent)
            {
                // TODO: use a constant for project id, which is currently defined in Microsoft.VisualStudio.Services.Agent.Constants.Variables.System.TeamProjectId (Ting)
                string projectIdStr = context.Variables.GetValueOrDefault("system.teamProjectId")?.Value;
                if (String.IsNullOrEmpty(projectIdStr))
                {
                    throw new ArgumentNullException("Project ID cannot be null.");
                }
                Guid projectId = Guid.Parse(projectIdStr);
                ArgUtil.NotEmpty(projectId, nameof(projectId));

                int pipelineId = 0;
                if (int.TryParse(environmentBuildId, out pipelineId) && pipelineId != 0)
                {
                    context.Output(StringUtil.Loc("DownloadingFromBuild", pipelineId));
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
                downloadParameters = new PipelineArtifactDownloadParameters
                {
                    ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectId,
                    ProjectId = projectId,
                    PipelineId = pipelineId,
                    ArtifactName = artifactName,
                    TargetDirectory = targetPath,
                    MinimatchFilters = minimatchPatterns,
                    MinimatchFilterWithArtifactName = false
                };
            }
            else if (buildType == buildTypeSpecific)
            {
                int? pipelineId = null;

                bool buildTriggeringBool = false;
                if (bool.TryParse(buildTriggering, out buildTriggeringBool) && buildTriggeringBool)
                {
                    string triggeringPipeline = context.Variables.GetValueOrDefault("build.triggeredBy.buildId")?.Value;

                    if (!string.IsNullOrEmpty(triggeringPipeline))
                    {
                        pipelineId = int.Parse(triggeringPipeline);
                    }
                }

                if (!pipelineId.HasValue)
                {
                    if (buildVersionToDownload == buildVersionToDownloadLatest)
                    {
                        pipelineId = await this.GetpipelineIdAsync(context, buildPipelineDefinition, buildVersionToDownload, projectName, tagsInput);
                    }
                    else if (buildVersionToDownload == buildVersionToDownloadSpecific)
                    {
                        pipelineId = Int32.Parse(userSpecifiedpipelineId);
                    }
                    else if (buildVersionToDownload == buildVersionToDownloadLatestFromBranch)
                    {
                        pipelineId = await this.GetpipelineIdAsync(context, buildPipelineDefinition, buildVersionToDownload, projectName, tagsInput, branchName);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unreachable code!");
                    }
                }

                context.Output(StringUtil.Loc("DownloadingFromBuild", pipelineId));

                downloadParameters = new PipelineArtifactDownloadParameters
                {
                    ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectName,
                    ProjectName = projectName,
                    PipelineId = pipelineId.Value,
                    ArtifactName = artifactName,
                    TargetDirectory = targetPath,
                    MinimatchFilters = minimatchPatterns,
                    MinimatchFilterWithArtifactName = false
                };
            }
            else
            {
                throw new InvalidOperationException($"Build type '{buildType}' is not recognized.");
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
            await server.DownloadAsync(context, downloadParameters, downloadOptions, token);
            context.Output(StringUtil.Loc("DownloadArtifactFinished"));
        }

        protected virtual string GetArtifactName(AgentTaskPluginExecutionContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
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

        private async Task<int> GetpipelineIdAsync(AgentTaskPluginExecutionContext context, string buildPipelineDefinition, string buildVersionToDownload, string project, string[] tagFilters, string branchName = null)
        {
            var definitions = new List<int>() { Int32.Parse(buildPipelineDefinition) };
            VssConnection connection = context.VssConnection;
            BuildHttpClient buildHttpClient = connection.GetClient<BuildHttpClient>();
            List<Build> list;
            if (buildVersionToDownload == "latest")
            {
                list = await buildHttpClient.GetBuildsAsync(project, definitions, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending, resultFilter: BuildResult.Succeeded);
            }
            else if (buildVersionToDownload == "latestFromBranch")
            {
                list = await buildHttpClient.GetBuildsAsync(project, definitions, branchName: branchName, tagFilters: tagFilters, queryOrder: BuildQueryOrder.FinishTimeDescending, resultFilter: BuildResult.Succeeded);
            }
            else
            {
                throw new InvalidOperationException("Unreachable code!");
            }

            if (list.Count > 0)
            {
                return list.First().Id;
            }
            else
            {
                throw new ArgumentException("No builds currently exist in the build definition supplied.");
            }
        }
    }

    public class DownloadPipelineArtifactTaskV1_1_0 : DownloadPipelineArtifactTaskV1
    {
        protected override string TargetPath => "downloadPath";
        protected override string PipelineId => "buildId";

        protected override string GetArtifactName(AgentTaskPluginExecutionContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
            return context.GetInput(ArtifactEventProperties.ArtifactName, required: false);
        }
    }

    public class DownloadPipelineArtifactTaskV1_1_1 : DownloadPipelineArtifactTaskV1
    {
        protected override string GetArtifactName(AgentTaskPluginExecutionContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
            return context.GetInput(ArtifactEventProperties.ArtifactName, required: false);
        }
    }

    // 1.1.2 is the same as 1.1.0 because we reverted 1.1.1 change.
    public class DownloadPipelineArtifactTaskV1_1_2 : DownloadPipelineArtifactTaskV1_1_0
    {
    }

    // 1.1.3 is the same as 1.1.0 because we reverted 1.1.1 change and the minimum agent version.
    public class DownloadPipelineArtifactTaskV1_1_3 : DownloadPipelineArtifactTaskV1_1_0
    {
    }
}
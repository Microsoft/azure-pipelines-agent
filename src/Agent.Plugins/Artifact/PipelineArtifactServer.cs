// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.VisualStudio.Services.Agent.Blob;
using Agent.Plugins.PipelineArtifact.Telemetry;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Plugins
{
    // A wrapper of DedupManifestArtifactClient, providing basic functionalities such as uploading and downloading pipeline artifacts.
    public class PipelineArtifactServer
    {
        private readonly IAppTraceSource tracer;

        public PipelineArtifactServer(IAppTraceSource tracer)
        {
            this.tracer = tracer;
        }

        // Upload from target path to Azure DevOps BlobStore service through DedupManifestArtifactClient, then associate it with the build
        internal async Task UploadAsync(
            AgentTaskPluginExecutionContext context,
            Guid projectId,
            int pipelineId,
            string name,
            string source,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;

            var (dedupManifestClient, clientTelemetry) = await DedupManifestArtifactClientFactory.Instance
                .CreateDedupManifestClientAsync(context.IsSystemDebugTrue(), (str) => context.Output(str), connection, cancellationToken);

            using (clientTelemetry)
            {
                //Upload the pipeline artifact.
                PipelineArtifactActionRecord uploadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                    new PipelineArtifactActionRecord(level, uri, type, nameof(UploadAsync), context));

                PublishResult result = await clientTelemetry.MeasureActionAsync(
                    record: uploadRecord,
                    actionAsync: async () => await AsyncHttpRetryHelper.InvokeAsync(
                            async () => 
                            {
                                return await dedupManifestClient.PublishAsync(source, cancellationToken);
                            },
                            maxRetries: 3,
                            tracer: tracer,
                            canRetryDelegate: e => true, // this isn't great, but failing on upload stinks, so just try a couple of times
                            cancellationToken: cancellationToken,
                            continueOnCapturedContext: false)
                    
                );
                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: uploadRecord);

                // 2) associate the pipeline artifact with an build artifact
                BuildServer buildServer = new BuildServer(connection);
                Dictionary<string, string> propertiesDictionary = new Dictionary<string, string>();
                propertiesDictionary.Add(PipelineArtifactConstants.RootId, result.RootId.ValueString);
                propertiesDictionary.Add(PipelineArtifactConstants.ProofNodes, StringUtil.ConvertToJson(result.ProofNodes.ToArray()));
                propertiesDictionary.Add(PipelineArtifactConstants.ArtifactSize, result.ContentSize.ToString());

                BuildArtifact buildArtifact = await AsyncHttpRetryHelper.InvokeAsync(
                    async () => 
                    {
                        return await buildServer.AssociateArtifactAsync(projectId, 
                                                                        pipelineId, 
                                                                        name, 
                                                                        context.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.JobId)?.Value?? string.Empty, 
                                                                        ArtifactResourceTypes.PipelineArtifact, 
                                                                        result.ManifestId.ValueString, 
                                                                        propertiesDictionary, 
                                                                        cancellationToken);

                    },
                    maxRetries: 3,
                    tracer: tracer,
                    canRetryDelegate: e => e is TimeoutException || e.InnerException is TimeoutException,
                    cancellationToken: cancellationToken,
                    continueOnCapturedContext: false);
                
                context.Output(StringUtil.Loc("AssociateArtifactWithBuild", buildArtifact.Id, pipelineId));
            }
        }

        // Download pipeline artifact from Azure DevOps BlobStore service through DedupManifestArtifactClient to a target path
        // Old V0 function
        internal Task DownloadAsync(
            AgentTaskPluginExecutionContext context,
            Guid projectId,
            int pipelineId,
            string artifactName,
            string targetDir,
            CancellationToken cancellationToken)
        {
            var downloadParameters = new ArtifactDownloadParameters
            {
                ProjectRetrievalOptions = BuildArtifactRetrievalOptions.RetrieveByProjectId,
                ProjectId = projectId,
                PipelineId = pipelineId,
                ArtifactName = artifactName,
                TargetDirectory = targetDir
            };

            return this.DownloadAsync(context, downloadParameters, DownloadOptions.SingleDownload, cancellationToken);
        }

        // Download with minimatch patterns, V1.
        internal async Task DownloadAsync(
            AgentTaskPluginExecutionContext context,
            ArtifactDownloadParameters downloadParameters,
            DownloadOptions downloadOptions, 
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            var (dedupManifestClient, clientTelemetry) = await DedupManifestArtifactClientFactory.Instance
                .CreateDedupManifestClientAsync(context.IsSystemDebugTrue(), (str) => context.Output(str), connection, cancellationToken);
            BuildServer buildServer = new BuildServer(connection);

            using (clientTelemetry)
            {
                // download all pipeline artifacts if artifact name is missing
                if (downloadOptions == DownloadOptions.MultiDownload)
                {
                    List<BuildArtifact> artifacts;
                    if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                    {
                        artifacts = await buildServer.GetArtifactsAsync(downloadParameters.ProjectId, downloadParameters.PipelineId, cancellationToken);
                    }
                    else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                    {
                        if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                        {
                            throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                        }
                        else
                        {
                            artifacts = await buildServer.GetArtifactsWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, cancellationToken);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                    }

                    IEnumerable<BuildArtifact> pipelineArtifacts = artifacts.Where(a => string.Equals(a.Resource.Type, PipelineArtifactConstants.PipelineArtifact, StringComparison.OrdinalIgnoreCase));
                    if (!pipelineArtifacts.Any())
                    {
                        throw new ArgumentException("Could not find any pipeline artifacts in the build.");
                    }
                    else
                    {
                        context.Output(StringUtil.Loc("DownloadingMultiplePipelineArtifacts", pipelineArtifacts.Count()));

                        var artifactNameAndManifestIds = pipelineArtifacts.ToDictionary(
                            keySelector: (a) => a.Name, // keys should be unique, if not something is really wrong
                            elementSelector: (a) => DedupIdentifier.Create(a.Resource.Data));
                        // 2) download to the target path
                        var options = DownloadDedupManifestArtifactOptions.CreateWithMultiManifestIds(
                            artifactNameAndManifestIds,
                            downloadParameters.TargetDirectory,
                            proxyUri: null,
                            minimatchPatterns: downloadParameters.MinimatchFilters,
                            minimatchFilterWithArtifactName: downloadParameters.MinimatchFilterWithArtifactName,
                            customMinimatchOptions: downloadParameters.CustomMinimatchOptions);

                        PipelineArtifactActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                            new PipelineArtifactActionRecord(level, uri, type, nameof(DownloadAsync), context));
                        await clientTelemetry.MeasureActionAsync(
                            record: downloadRecord,
                            actionAsync: async () =>
                            {
                                await AsyncHttpRetryHelper.InvokeVoidAsync(
                                    async () =>
                                    {
                                        await dedupManifestClient.DownloadAsync(options, cancellationToken);
                                    },
                                    maxRetries: 3,
                                    tracer: tracer,
                                    canRetryDelegate: e => true,
                                    context: nameof(DownloadAsync),
                                    cancellationToken: cancellationToken,
                                    continueOnCapturedContext: false);
                            });

                        // Send results to CustomerIntelligence
                        context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: downloadRecord);
                        }
                }
                else if (downloadOptions == DownloadOptions.SingleDownload)
                {
                    // 1) get manifest id from artifact data
                    BuildArtifact buildArtifact;
                    if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                    {
                        buildArtifact = await buildServer.GetArtifact(downloadParameters.ProjectId, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                    }
                    else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                    {
                        if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                        {
                            throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                        }
                        else
                        {
                            buildArtifact = await buildServer.GetArtifactWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                    }

                    var manifestId = DedupIdentifier.Create(buildArtifact.Resource.Data);
                    var options = DownloadDedupManifestArtifactOptions.CreateWithManifestId(
                        manifestId,
                        downloadParameters.TargetDirectory,
                        proxyUri: null,
                        minimatchPatterns: downloadParameters.MinimatchFilters,
                        customMinimatchOptions: downloadParameters.CustomMinimatchOptions);

                    PipelineArtifactActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineArtifactActionRecord>((level, uri, type) =>
                            new PipelineArtifactActionRecord(level, uri, type, nameof(DownloadAsync), context));
                    await clientTelemetry.MeasureActionAsync(
                        record: downloadRecord,
                        actionAsync: async () =>
                        {
                            await AsyncHttpRetryHelper.InvokeVoidAsync(
                                async () =>
                                {
                                    await dedupManifestClient.DownloadAsync(options, cancellationToken);
                                },
                                maxRetries: 3,
                                tracer: tracer,
                                canRetryDelegate: e => true,
                                context: nameof(DownloadAsync),
                                cancellationToken: cancellationToken,
                                continueOnCapturedContext: false);
                        });

                    // Send results to CustomerIntelligence
                    context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineArtifact, record: downloadRecord);
                }
                else
                {
                    throw new InvalidOperationException($"Invalid {nameof(downloadOptions)}!");
                }
            }
        }

        // Download for version 2. This decision was made because version 1 is sealed and we didn't want to break any existing customers.
        internal async Task DownloadAsyncV2(
            AgentTaskPluginExecutionContext context,
            ArtifactDownloadParameters downloadParameters,
            DownloadOptions downloadOptions,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BuildServer buildServer = new BuildServer(connection);

            // download all pipeline artifacts if artifact name is missing
            if (downloadOptions == DownloadOptions.MultiDownload)
            {
                List<BuildArtifact> artifacts;
                if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                {
                    artifacts = await buildServer.GetArtifactsAsync(downloadParameters.ProjectId, downloadParameters.PipelineId, cancellationToken);
                }
                else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                {
                    if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                    {
                        throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                    }
                    else
                    {
                        artifacts = await buildServer.GetArtifactsWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, cancellationToken);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                }

                IEnumerable<BuildArtifact> buildArtifacts = artifacts.Where(a => string.Equals(a.Resource.Type, PipelineArtifactConstants.Container, StringComparison.OrdinalIgnoreCase));
                IEnumerable<BuildArtifact> pipelineArtifacts = artifacts.Where(a => string.Equals(a.Resource.Type, PipelineArtifactConstants.PipelineArtifact, StringComparison.OrdinalIgnoreCase));
                IEnumerable<BuildArtifact> fileShareArtifacts = artifacts.Where(a => string.Equals(a.Resource.Type, PipelineArtifactConstants.FileShareArtifact, StringComparison.OrdinalIgnoreCase));

                if (buildArtifacts.Any())
                {
                    FileContainerProvider provider = new FileContainerProvider(connection, this.tracer);
                    await provider.DownloadMultipleArtifactsAsync(downloadParameters, buildArtifacts, cancellationToken, context);
                }

                if (pipelineArtifacts.Any())
                {
                    PipelineArtifactProvider provider = new PipelineArtifactProvider(context, connection, this.tracer);
                    await provider.DownloadMultipleArtifactsAsync(downloadParameters, pipelineArtifacts, cancellationToken, context);
                }

                if (fileShareArtifacts.Any()) 
                {
                    FileShareProvider provider = new FileShareProvider(context, connection, this.tracer);
                    await provider.DownloadMultipleArtifactsAsync(downloadParameters, fileShareArtifacts, cancellationToken, context);
                }
            }
            else if (downloadOptions == DownloadOptions.SingleDownload)
            {
                // 1) get manifest id from artifact data
                BuildArtifact buildArtifact;
                if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectId)
                {
                    buildArtifact = await buildServer.GetArtifact(downloadParameters.ProjectId, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                }
                else if (downloadParameters.ProjectRetrievalOptions == BuildArtifactRetrievalOptions.RetrieveByProjectName)
                {
                    if (string.IsNullOrEmpty(downloadParameters.ProjectName))
                    {
                        throw new InvalidOperationException("Project name can't be empty when trying to fetch build artifacts!");
                    }
                    else
                    {
                        buildArtifact = await buildServer.GetArtifactWithProjectNameAsync(downloadParameters.ProjectName, downloadParameters.PipelineId, downloadParameters.ArtifactName, cancellationToken);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Invalid {nameof(downloadParameters.ProjectRetrievalOptions)}!");
                }

                ArtifactProviderFactory factory = new ArtifactProviderFactory(context, connection, this.tracer);
                IArtifactProvider provider = factory.GetProvider(buildArtifact);
                
                await provider.DownloadSingleArtifactAsync(downloadParameters, buildArtifact, cancellationToken, context);
            }
            else
            {
                throw new InvalidOperationException($"Invalid {nameof(downloadOptions)}!");
            }
        }
    }

    internal enum DownloadOptions
    {
        SingleDownload,
        MultiDownload
    }
}
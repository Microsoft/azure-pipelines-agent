// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Plugins.PipelineArtifact
{
    // A client wrapper interacting with TFS/Build's Artifact API
    public class BuildServer
    {
        private readonly BuildHttpClient _buildHttpClient;

        public BuildServer(VssConnection connection)
        {
            ArgUtil.NotNull(connection, nameof(connection));
            connection.Settings.SendTimeout = TimeSpan.FromSeconds(300);
            ArgUtil.NotNull(connection, nameof(connection));
            _buildHttpClient = connection.GetClient<BuildHttpClient>();
        }

        // Associate the specified artifact with a build, along with custom data.
        public async Task<BuildArtifact> AssociateArtifactAsync(
            Guid projectId,
            int pipelineId,
            string name,
            string jobId,
            string type,
            string data,
            Dictionary<string, string> propertiesDictionary,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            BuildArtifact artifact = new BuildArtifact()
            {
                Name = name,
                Source = jobId,
                Resource = new ArtifactResource()
                {
                    Data = data,
                    Type = type,
                    Properties = propertiesDictionary
                }
            };

            return await _buildHttpClient.CreateArtifactAsync(artifact, projectId, pipelineId, cancellationToken: cancellationToken);
        }

        // Get named artifact from a build
        public async Task<BuildArtifact> GetArtifact(
            Guid projectId,
            int pipelineId,
            string name,
            CancellationToken cancellationToken)
        {
            return await _buildHttpClient.GetArtifactAsync(projectId, pipelineId, name, cancellationToken: cancellationToken);
        }

        public async Task<List<BuildArtifact>> GetArtifactsAsync(
            Guid project,
            int pipelineId,
            CancellationToken cancellationToken)
        {
            return await _buildHttpClient.GetArtifactsAsync(project, pipelineId, userState: null, cancellationToken: cancellationToken);
        }

        //Get artifact with project name.
        public async Task<BuildArtifact> GetArtifactWithProjectNameAsync(
            string project,
            int pipelineId,
            string name,
            CancellationToken cancellationToken)
        {
            return await _buildHttpClient.GetArtifactAsync(project, pipelineId, name, cancellationToken: cancellationToken);
        }

        public async Task<List<BuildArtifact>> GetArtifactsWithProjectNameAsync(
            string project,
            int pipelineId,
            CancellationToken cancellationToken)
        {
            return await _buildHttpClient.GetArtifactsAsync(project, pipelineId, userState: null, cancellationToken: cancellationToken);
        }

        public async Task<List<BuildDefinitionReference>> GetDefinitionsAsync(
            Guid project,
            string definitionName,
            CancellationToken cancellationToken)
        {
            return await _buildHttpClient.GetDefinitionsAsync(project, definitionName, cancellationToken: cancellationToken);
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Plugins.PipelineCache
{
    public class RestorePipelineCacheV0 : PipelineCacheTaskPluginBase
    {
        public override string Stage => "main";

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint fingerprint,
            Func<Fingerprint[]> restoreKeysGenerator,
            string path,
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            ArgUtil.NotNull(fingerprint, nameof(fingerprint));
            ArgUtil.NotNull(restoreKeysGenerator, nameof(restoreKeysGenerator));
            context.SetTaskVariable(RestoreStepRanVariableName, RestoreStepRanVariableValue);
            context.SetTaskVariable(ResolvedFingerPrintVariableName, fingerprint.ToString());

            var server = new PipelineCacheServer();
            Fingerprint[] restoreFingerprints = restoreKeysGenerator();
            await server.DownloadAsync(
                context,
                (new [] { fingerprint}).Concat(restoreFingerprints).ToArray(),
                path,
                context.GetInput(PipelineCacheTaskPluginConstants.CacheHitVariable, required: false),
                token);
        }
    }
}
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Plugins.PipelineCache
{
    public class SavePipelineCacheV0 : PipelineCacheTaskPluginBase
    {
        public override string Stage => "post";

        /* To mitigate the issue - https://github.com/microsoft/azure-pipelines-tasks/issues/10907, we need to check the restore condition logic, before creating the fingerprint.
           Hence we are overriding the RunAsync function to include that logic. */
        public override async Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            bool successSoFar = false;
            if (context.Variables.TryGetValue("agent.jobstatus", out VariableValue jobStatusVar))
            {
                if (Enum.TryParse<TaskResult>(jobStatusVar?.Value ?? string.Empty, true, out TaskResult jobStatus))
                {
                    if (jobStatus == TaskResult.Succeeded)
                    {
                        successSoFar = true;
                    }
                }
            }

            if (!successSoFar)
            {
                context.Info($"Skipping because the job status was not 'Succeeded'.");
                return;
            }

            bool restoreStepRan = false;
            if (context.TaskVariables.TryGetValue(RestoreStepRanVariableName, out VariableValue ran))
            {
                if (ran != null && ran.Value != null && ran.Value.Equals(RestoreStepRanVariableValue, StringComparison.Ordinal))
                {
                    restoreStepRan = true;
                }
            }

            if (!restoreStepRan)
            {
                context.Info($"Skipping because restore step did not run.");
                return;
            }

            await base.RunAsync(context, token);
        }

        protected override async Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint fingerprint,
            Func<Fingerprint[]> restoreKeysGenerator,
            string path,
            CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));
            ArgUtil.NotNull(fingerprint, nameof(fingerprint));
            string contentFormatValue  = context.Variables.GetValueOrDefault(ContentFormatVariableName)?.Value ?? string.Empty;
            string calculatedFingerPrint = context.TaskVariables.GetValueOrDefault(ResolvedFingerPrintVariableName)?.Value ?? string.Empty;

            if(!string.IsNullOrWhiteSpace(calculatedFingerPrint) && !fingerprint.ToString().Equals(calculatedFingerPrint, StringComparison.Ordinal))
            {
                context.Warning($"The given cache key has changed in its resolved value between restore and save steps;\n"+
                                $"original key: {calculatedFingerPrint}\n"+
                                $"modified key: {fingerprint}\n");
            }

            ContentFormat contentFormat;
            if (string.IsNullOrWhiteSpace(contentFormatValue))
            {
                contentFormat = ContentFormat.SingleTar;
            }
            else
            {
                contentFormat = Enum.Parse<ContentFormat>(contentFormatValue, ignoreCase: true);
            }

            PipelineCacheServer server = new PipelineCacheServer();
            await server.UploadAsync(
                context,
                fingerprint,
                path,
                token,
                contentFormat);
        }
    }
}

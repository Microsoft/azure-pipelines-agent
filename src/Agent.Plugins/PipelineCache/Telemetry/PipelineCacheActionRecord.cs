// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Agent.Sdk;
using Agent.Plugins.PipelineArtifact.Telemetry;
using Microsoft.VisualStudio.Services.Content.Common.Telemetry;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi.Telemetry;

namespace Agent.Plugins.PipelineCache.Telemetry
{
    /// <summary>
    /// Generic telemetry record for use with Pipeline Caching events.
    /// </summary>
    public class PipelineCacheActionRecord : PipelineCacheTelemetryRecord
    {
        public static long FileCount { get; private set; }

        public PipelineCacheActionRecord(TelemetryInformationLevel level, Uri baseAddress, string eventNamePrefix, string eventNameSuffix, AgentTaskPluginExecutionContext context, uint attemptNumber = 1)
            : base(
            level: level,
            baseAddress: baseAddress,
            eventNamePrefix: eventNamePrefix,
            eventNameSuffix: eventNameSuffix,
            planId: Guid.Parse(context?.Variables["system.planId"].Value),
            jobId: Guid.Parse(context?.Variables["system.jobId"].Value),
            taskInstanceId: Guid.Parse(context?.Variables["system.taskInstanceId"].Value),
            attemptNumber: attemptNumber)
        {
        }

        protected override void SetMeasuredActionResult<T>(T value)
        {
            if (value is PublishResult)
            {
                PublishResult result = value as PublishResult;
                FileCount = result.FileCount;
            }
            base.SetMeasuredActionResult(value);
        }
    }
}
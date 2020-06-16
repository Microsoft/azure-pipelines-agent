// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Content.Common.Telemetry;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Plugins.PipelineArtifact.Telemetry
{
    /// <summary>
    /// Generic telemetry record for use with Pipeline events.
    /// </summary>
    public abstract class PipelineTelemetryRecord : BlobStoreTelemetryRecord
    {
        public Guid PlanId { get; private set; }
        public Guid JobId { get; private set; }
        public Guid TaskInstanceId { get; private set; }

        public PipelineTelemetryRecord(TelemetryInformationLevel level, Uri baseAddress, string eventNamePrefix, string eventNameSuffix, AgentTaskPluginExecutionContext context, uint attemptNumber = 1)
            : base(level, baseAddress, eventNamePrefix, eventNameSuffix, attemptNumber)
        {
            PlanId = new Guid(context?.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.PlanId)?.Value ?? Guid.Empty.ToString());
            JobId = new Guid(context?.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.JobId)?.Value ?? Guid.Empty.ToString());
            TaskInstanceId = new Guid(context?.Variables.GetValueOrDefault(WellKnownDistributedTaskVariables.TaskInstanceId)?.Value ?? Guid.Empty.ToString());
        }
    }
}
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;

namespace Agent.Plugins.PipelineCache
{
    public enum ContentFormat
    {
        SingleTar,
        Files
    }

    public abstract class PipelineCacheTaskPluginBase : IAgentTaskPlugin
    {
        protected const string RestoreStepRanVariableName = "RESTORE_STEP_RAN";
        protected const string RestoreStepRanVariableValue = "true";
        private const string SaltVariableName = "AZP_CACHING_SALT";
        private const string OldKeyFormatMessage = "'key' format is changing to a single line: https://aka.ms/pipeline-caching-docs";
        protected const string ContentFormatVariableName = "AZP_CACHING_CONTENT_FORMAT";
        public Guid Id => PipelineCachePluginConstants.CacheTaskId;
        public abstract String Stage { get; }
        public const string ResolvedFingerPrintVariableName = "RESTORE_STEP_RESOLVED_FINGERPRINT";

        internal static (bool isOldFormat, string[] keySegments,IEnumerable<string[]> restoreKeys) ParseIntoSegments(string salt, string key, string restoreKeysBlock)
        {
            Func<string,string[]> splitAcrossPipes = (s) => {
                var segments = s.Split(new [] {'|'},StringSplitOptions.RemoveEmptyEntries).Select(segment => segment.Trim());
                if(!string.IsNullOrWhiteSpace(salt))
                {
                    segments = (new [] { $"{SaltVariableName}={salt}"}).Concat(segments);
                }
                return segments.ToArray();
            };

            Func<string,string[]> splitAcrossNewlines = (s) =>
                s.Replace("\r\n", "\n") //normalize newlines
                 .Split(new [] {'\n'}, StringSplitOptions.RemoveEmptyEntries)
                 .Select(line => line.Trim())
                 .ToArray();

            string[] keySegments;
            bool isOldFormat = key.Contains('\n');

            IEnumerable<string[]> restoreKeys;
            bool hasRestoreKeys = !string.IsNullOrWhiteSpace(restoreKeysBlock);

            if (isOldFormat && hasRestoreKeys)
            {
                throw new ArgumentException(OldKeyFormatMessage);
            }

            if (isOldFormat)
            {
                keySegments = splitAcrossNewlines(key);
            }
            else
            {
                keySegments = splitAcrossPipes(key);
            }


            if (hasRestoreKeys)
            {
                restoreKeys = splitAcrossNewlines(restoreKeysBlock).Select(restoreKey => splitAcrossPipes(restoreKey));
            }
            else
            {
                restoreKeys = Enumerable.Empty<string[]>();
            }

            return (isOldFormat, keySegments, restoreKeys);
        }

        public async virtual Task RunAsync(AgentTaskPluginExecutionContext context, CancellationToken token)
        {
            ArgUtil.NotNull(context, nameof(context));

            VariableValue saltValue = context.Variables.GetValueOrDefault(SaltVariableName);
            string salt = saltValue?.Value ?? string.Empty;

            VariableValue workspaceRootValue = context.Variables.GetValueOrDefault("pipeline.workspace");
            string workspaceRoot = workspaceRootValue?.Value;

            string key = context.GetInput(PipelineCacheTaskPluginConstants.Key, required: true);
            string restoreKeysBlock = context.GetInput(PipelineCacheTaskPluginConstants.RestoreKeys, required: false);

            (bool isOldFormat, string[] keySegments, IEnumerable<string[]> restoreKeys) = ParseIntoSegments(salt, key, restoreKeysBlock);

            if (isOldFormat)
            {
                ArgUtil.NotNull(context, nameof(context));
                context.Warning(OldKeyFormatMessage);
            }
            ArgUtil.NotNull(context, nameof(context));
            context.Output("Resolving key:");
            Fingerprint keyFp = FingerprintCreator.EvaluateKeyToFingerprint(context, workspaceRoot, keySegments);
            context.Output($"Resolved to: {keyFp}");

            Func<Fingerprint[]> restoreKeysGenerator = () =>
                restoreKeys.Select(restoreKey => {
                    context.Output("Resolving restore key:");
                    Fingerprint f = FingerprintCreator.EvaluateKeyToFingerprint(context, workspaceRoot, restoreKey);
                    f.Segments = f.Segments.Concat(new [] { Fingerprint.Wildcard} ).ToArray();
                    context.Output($"Resolved to: {f}");
                    return f;
                }).ToArray();

            // TODO: Translate path from container to host (Ting)
            string path = context.GetInput(PipelineCacheTaskPluginConstants.Path, required: true);

            await ProcessCommandInternalAsync(
                context,
                keyFp,
                restoreKeysGenerator,
                path,
                token);
        }

        // Process the command with preprocessed arguments.
        protected abstract Task ProcessCommandInternalAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint fingerprint,
            Func<Fingerprint[]> restoreKeysGenerator,
            string path,
            CancellationToken token);

        // Properties set by tasks
        protected static class PipelineCacheTaskPluginConstants
        {
            public static readonly string Key = "key"; // this needs to match the input in the task.
            public static readonly string RestoreKeys = "restoreKeys";
            public static readonly string Path = "path";
            public static readonly string PipelineId = "pipelineId";
            public static readonly string CacheHitVariable = "cacheHitVar";
            public static readonly string Salt = "salt";
        }
    }
}
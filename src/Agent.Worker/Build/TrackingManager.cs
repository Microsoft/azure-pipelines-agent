// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.VisualStudio.Services.Agent.Util;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    /// <summary>
    /// This class manages the tracking config files used by the worker to determine where sources are located.
    /// There is a single file per pipeline. We use a hash to determine if the file needs to be updated.
    /// The tracking configs are "garbage collected" if any of the repositories are changed.
    /// I.e. the repo url is different than last build.
    /// The config file format has changed over time and must remain backwards compatible to avoid unneeded recloning.
    /// </summary>
    [ServiceLocator(Default = typeof(TrackingManager))]
    public interface ITrackingManager : IAgentService
    {
        TrackingConfig LoadExistingTrackingConfig(
            IExecutionContext executionContext);

        TrackingConfig Create(
            IExecutionContext executionContext,
            IList<RepositoryResource> repositories,
            bool overrideBuildDirectory);

        bool AreTrackingConfigsCompatible(
            IExecutionContext executionContext,
            TrackingConfig newConfig,
            TrackingConfig previousConfig);

        TrackingConfig MergeTrackingConfigs(
            IExecutionContext executionContext,
            TrackingConfig newConfig,
            TrackingConfig previousConfig);

        void UpdateTrackingConfig(
            IExecutionContext executionContext,
            TrackingConfig modifiedConfig);

        IEnumerable<TrackingConfig> EnumerateAllTrackingConfigs(IExecutionContext executionContext);

        void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfig config);

        void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration);

        void DisposeCollectedGarbage(IExecutionContext executionContext);

        void MaintenanceStarted(TrackingConfig config);

        void MaintenanceCompleted(TrackingConfig config);
    }

    public sealed class TrackingManager : AgentService, ITrackingManager
    {
        private TopLevelTrackingConfig topLevelConfig;

        public TrackingConfig Create(
            IExecutionContext executionContext,
            IList<RepositoryResource> repositories,
            bool overrideBuildDirectory)
        {
            Trace.Entering();

            EnsureTopLevelTrackingConfigLoaded(executionContext);

            // Determine the build directory.
            if (overrideBuildDirectory)
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.

                // This line recently started causing issues described in FEEDBACKTICKET 1649233
                // We think this is related to the refactor of topLevelConfig going from a local variable in this method
                // to being a private class variable of this object.
                // ArgUtil.Equal(default(int), topLevelConfig.LastBuildDirectoryNumber, nameof(topLevelConfig.LastBuildDirectoryNumber));

                var configurationStore = HostContext.GetService<IConfigurationStore>();
                AgentSettings settings = configurationStore.GetSettings();
                Trace.Verbose($"Overriding LastBuildDirectoryNumber from {topLevelConfig.LastBuildDirectoryNumber} to {settings.AgentId}");
                topLevelConfig.LastBuildDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastBuildDirectoryNumber++;
            }

            // Create the new tracking config.
            TrackingConfig config = new TrackingConfig(
                executionContext,
                repositories,
                topLevelConfig.LastBuildDirectoryNumber);
            return config;
        }

        public bool AreTrackingConfigsCompatible(
            IExecutionContext executionContext,
            TrackingConfig newConfig,
            TrackingConfig previousConfig)
        {
            return
                newConfig != null &&
                previousConfig != null &&
                string.Equals(newConfig.HashKey, previousConfig.HashKey, StringComparison.OrdinalIgnoreCase);
        }

        public TrackingConfig MergeTrackingConfigs(
            IExecutionContext executionContext,
            TrackingConfig newConfig,
            TrackingConfig previousConfig)
        {
            ArgUtil.NotNull(newConfig, nameof(newConfig));
            ArgUtil.NotNull(previousConfig, nameof(previousConfig));

            Trace.Entering();

            TrackingConfig mergedConfig = previousConfig.Clone();

            // Update the sources directory if we don't have one
            if (!string.IsNullOrEmpty(mergedConfig.SourcesDirectory))
            {
                mergedConfig.SourcesDirectory = newConfig.SourcesDirectory;
            }

            // Fill out repository type if it's not there.
            // repository type is a new property introduced for maintenance job
            if (string.IsNullOrEmpty(mergedConfig.RepositoryType))
            {
                mergedConfig.RepositoryType = newConfig.RepositoryType;
            }

            if (string.IsNullOrEmpty(mergedConfig.CollectionUrl))
            {
                mergedConfig.CollectionUrl = newConfig.CollectionUrl;
            }

            return previousConfig;
        }

        public void UpdateTrackingConfig(
            IExecutionContext executionContext,
            TrackingConfig modifiedConfig)
        {
            ArgUtil.NotNull(modifiedConfig, nameof(modifiedConfig));

            Trace.Entering();

            Trace.Verbose("Updating job run properties.");
            UpdateJobRunProperties(executionContext, modifiedConfig);

            bool isNewFile = string.IsNullOrEmpty(modifiedConfig.FileLocation);
            if (isNewFile && topLevelConfig != null)
            {
                // Update the top-level tracking config.
                Trace.Verbose("Updating top level config.");
                topLevelConfig.LastBuildDirectoryCreatedOn = DateTimeOffset.Now;
                WriteToFile(GetTopLevelTrackingFileLocation(), topLevelConfig);
            }
        }

        public TrackingConfig LoadExistingTrackingConfig(
            IExecutionContext executionContext)
        {
            Trace.Entering();

            ArgUtil.NotNull(executionContext, nameof(executionContext));
            // First, attempt to load the file from the new location (collection, definition, workspaceId)
            string trackingFileLocation = GetTrackingFileLocation(executionContext, true);
            var trackingConfig = LoadIfExists(executionContext, trackingFileLocation);
            if (trackingConfig == null)
            {
                // If it's not in the new location, look for it in the old location
                trackingFileLocation = GetTrackingFileLocation(executionContext, false);
                trackingConfig = LoadIfExists(executionContext, trackingFileLocation);
            }

            return trackingConfig;
        }

        public void MarkForGarbageCollection(
            IExecutionContext executionContext,
            TrackingConfig config)
        {
            Trace.Entering();

            // Write a copy of the tracking config to the GC folder.
            WriteToFile(GetGarbageFileLocation(), config);
        }

        public void MaintenanceStarted(TrackingConfig config)
        {
            ArgUtil.NotNull(config, nameof(config));

            Trace.Entering();
            config.LastMaintenanceAttemptedOn = DateTimeOffset.Now;
            config.LastMaintenanceCompletedOn = null;
            WriteToFile(config.FileLocation, config);
        }

        public void MaintenanceCompleted(TrackingConfig config)
        {
            ArgUtil.NotNull(config, nameof(config));

            Trace.Entering();
            config.LastMaintenanceCompletedOn = DateTimeOffset.Now;
            WriteToFile(config.FileLocation, config);
        }

        public IEnumerable<TrackingConfig> EnumerateAllTrackingConfigs(IExecutionContext executionContext)
        {
            Trace.Entering();

            Trace.Info("Scan all SourceFolder tracking files.");
            string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            if (!Directory.Exists(searchRoot))
            {
                Trace.Info($"Search root does not exist. searchRoot={searchRoot}");
                yield break;
            }

            var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
            Trace.Info($"Found {allTrackingFiles.Count()} tracking files.");

            foreach (var trackingFile in allTrackingFiles)
            {
                TrackingConfig trackingConfig = LoadIfExists(executionContext, trackingFile);
                if (trackingConfig != null)
                {
                    Trace.Verbose($"Found {trackingFile} and parsed correctly.");
                    yield return trackingConfig;
                }
                else
                {
                    // Return an empty config so the caller can remove the file if needed
                    Trace.Info($"{trackingFile} could not be parsed correctly.");
                    yield return new TrackingConfig() { FileLocation = trackingFile, HashKey = "" };
                }
            }

            // End the iterator
            yield break;
        }

        public void MarkExpiredForGarbageCollection(
            IExecutionContext executionContext,
            TimeSpan expiration)
        {
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(expiration, nameof(expiration));

            Trace.Entering();

            Trace.Info("Scan all SourceFolder tracking files.");
            string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            if (!Directory.Exists(searchRoot))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
                return;
            }

            executionContext.Output(StringUtil.Loc("DirExpireLimit", expiration.TotalDays));
            executionContext.Output(StringUtil.Loc("CurrentUTC", DateTime.UtcNow.ToString("o")));

            // scan all sourcefolder tracking file, find which folder has never been used since UTC-expiration
            // the scan and garbage discovery should be best effort.
            // if the tracking file is in old format, just delete the folder since the first time the folder been use we will convert the tracking file to new format.
            foreach (var config in EnumerateAllTrackingConfigs(executionContext))
            {
                try
                {
                    executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", config.FileLocation));

                    // Check the last run on time against the expiration
                    ArgUtil.NotNull(config.LastRunOn, nameof(config.LastRunOn));
                    executionContext.Output(StringUtil.Loc("BuildDirLastUseTIme", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.BuildDirectory), config.LastRunOn?.ToString("u")));
                    if (DateTime.UtcNow - expiration > config.LastRunOn)
                    {
                        // The config has expired, clean it up
                        executionContext.Output(StringUtil.Loc("GCUnusedTrackingFile", config.FileLocation, expiration.TotalDays));
                        MarkForGarbageCollection(executionContext, config);
                        IOUtil.DeleteFile(config.FileLocation);
                    }
                }
                catch (Exception ex)
                {
                    Trace.Info($"config.FileLocation={config.FileLocation}; config.HashKey={config.HashKey}; config.LastRunOn={config.LastRunOn}");

                    executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", config.FileLocation));
                    executionContext.Error(ex);
                }
            }
        }

        public void DisposeCollectedGarbage(IExecutionContext executionContext)
        {
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            Trace.Entering();
            PrintOutDiskUsage(executionContext);

            string gcDirectory = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);

            if (!Directory.Exists(gcDirectory))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", gcDirectory));
                return;
            }

            IEnumerable<string> gcTrackingFiles = Directory.EnumerateFiles(gcDirectory, "*.json");
            if (gcTrackingFiles == null || !gcTrackingFiles.Any())
            {
                executionContext.Output(StringUtil.Loc("GCDirIsEmpty", gcDirectory));
                return;
            }

            Trace.Info($"Find {gcTrackingFiles.Count()} GC tracking files.");

            if (gcTrackingFiles.Any())
            {
                foreach (string gcFile in gcTrackingFiles)
                {
                    // maintenance has been cancelled.
                    executionContext.CancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var gcConfig = LoadIfExists(executionContext, gcFile) as TrackingConfig;
                        ArgUtil.NotNull(gcConfig, nameof(TrackingConfig));

                        string fullPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), gcConfig.BuildDirectory);
                        executionContext.Output(StringUtil.Loc("Deleting", fullPath));
                        IOUtil.DeleteDirectory(fullPath, executionContext.CancellationToken);

                        executionContext.Output(StringUtil.Loc("DeleteGCTrackingFile", fullPath));
                        IOUtil.DeleteFile(gcFile);
                    }
                    catch (Exception ex)
                    {
                        executionContext.Error(StringUtil.Loc("ErrorDuringBuildGCDelete", gcFile));
                        executionContext.Error(ex);
                    }
                }

                PrintOutDiskUsage(executionContext);
            }
        }

        private TrackingConfig LoadIfExists(
            IExecutionContext executionContext,
            string file)
        {
            Trace.Entering();

            Trace.Verbose($"Loading {file}");

            // The tracking config will not exist for a new definition.
            if (!File.Exists(file))
            {
                Trace.Verbose($"Tracking file does not exist: {file}");
                return null;
            }

            TrackingConfig result = null;

            // Load the content and distinguish between tracking config file
            // version 1 and file version 2.
            string content = File.ReadAllText(file);
            string fileFormatVersionJsonProperty = StringUtil.Format(
                @"""{0}""",
                TrackingConfig.FileFormatVersionJsonProperty);
            if (content.Contains(fileFormatVersionJsonProperty))
            {
                // The config is the new format.
                Trace.Verbose("Parsing new tracking config format.");
                result = JsonConvert.DeserializeObject<TrackingConfig>(content);
                if (result != null)
                {
                    // if RepositoryTrackingInfo is empty, then we should create an entry so the rest
                    // of the logic after this will act correctly
                    if (result.RepositoryTrackingInfo.Count == 0)
                    {
                        result.RepositoryTrackingInfo.Add(new Build.RepositoryTrackingInfo
                        {
                            Identifier = RepositoryUtil.DefaultPrimaryRepositoryName,
                            RepositoryType = result.RepositoryType,
                            RepositoryUrl = result.RepositoryUrl,
                            SourcesDirectory = result.SourcesDirectory,
                        });
                    }
                }
            }
            else
            {
                // Attempt to parse the legacy format.
                Trace.Verbose("Parsing legacy tracking config format.");
                var legacyTrackingConfig = LegacyTrackingConfig.TryParse(content);
                if (legacyTrackingConfig == null)
                {
                    executionContext.Warning(StringUtil.Loc("UnableToParseBuildTrackingConfig0", content));
                }
                else
                {
                    // Convert legacy format to the new format.
                    result = ConvertToNewFormat(
                        executionContext,
                        legacyTrackingConfig,
                        RepositoryUtil.GetCloneDirectory(legacyTrackingConfig.RepositoryUrl),
                        RepositoryUtil.GuessRepositoryType(legacyTrackingConfig.RepositoryUrl));
                }
            }

            if (result != null)
            {
                result.FileLocation = file;
            }

            return result;
        }

        private void EnsureTopLevelTrackingConfigLoaded(IExecutionContext executionContext)
        {
            // Get or create the top-level tracking config.
            string topLevelFile = GetTopLevelTrackingFileLocation();
            TopLevelTrackingConfig topLevelConfig = null;


            if (!File.Exists(topLevelFile))
            {
                Trace.Verbose($"Creating default top-level tracking config: {topLevelFile}");
                topLevelConfig = new TopLevelTrackingConfig();
            }
            else
            {
                Trace.Verbose($"Loading top-level tracking config: {topLevelFile}");
                topLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    executionContext.Warning($"Rebuild corrupted top-level tracking configure file {topLevelFile}.");
                    // save the corruptted file in case we need to investigate more.
                    File.Copy(topLevelFile, $"{topLevelFile}.corrupted", true);

                    topLevelConfig = new TopLevelTrackingConfig();
                    DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));

                    foreach (var dir in workDir.EnumerateDirectories())
                    {
                        // we scan the entire _work directory and find the directory with the highest integer number.
                        if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                            lastBuildNumber > topLevelConfig.LastBuildDirectoryNumber)
                        {
                            topLevelConfig.LastBuildDirectoryNumber = lastBuildNumber;
                        }
                    }
                    Trace.Verbose($"Top-level tracking config was corrupted. Setting LastBuildDirectoryNumber to {topLevelConfig.LastBuildDirectoryNumber}");
                }
            }

            // Put the instance of the config in the member variable.
            this.topLevelConfig = topLevelConfig;
        }

        private string GetTopLevelTrackingFileLocation()
        {
            return Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.TopLevelTrackingConfigFile);
        }

        private string GetTrackingFileLocation(IExecutionContext executionContext, bool includeWorkspaceId)
        {
            string workspaceId = null;
            if (includeWorkspaceId && executionContext.JobSettings?.TryGetValue(WellKnownJobSettings.WorkspaceIdentifier, out workspaceId) == true)
            {
                return Path.Combine(
                    HostContext.GetDirectory(WellKnownDirectory.Work),
                    Constants.Build.Path.SourceRootMappingDirectory,
                    executionContext.Variables.System_CollectionId,
                    executionContext.Variables.System_DefinitionId,
                    workspaceId,
                    Constants.Build.Path.TrackingConfigFile);
            }

            return Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                Constants.Build.Path.TrackingConfigFile);
        }

        private string GetGarbageFileLocation()
        {
            string gcDirectory = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);
            string file = Path.Combine(
                gcDirectory,
                StringUtil.Format("{0}.json", Guid.NewGuid()));
            return file;
        }

        private void UpdateJobRunProperties(
            IExecutionContext executionContext,
            TrackingConfig config)
        {
            Trace.Entering();

            // Update the info properties and save the file.
            config.UpdateJobRunProperties(executionContext);

            // Make sure we clean up any files in the old location (no workspace id in the path)
            string oldLocation = GetTrackingFileLocation(executionContext, false);
            if (File.Exists(oldLocation))
            {
                File.Delete(oldLocation);
            }

            WriteToFile(GetTrackingFileLocation(executionContext, true), config);
        }

        private void PrintOutDiskUsage(IExecutionContext context)
        {
            // Print disk usage should be best effort, since DriveInfo can't detect usage of UNC share.
            try
            {
                context.Output($"Disk usage for working directory: {HostContext.GetDirectory(WellKnownDirectory.Work)}");
                var workDirectoryDrive = new DriveInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                long freeSpace = workDirectoryDrive.AvailableFreeSpace;
                long totalSpace = workDirectoryDrive.TotalSize;
                if (PlatformUtil.RunningOnWindows)
                {
                    context.Output($"Working directory belongs to drive: '{workDirectoryDrive.Name}'");
                }
                else
                {
                    context.Output($"Information about file system on which working directory resides.");
                }
                context.Output($"Total size: '{totalSpace / 1024.0 / 1024.0} MB'");
                context.Output($"Available space: '{freeSpace / 1024.0 / 1024.0} MB'");
            }
            catch (Exception ex)
            {
                context.Warning($"Unable inspect disk usage for working directory {HostContext.GetDirectory(WellKnownDirectory.Work)}.");
                Trace.Error(ex);
                context.Debug(ex.ToString());
            }
        }

        private void WriteToFile(string file, object value)
        {
            Trace.Entering();
            Trace.Verbose($"Writing config to file: {file}");

            // Create the directory if it does not exist.
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            IOUtil.SaveObject(value, file);
        }

        private TrackingConfig ConvertToNewFormat(
            IExecutionContext executionContext,
            LegacyTrackingConfig legacyConfig,
            string suggestedSourceFolderName,
            string repositoryType)
        {
            Trace.Entering();

            if (legacyConfig == null)
            {
                return null;
            }

            // Determine the source directory name. Check if the directory is named "s" already.
            // Convert the source directory to be named "s" if there is a problem with the old name.
            string sourcesDirectoryNameOnly = Constants.Build.Path.SourcesDirectory;
            if (!Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, sourcesDirectoryNameOnly))
                && !String.Equals(suggestedSourceFolderName, Constants.Build.Path.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(suggestedSourceFolderName, Constants.Build.Path.LegacyArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(suggestedSourceFolderName, Constants.Build.Path.LegacyStagingDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(suggestedSourceFolderName, Constants.Build.Path.TestResultsDirectory, StringComparison.OrdinalIgnoreCase)
                && !suggestedSourceFolderName.Contains("\\")
                && !suggestedSourceFolderName.Contains("/")
                && Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, suggestedSourceFolderName)))
            {
                sourcesDirectoryNameOnly = suggestedSourceFolderName;
            }

            // Convert to the new format.
            var newConfig = new TrackingConfig(
                executionContext,
                legacyConfig,
                sourcesDirectoryNameOnly,
                repositoryType,
                useNewArtifactsDirectoryName: false);

            return newConfig;
        }
    }
}
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;

namespace Agent.Plugins.PipelineCache
{
    public static class TarUtils
    {
        public const string TarLocationEnvironmentVariableName = "VSTS_TAR_EXECUTABLE";

        private readonly static bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private const string archive = "archive.tar";

        /// <summary>
        /// Will archive files in the input path into a TAR file.
        /// </summary>
        /// <returns>The path to the TAR.</returns>
        public static async Task<string> ArchiveFilesToTarAsync(
            AgentTaskPluginExecutionContext context,
            string[] pathSegments,
            string workspace,
            CancellationToken cancellationToken)
        {
            foreach (var inputPath in pathSegments)
            {
                if (File.Exists(inputPath))
                {
                    throw new DirectoryNotFoundException($"Please specify path to a directory, File path is not allowed. {inputPath} is a file.");
                }
            }

            var archiveFileName = CreateArchiveFileName();
            var archiveFile = Path.Combine(Path.GetTempPath(), archiveFileName);

            ProcessStartInfo processStartInfo = GetCreateTarProcessInfo(context, archiveFile, pathSegments, workspace);

            Action actionOnFailure = () =>
            {
                // Delete archive file.
                TryDeleteFile(archiveFile);
            };

            await RunProcessAsync(
                context,
                processStartInfo,
                // no additional tasks on create are required to run whilst running the TAR process
                (Process process, CancellationToken ct) => Task.CompletedTask,
                actionOnFailure,
                cancellationToken);

            return archiveFile;
        }

        /// <summary>
        /// This will download the dedup into stdin stream while extracting the TAR simulataneously (piped). This is done by
        /// starting the download through a Task and starting the TAR/7z process which is reading from STDIN.
        /// </summary>
        /// <remarks>
        /// Windows will use 7z to extract the TAR file (only if 7z is installed on the machine and is part of PATH variables). 
        /// Non-Windows machines will extract TAR file using the 'tar' command'.
        /// </remarks>
        public static Task DownloadAndExtractTarAsync(
            AgentTaskPluginExecutionContext context,
            Manifest manifest,
            DedupManifestArtifactClient dedupManifestClient,
            string workspace,
            CancellationToken cancellationToken)
        {
            ValidateTarManifest(manifest);

            DedupIdentifier dedupId = DedupIdentifier.Create(manifest.Items.Single(i => i.Path.EndsWith(archive, StringComparison.OrdinalIgnoreCase)).Blob.Id);

            // We now can simply specify the working directory as the tarball will contain paths relative to it
            ProcessStartInfo processStartInfo = GetExtractStartProcessInfo(context, workspace);

            Func<Process, CancellationToken, Task> downloadTaskFunc =
                (process, ct) =>
                Task.Run(async () =>
                {
                    try
                    {
                        await dedupManifestClient.DownloadToStreamAsync(dedupId, process.StandardInput.BaseStream, proxyUri: null, cancellationToken: ct);
                        process.StandardInput.BaseStream.Close();
                    }
                    catch (Exception e)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch { }
                        ExceptionDispatchInfo.Capture(e).Throw();
                    }
                });

            return RunProcessAsync(
                context,
                processStartInfo,
                downloadTaskFunc,
                () => { },
                cancellationToken);
        }

        internal static async Task RunProcessAsync(
            AgentTaskPluginExecutionContext context,
            ProcessStartInfo processStartInfo,
            Func<Process, CancellationToken, Task> additionalTaskToExecuteWhilstRunningProcess,
            Action actionOnFailure,
            CancellationToken cancellationToken)
        {
            using (var process = new Process())
            {
                process.StartInfo = processStartInfo;
                process.EnableRaisingEvents = true;

                try
                {
                    context.Debug($"Starting '{process.StartInfo.FileName}' with arguments '{process.StartInfo.Arguments}'...");
                    process.Start();
                }
                catch (Exception e)
                {
                    // couldn't start the process, so throw a slightly nicer message about required dependencies:
                    throw new InvalidOperationException($"Failed to start the required dependency '{process.StartInfo.FileName}'.  Please verify the correct version is installed and available on the path.", e);
                }

                // Our goal is to always have the process ended or killed by the time we exit the function.
                try
                {
                    await additionalTaskToExecuteWhilstRunningProcess(process, cancellationToken);
                    process.WaitForExit();

                    int exitCode = process.ExitCode;

                    if (exitCode == 0)
                    {
                        context.Output($"Process exit code: {exitCode}");
                    }
                    else
                    {
                        throw new Exception($"Process returned non-zero exit code: {exitCode}");
                    }
                }
                catch (Exception e)
                {
                    actionOnFailure();
                    ExceptionDispatchInfo.Capture(e).Throw();
                }
            }
        }

        private static void CreateProcessStartInfo(ProcessStartInfo processStartInfo, string processFileName, string processArguments, string processWorkingDirectory)
        {
            processStartInfo.FileName = processFileName;
            processStartInfo.Arguments = processArguments;
            processStartInfo.UseShellExecute = false;
            processStartInfo.RedirectStandardInput = true;
            processStartInfo.WorkingDirectory = processWorkingDirectory;
        }

        private static ProcessStartInfo GetCreateTarProcessInfo(AgentTaskPluginExecutionContext context, string archiveFilePath, string[] inputPaths, string workspace)
        {
            var processFileName = GetTar(context);

            // TODO: Add unit tests for this path expansion
            inputPaths = inputPaths
                .Select(i => Path.IsPathFullyQualified(i) ? i : Path.Combine(workspace, i))
                .Select(i => Path.GetRelativePath(workspace, i))
                .Select(i => $"\"{i.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\"")
                .ToArray();

            workspace = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // If given the absolute path for the '-cf' option, the GNU tar fails. The workaround is to start the tarring process in the temp directory, and simply speficy 'archive.tar' for that option.
            var processArguments = $"-cf \"{archiveFilePath}\" -C \"{workspace}\" {string.Join(' ', inputPaths)}";

            if (IsSystemDebugTrue(context))
            {
                processArguments = "-v " + processArguments;
            }
            if (isWindows)
            {
                processArguments = "-h " + processArguments;
            }

            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            CreateProcessStartInfo(processStartInfo, processFileName, processArguments, processWorkingDirectory: Path.GetTempPath()); // We want to create the archiveFile in temp folder, and hence starting the tar process from TEMP to avoid absolute paths in tar cmd line.
            return processStartInfo;
        }

        private static string GetTar(AgentTaskPluginExecutionContext context)
        {
            // check if the user specified the tar executable to use:
            string location = Environment.GetEnvironmentVariable(TarLocationEnvironmentVariableName);
            return String.IsNullOrWhiteSpace(location) ? "tar" : location;
        }

        private static ProcessStartInfo GetExtractStartProcessInfo(AgentTaskPluginExecutionContext context, string workspace)
        {
            // TODO: Get common directory of path segments
            //var targetDirectory = pathSegments[0];
            string processFileName, processArguments;
            if (isWindows && CheckIf7ZExists())
            {
                //processFileName = "7z";
                //processArguments = $"x -si -aoa -o\"{targetDirectory}\" -ttar";
                //if (IsSystemDebugTrue(context))
                //{
                //    processArguments = "-bb1 " + processArguments;
                //}
                // TODO: Verify this works
                processFileName = "7z";
                processArguments = $"x -si -aoa -o\"{workspace}\" -ttar";
                if (IsSystemDebugTrue(context))
                {
                    processArguments = "-bb1 " + processArguments;
                }
            }
            else
            {
                processFileName = GetTar(context);
                // Instead of targetDirectory, we are providing . to tar, because the tar process is being started from targetDirectory.
                // -P is added so that relative paths are accepted by tar
                processArguments = $"-xf - -P -C .";
                if (IsSystemDebugTrue(context))
                {
                    processArguments = "-v " + processArguments;
                }
            }

            ProcessStartInfo processStartInfo = new ProcessStartInfo();
            CreateProcessStartInfo(processStartInfo, processFileName, processArguments, processWorkingDirectory: workspace); // TODO: is this the right directory?
            return processStartInfo;
        }

        private static void ValidateTarManifest(Manifest manifest)
        {
            // TODO: This will need to change
            if (manifest == null || manifest.Items.Count() != 1 || !manifest.Items.Single().Path.EndsWith(archive, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Manifest containing a tar cannot have more than one item.");
            }
        }

        private static void TryDeleteFile(string fileName)
        {
            try
            {
                if (File.Exists(fileName))
                {
                    File.Delete(fileName);
                }
            }
            catch { }
        }

        private static string CreateArchiveFileName()
        {
            return $"{Guid.NewGuid().ToString("N")}_{archive}";
        }

        private static bool IsSystemDebugTrue(AgentTaskPluginExecutionContext context)
        {
            if (context.Variables.TryGetValue("system.debug", out VariableValue systemDebugVar))
            {
                return string.Equals(systemDebugVar?.Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool CheckIf7ZExists()
        {
            using (var process = new Process())
            {
                process.StartInfo.FileName = "7z";
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true;
                try
                {
                    process.Start();
                }
                catch
                {
                    return false;
                }
                return true;
            }
        }
    }
}

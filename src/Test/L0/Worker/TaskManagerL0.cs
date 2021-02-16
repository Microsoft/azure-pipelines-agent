// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Worker;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Worker
{
    public sealed class TaskManagerL0
    {
        private Mock<IJobServer> _jobServer;
        private Mock<ITaskServer> _taskServer;
        private Mock<IConfigurationStore> _configurationStore;
        private Mock<IExecutionContext> _ec;
        private TaskManager _taskManager;
        private string _workFolder;

        //Test the cancellation flow: interrupt download task via HostContext cancellation token.
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void BubblesCancellation()
        {
            try
            {
                //Arrange
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    var bingTask = new Pipelines.TaskStep()
                    {
                        Enabled = true,
                        Reference = new Pipelines.TaskStepDefinitionReference()
                        {
                            Name = "Bing",
                            Version = "0.1.2",
                            Id = Guid.NewGuid()
                        }
                    };
                    var pingTask = new Pipelines.TaskStep()
                    {
                        Enabled = true,
                        Reference = new Pipelines.TaskStepDefinitionReference()
                        {
                            Name = "Ping",
                            Version = "0.1.1",
                            Id = Guid.NewGuid()
                        }
                    };

                    var bingVersion = new TaskVersion(bingTask.Reference.Version);
                    var pingVersion = new TaskVersion(pingTask.Reference.Version);

                    _taskServer
                        .Setup(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()))
                        .Returns((Guid taskId, TaskVersion taskVersion, CancellationToken token) =>
                        {
                            tokenSource.Cancel();
                            tokenSource.Token.ThrowIfCancellationRequested();
                            return null;
                        });

                    var tasks = new List<Pipelines.TaskStep>(new Pipelines.TaskStep[] { bingTask, pingTask });

                    //Act
                    //should initiate a download with a mocked IJobServer, which sets a cancellation token and
                    //download task is expected to be in cancelled state
                    Task downloadTask = _taskManager.DownloadAsync(_ec.Object, tasks);
                    Task[] taskToWait = { downloadTask, Task.Delay(2000) };
                    //wait for the task to be cancelled to exit
                    await Task.WhenAny(taskToWait);

                    //Assert
                    //verify task completed in less than 2sec and it is in cancelled state
                    Assert.True(downloadTask.IsCompleted, $"{nameof(_taskManager.DownloadAsync)} timed out.");
                    Assert.True(!downloadTask.IsFaulted, downloadTask.Exception?.ToString());
                    Assert.True(downloadTask.IsCanceled);
                    //check if the task.json was not downloaded for ping and bing tasks
                    Assert.Equal(
                        0,
                        Directory.GetFiles(_hc.GetDirectory(WellKnownDirectory.Tasks), "*", SearchOption.AllDirectories).Length);
                    //assert download was invoked only once, because the first task cancelled the second task download
                    _taskServer
                        .Verify(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()), Times.Once());
                }
            }
            finally
            {
                Teardown();
            }
        }

        //Test how exceptions are propagated to the caller.
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void RetryNetworkException()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    var pingTask = new Pipelines.TaskStep()
                    {
                        Enabled = true,
                        Reference = new Pipelines.TaskStepDefinitionReference()
                        {
                            Name = "Ping",
                            Version = "0.1.1",
                            Id = Guid.NewGuid()
                        }
                    };

                    var pingVersion = new TaskVersion(pingTask.Reference.Version);
                    Exception expectedException = new System.Net.Http.HttpRequestException("simulated network error");
                    _taskServer
                        .Setup(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()))
                        .Returns((Guid taskId, TaskVersion taskVersion, CancellationToken token) =>
                        {
                            throw expectedException;
                        });

                    var tasks = new List<Pipelines.TaskStep>(new Pipelines.TaskStep[] { pingTask });

                    //Act
                    Exception actualException = null;
                    try
                    {
                        await _taskManager.DownloadAsync(_ec.Object, tasks);
                    }
                    catch (Exception ex)
                    {
                        actualException = ex;
                    }

                    //Assert
                    //verify task completed in less than 2sec and it is in failed state state
                    Assert.Equal(expectedException, actualException);

                    //assert download was invoked 3 times, because we retry on task download
                    _taskServer
                        .Verify(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

                    //see if the task.json was not downloaded
                    Assert.Equal(
                        0,
                        Directory.GetFiles(_hc.GetDirectory(WellKnownDirectory.Tasks), "*", SearchOption.AllDirectories).Length);
                }
            }
            finally
            {
                Teardown();
            }
        }

        //Test how exceptions are propagated to the caller.
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void RetryStreamException()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    var pingTask = new Pipelines.TaskStep()
                    {
                        Enabled = true,
                        Reference = new Pipelines.TaskStepDefinitionReference()
                        {
                            Name = "Ping",
                            Version = "0.1.1",
                            Id = Guid.NewGuid()
                        }
                    };

                    var pingVersion = new TaskVersion(pingTask.Reference.Version);
                    Exception expectedException = new System.Net.Http.HttpRequestException("simulated network error");
                    _taskServer
                        .Setup(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()))
                        .Returns((Guid taskId, TaskVersion taskVersion, CancellationToken token) =>
                        {
                            return Task.FromResult<Stream>(new ExceptionStream());
                        });

                    var tasks = new List<Pipelines.TaskStep>(new Pipelines.TaskStep[] { pingTask });

                    //Act
                    Exception actualException = null;
                    try
                    {
                        await _taskManager.DownloadAsync(_ec.Object, tasks);
                    }
                    catch (Exception ex)
                    {
                        actualException = ex;
                    }

                    //Assert
                    //verify task completed in less than 2sec and it is in failed state state
                    Assert.Equal("NotImplementedException", actualException.GetType().Name);

                    //assert download was invoked 3 times, because we retry on task download
                    _taskServer
                        .Verify(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

                    //see if the task.json was not downloaded
                    Assert.Equal(
                        0,
                        Directory.GetFiles(_hc.GetDirectory(WellKnownDirectory.Tasks), "*", SearchOption.AllDirectories).Length);
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void DeserializesPlatformSupportedHandlersOnly()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    // Prepare the content.
                    const string Content = @"
{
    ""execution"": {
        ""Node"": { },
        ""Process"": { },
    }
}";
                    // Write the task.json to disk.
                    Pipelines.TaskStep instance;
                    string directory;
                    CreateTask(jsonContent: Content, instance: out instance, directory: out directory);

                    // Act.
                    Definition definition = _taskManager.Load(instance);

                    // Assert.
                    Assert.NotNull(definition);
                    Assert.NotNull(definition.Data);
                    Assert.NotNull(definition.Data.Execution);
                    Assert.NotNull(definition.Data.Execution.Node);
                    if (TestUtil.IsWindows())
                    {
                        Assert.NotNull(definition.Data.Execution.Process);
                    }
                    else
                    {
                        Assert.Null(definition.Data.Execution.Process);
                    }
                }
            }
            finally
            {
                Teardown();
            }
        }

        //Test the normal flow, which downloads a few tasks and skips disabled, duplicate and cached tasks.
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public async void DownloadsTasks()
        {
            try
            {
                //Arrange
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    var bingGuid = Guid.NewGuid();
                    string bingTaskName = "Bing";
                    string bingVersion = "1.21.2";
                    var tasks = new List<Pipelines.TaskStep>
                    {
                        new Pipelines.TaskStep()
                        {
                            Enabled = true,
                            Reference = new Pipelines.TaskStepDefinitionReference()
                            {
                                Name = bingTaskName,
                                Version = bingVersion,
                                Id = bingGuid
                            }
                        },
                        new Pipelines.TaskStep()
                        {
                            Enabled = true,
                            Reference = new Pipelines.TaskStepDefinitionReference()
                            {
                                Name = bingTaskName,
                                Version = bingVersion,
                                Id = bingGuid
                            }
                        }
                    };

                    using (var stream = GetZipStream())
                    {
                        _taskServer
                            .Setup(x => x.GetTaskContentZipAsync(
                                bingGuid,
                                It.Is<TaskVersion>(y => string.Equals(y.ToString(), bingVersion, StringComparison.Ordinal)),
                                It.IsAny<CancellationToken>()))
                            .Returns(Task.FromResult<Stream>(stream));

                        //Act
                        //first invocation will download and unzip the task from mocked IJobServer
                        await _taskManager.DownloadAsync(_ec.Object, tasks);
                        //second and third invocations should find the task in the cache and only unzip
                        await _taskManager.DownloadAsync(_ec.Object, tasks);
                        await _taskManager.DownloadAsync(_ec.Object, tasks);

                        //Assert
                        //see if the task.json was downloaded
                        string destDirectory = Path.Combine(
                            _hc.GetDirectory(WellKnownDirectory.Tasks),
                            $"{bingTaskName}_{bingGuid}",
                            bingVersion);
                        Assert.True(File.Exists(Path.Combine(destDirectory, Constants.Path.TaskJsonFile)));
                        //assert download has happened only once, because disabled, duplicate and cached tasks are not downloaded
                        _taskServer
                            .Verify(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()), Times.Once());
                    }
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void PreservesTaskZipTaskWhenInSignatureVerification()
        {
            PreservesTaskZipTask(signatureVerification: true);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void PreservesTaskZipTaskWhenAlwaysExtractTask()
        {
            PreservesTaskZipTask(alwaysExtractTask: true);
        }

        // TODO: Add test for Extract
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ExtractsAnAlreadyDownloadedZipToTheCorrectLocationWhenInSignatureVerification()
        {
            ExtractsAnAlreadyDownloadedZipToTheCorrectLocation(signatureVerification: true);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ExtractsAnAlreadyDownloadedZipToTheCorrectLocationWhenExtractTask()
        {
            ExtractsAnAlreadyDownloadedZipToTheCorrectLocation(alwaysExtractTask: true);
        }


        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void DoesNotMatchPlatform()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    HandlerData data = new NodeHandlerData() { Platforms = new string[] { "nosuch" } };
                    // Act/Assert.
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.Windows));
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.Linux));
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.FreeBSD));
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.OSX));
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void LoadsDefinition()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    // Prepare the task.json content.
                    const string Content = @"
{
    ""inputs"": [
        {
            ""extraInputKey"": ""Extra input value"",
            ""name"": ""someFilePathInput"",
            ""type"": ""filePath"",
            ""label"": ""Some file path input label"",
            ""defaultValue"": ""Some default file path"",
            ""required"": true,
            ""helpMarkDown"": ""Some file path input markdown""
        },
        {
            ""name"": ""someStringInput"",
            ""type"": ""string"",
            ""label"": ""Some string input label"",
            ""defaultValue"": ""Some default string"",
            ""helpMarkDown"": ""Some string input markdown"",
            ""required"": false,
            ""groupName"": ""advanced""
        }
    ],
    ""execution"": {
        ""Node"": {
            ""target"": ""Some Node target"",
            ""extraNodeArg"": ""Extra node arg value""
        },
        ""Node10"": {
            ""target"": ""Some Node10 target"",
            ""extraNodeArg"": ""Extra node10 arg value""
        },
        ""Process"": {
            ""target"": ""Some process target"",
            ""argumentFormat"": ""Some process argument format"",
            ""workingDirectory"": ""Some process working directory"",
            ""extraProcessArg"": ""Some extra process arg"",
            ""platforms"": [
                ""windows""
            ]
        },
        ""NoSuchHandler"": {
            ""target"": ""no such target""
        }
    },
    ""restrictions"": {
        ""commands"": {
            ""mode"": ""restricted""
        },
        ""settableVariables"": {
            ""allowed"": [
                ""okVar"",
                ""otherVar""
            ]
        }
    },
    ""someExtraSection"": {
        ""someExtraKey"": ""Some extra value""
    }
}";
                    // Write the task.json to disk.
                    Pipelines.TaskStep instance;
                    string directory;
                    CreateTask(jsonContent: Content, instance: out instance, directory: out directory);

                    // Act.
                    Definition definition = _taskManager.Load(instance);

                    // Assert.
                    Assert.NotNull(definition);
                    Assert.Equal(directory, definition.Directory);
                    Assert.NotNull(definition.Data);
                    Assert.NotNull(definition.Data.Inputs); // inputs
                    Assert.Equal(2, definition.Data.Inputs.Length);
                    Assert.Equal("someFilePathInput", definition.Data.Inputs[0].Name);
                    Assert.Equal("Some default file path", definition.Data.Inputs[0].DefaultValue);
                    Assert.Equal("someStringInput", definition.Data.Inputs[1].Name);
                    Assert.Equal("Some default string", definition.Data.Inputs[1].DefaultValue);
                    Assert.NotNull(definition.Data.Execution); // execution

                    if (TestUtil.IsWindows())
                    {
                        // Process handler should only be deserialized on Windows.
                        Assert.Equal(3, definition.Data.Execution.All.Count);
                    }
                    else
                    {
                        // Only the Node handlers should be deserialized on non-Windows.
                        Assert.Equal(2, definition.Data.Execution.All.Count);
                    }

                    // Node handler should always be deserialized.
                    Assert.NotNull(definition.Data.Execution.Node); // execution.Node
                    Assert.Equal(definition.Data.Execution.Node, definition.Data.Execution.All[0]);
                    Assert.Equal("Some Node target", definition.Data.Execution.Node.Target);

                    // Node10 handler should always be deserialized.
                    Assert.NotNull(definition.Data.Execution.Node10); // execution.Node10
                    Assert.Equal(definition.Data.Execution.Node10, definition.Data.Execution.All[1]);
                    Assert.Equal("Some Node10 target", definition.Data.Execution.Node10.Target);

                    if (TestUtil.IsWindows())
                    {
                        // Process handler should only be deserialized on Windows.
                        Assert.NotNull(definition.Data.Execution.Process); // execution.Process
                        Assert.Equal(definition.Data.Execution.Process, definition.Data.Execution.All[2]);
                        Assert.Equal("Some process argument format", definition.Data.Execution.Process.ArgumentFormat);
                        Assert.NotNull(definition.Data.Execution.Process.Platforms);
                        Assert.Equal(1, definition.Data.Execution.Process.Platforms.Length);
                        Assert.Equal("windows", definition.Data.Execution.Process.Platforms[0]);
                        Assert.Equal("Some process target", definition.Data.Execution.Process.Target);
                        Assert.Equal("Some process working directory", definition.Data.Execution.Process.WorkingDirectory);
                    }

                    // restrictions
                    Assert.NotNull(definition.Data.Restrictions);
                    Assert.Equal(TaskCommandMode.Restricted, definition.Data.Restrictions.Commands.Mode);
                    Assert.Equal(2, definition.Data.Restrictions.SettableVariables.Allowed.Count);
                    Assert.Equal("okVar", definition.Data.Restrictions.SettableVariables.Allowed[0]);
                    Assert.Equal("otherVar", definition.Data.Restrictions.SettableVariables.Allowed[1]);
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        [Trait("SkipOn", "darwin")]
        [Trait("SkipOn", "linux")]
        [Trait("SkipOn", "freebsd")]
        public void MatchesPlatform()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    HandlerData data = new NodeHandlerData() { Platforms = new[] { "WiNdOwS" } };
                    // Act/Assert.
                    Assert.True(data.PreferredOnPlatform(PlatformUtil.OS.Windows));
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.Linux));
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.FreeBSD));
                    Assert.False(data.PreferredOnPlatform(PlatformUtil.OS.OSX));
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ReplacesMacros()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    const string Directory = "Some directory";
                    Definition definition = new Definition() { Directory = Directory };
                    NodeHandlerData node = new NodeHandlerData()
                    {
                        Target = @"$(CuRrEnTdIrEcToRy)\Some node target",
                        WorkingDirectory = @"$(CuRrEnTdIrEcToRy)\Some node working directory",
                    };
                    ProcessHandlerData process = new ProcessHandlerData()
                    {
                        ArgumentFormat = @"$(CuRrEnTdIrEcToRy)\Some process argument format",
                        Target = @"$(CuRrEnTdIrEcToRy)\Some process target",
                        WorkingDirectory = @"$(CuRrEnTdIrEcToRy)\Some process working directory",
                    };

                    // Act.
                    node.ReplaceMacros(_hc, definition);
                    process.ReplaceMacros(_hc, definition);

                    // Assert.
                    Assert.Equal($@"{Directory}\Some node target", node.Target);
                    Assert.Equal($@"{Directory}\Some node working directory", node.WorkingDirectory);
                    Assert.Equal($@"{Directory}\Some process argument format", process.ArgumentFormat);
                    Assert.Equal($@"{Directory}\Some process target", process.Target);
                    Assert.Equal($@"{Directory}\Some process working directory", process.WorkingDirectory);
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ReplacesMacrosAndPreventsInfiniteRecursion()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    string directory = "$(currentdirectory)$(currentdirectory)";
                    Definition definition = new Definition() { Directory = directory };
                    NodeHandlerData node = new NodeHandlerData()
                    {
                        Target = @"$(currentDirectory)\Some node target",
                    };

                    // Act.
                    node.ReplaceMacros(_hc, definition);

                    // Assert.
                    Assert.Equal($@"{directory}\Some node target", node.Target);
                }
            }
            finally
            {
                Teardown();
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ReplacesMultipleMacroInstances()
        {
            try
            {
                // Arrange.
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource))
                {
                    const string Directory = "Some directory";
                    Definition definition = new Definition() { Directory = Directory };
                    NodeHandlerData node = new NodeHandlerData()
                    {
                        Target = @"$(CuRrEnTdIrEcToRy)$(CuRrEnTdIrEcToRy)\Some node target",
                    };

                    // Act.
                    node.ReplaceMacros(_hc, definition);

                    // Assert.
                    Assert.Equal($@"{Directory}{Directory}\Some node target", node.Target);
                }
            }
            finally
            {
                Teardown();
            }
        }

        private void CreateTask(string jsonContent, out Pipelines.TaskStep instance, out string directory)
        {
            const string TaskName = "SomeTask";
            const string TaskVersion = "1.2.3";
            Guid taskGuid = Guid.NewGuid();
            directory = Path.Combine(_workFolder, Constants.Path.TasksDirectory, $"{TaskName}_{taskGuid}", TaskVersion);
            string file = Path.Combine(directory, Constants.Path.TaskJsonFile);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, jsonContent);
            instance = new Pipelines.TaskStep()
            {
                Reference = new Pipelines.TaskStepDefinitionReference()
                {
                    Id = taskGuid,
                    Name = TaskName,
                    Version = TaskVersion,
                }
            };
        }

        private Stream GetZipStream()
        {
            // Locate the test data folder containing the task.json.
            string sourceFolder = Path.Combine(TestUtil.GetTestDataPath(), nameof(TaskManagerL0));
            Assert.True(Directory.Exists(sourceFolder), $"Directory does not exist: {sourceFolder}");
            Assert.True(File.Exists(Path.Combine(sourceFolder, Constants.Path.TaskJsonFile)));

            // Create the zip file under the work folder so it gets cleaned up.
            string zipFile = Path.Combine(
                _workFolder,
                $"{Guid.NewGuid()}.zip");
            Directory.CreateDirectory(_workFolder);
            ZipFile.CreateFromDirectory(sourceFolder, zipFile, CompressionLevel.Fastest, includeBaseDirectory: false);
            return new FileStream(zipFile, FileMode.Open);
        }

        private TestHostContext Setup(
            CancellationTokenSource _ecTokenSource,
            [CallerMemberName] string name = "",
            bool signatureVerificationEnabled = false,
            bool alwaysExtractTaskEnabled = false)
        {

            // Mocks.
            _jobServer = new Mock<IJobServer>();
            _taskServer = new Mock<ITaskServer>();
            _ec = new Mock<IExecutionContext>();
            _ec.Setup(x => x.CancellationToken).Returns(_ecTokenSource.Token);

            // Test host context.
            var _hc = new TestHostContext(this, name);

            // Random work folder.
            _workFolder = _hc.GetDirectory(WellKnownDirectory.Work);

            _hc.SetSingleton<IJobServer>(_jobServer.Object);
            _hc.SetSingleton<ITaskServer>(_taskServer.Object);

            String fingerprint = String.Empty;
            if (signatureVerificationEnabled)
            {
                fingerprint = "FAKEFINGERPRINT";
            }

            _configurationStore = new Mock<IConfigurationStore>();
            _configurationStore
                .Setup(x => x.GetSettings())
                .Returns(
                    new AgentSettings
                    {
                        Fingerprint = fingerprint,
                        WorkFolder = _workFolder,
                        AlwaysExtractTask = alwaysExtractTaskEnabled
                    });

            _hc.SetSingleton<IConfigurationStore>(_configurationStore.Object);

            // Instance to test.
            _taskManager = new TaskManager();
            _taskManager.Initialize(_hc);

            Environment.SetEnvironmentVariable("VSTS_TASK_DOWNLOAD_NO_BACKOFF", "1");
            return _hc;
        }

        private void Teardown()
        {
            if (!string.IsNullOrEmpty(_workFolder) && Directory.Exists(_workFolder))
            {
                Directory.Delete(_workFolder, recursive: true);
            }
        }

        private async void PreservesTaskZipTask(bool signatureVerification = false, bool alwaysExtractTask = false)
        {
            try
            {
                //Arrange
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource, signatureVerificationEnabled: signatureVerification, alwaysExtractTaskEnabled: alwaysExtractTask))
                {
                    var bingGuid = Guid.NewGuid();
                    string bingTaskName = "Bing";
                    string bingVersion = "1.21.2";
                    var tasks = new List<Pipelines.TaskStep>
                    {
                        new Pipelines.TaskStep()
                        {
                            Enabled = true,
                            Reference = new Pipelines.TaskStepDefinitionReference()
                            {
                                Name = bingTaskName,
                                Version = bingVersion,
                                Id = bingGuid
                            }
                        },
                        new Pipelines.TaskStep()
                        {
                            Enabled = true,
                            Reference = new Pipelines.TaskStepDefinitionReference()
                            {
                                Name = bingTaskName,
                                Version = bingVersion,
                                Id = bingGuid
                            }
                        }
                    };
                    using (var stream = GetZipStream())
                    {
                        _taskServer
                            .Setup(x => x.GetTaskContentZipAsync(
                                bingGuid,
                                It.Is<TaskVersion>(y => string.Equals(y.ToString(), bingVersion, StringComparison.Ordinal)),
                                It.IsAny<CancellationToken>()))
                            .Returns(Task.FromResult<Stream>(stream));

                        //Act
                        //first invocation will download and unzip the task from mocked IJobServer
                        await _taskManager.DownloadAsync(_ec.Object, tasks);

                        string destDirectory = Path.Combine(
                            _hc.GetDirectory(WellKnownDirectory.Tasks),
                            $"{bingTaskName}_{bingGuid}",
                            bingVersion);

                        //see if the task.json was downloaded
                        string zipDestDirectory = Path.Combine(_hc.GetDirectory(WellKnownDirectory.TaskZips), $"{bingTaskName}_{bingGuid}_{bingVersion}.zip");

                        // task.json should exist since we need it for JobExtension.InitializeJob
                        Assert.True(File.Exists(Path.Combine(destDirectory, Constants.Path.TaskJsonFile)));

                        // Write a test file
                        string testFile = Path.Combine(destDirectory, "test.txt");
                        using (File.Create(testFile)) { }
                        Assert.True(File.Exists(testFile));

                        //second and third invocations should find the task in the cache and do nothing
                        await _taskManager.DownloadAsync(_ec.Object, tasks);

                        // Test file should no longer exist due to a fresh unzip
                        Assert.False(File.Exists(testFile));

                        await _taskManager.DownloadAsync(_ec.Object, tasks);

                        // the zip for the task should exist on disk
                        Assert.True(File.Exists(zipDestDirectory));
                        //assert download has happened only once, because disabled, duplicate and cached tasks are not downloaded
                        _taskServer
                            .Verify(x => x.GetTaskContentZipAsync(It.IsAny<Guid>(), It.IsAny<TaskVersion>(), It.IsAny<CancellationToken>()), Times.Once());
                    }
                }
            }
            finally
            {
                Teardown();
            }
        }

        private void ExtractsAnAlreadyDownloadedZipToTheCorrectLocation(bool signatureVerification = true, bool alwaysExtractTask = true)
        {
            try
            {
                // Arrange
                using (var tokenSource = new CancellationTokenSource())
                using (var _hc = Setup(tokenSource, signatureVerificationEnabled: signatureVerification, alwaysExtractTaskEnabled: alwaysExtractTask))
                {
                    var bingGuid = Guid.NewGuid();
                    string bingTaskName = "Bing";
                    string bingVersion = "1.21.2";
                    var taskStep = new Pipelines.TaskStep
                    {
                        Name = bingTaskName,
                        Reference = new Pipelines.TaskStepDefinitionReference
                        {
                            Id = bingGuid,
                            Name = bingTaskName,
                            Version = bingVersion
                        }
                    };
                    string zipDestDirectory = Path.Combine(_hc.GetDirectory(WellKnownDirectory.TaskZips), $"{bingTaskName}_{bingGuid}_{bingVersion}.zip");
                    Directory.CreateDirectory(_hc.GetDirectory(WellKnownDirectory.TaskZips));
                    // write stream to file
                    using (Stream zipStream = GetZipStream())
                    using (var fileStream = new FileStream(zipDestDirectory, FileMode.Create, FileAccess.Write))
                    {
                        zipStream.CopyTo(fileStream);
                    }

                    // Act
                    _taskManager.Extract(_ec.Object, taskStep);

                    // Assert
                    string destDirectory = Path.Combine(
                        _hc.GetDirectory(WellKnownDirectory.Tasks),
                        $"{bingTaskName}_{bingGuid}",
                        bingVersion);
                    Assert.True(File.Exists(Path.Combine(destDirectory, Constants.Path.TaskJsonFile)));
                }
            }
            finally
            {
                Teardown();
            }
        }

        private class ExceptionStream : Stream
        {
            public override bool CanRead => throw new NotImplementedException();

            public override bool CanSeek => throw new NotImplementedException();

            public override bool CanWrite => throw new NotImplementedException();

            public override long Length => throw new NotImplementedException();

            public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public override void Flush()
            {
                throw new NotImplementedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }
        }
    }
}

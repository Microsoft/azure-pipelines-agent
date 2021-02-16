// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.TeamFoundation.DistributedTask.WebApi;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests.L1.Worker
{
    [Collection("Worker L1 Tests")]
    public class CoreL1Tests : L1TestBase
    {
        [Fact]
        [Trait("Level", "L1")]
        [Trait("Category", "Worker")]
        public async Task Test_Base()
        {
            try
            {
                // Arrange
                SetupL1();
                var message = LoadTemplateMessage();

                // Act
                var results = await RunWorker(message);

                // Assert
                AssertJobCompleted();
                Assert.Equal(TaskResult.Succeeded, results.Result);

                var steps = GetSteps();
                var expectedSteps = new[] { "Initialize job", "Checkout MyFirstProject@master to s", "CmdLine", "Post-job: Checkout MyFirstProject@master to s", "Finalize Job" };
                Assert.Equal(5, steps.Count()); // Init, Checkout, CmdLine, Post, Finalize
                for (var idx = 0; idx < steps.Count; idx++)
                {
                    Assert.Equal(expectedSteps[idx], steps[idx].Name);
                }
            }
            finally
            {
                TearDown();
            }
        }

        [Theory]
        [Trait("Level", "L1")]
        [Trait("SkipOn", "freebsd")]
        [Trait("Category", "Worker")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Test_Base_Node10(bool writeToBlobstorageService)
        {
            try
            {
                // Arrange
                SetupL1();
                var message = LoadTemplateMessage();

                message.Steps.Clear();
                // Add variable setting tasks
                message.Steps.Add(CreateNode10ScriptTask("echo Hey!"));
                message.Variables.Add("agent.LogToBlobstorageService", writeToBlobstorageService.ToString());

                // Act
                var results = await RunWorker(message);

                // Assert
                AssertJobCompleted();
                Assert.Equal(TaskResult.Succeeded, results.Result);

                var steps = GetSteps();
                var expectedSteps = new[] { "Initialize job", "CmdLine", "Finalize Job" };
                Assert.Equal(3, steps.Count()); // Init, CmdLine, Finalize
                for (var idx = 0; idx < steps.Count; idx++)
                {
                    Assert.Equal(expectedSteps[idx], steps[idx].Name);
                }

                // CmdLineV2 runs on powershell on windows
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Assert we used Node 10 from debug logs
                    System.Diagnostics.Debugger.Launch();
                    var log = GetTimelineLogLines(steps[1]);
                    Assert.Equal(1, log.Where(x => x.Contains("Using node path:") && x.Contains("node10")).Count());
                }
            }
            finally
            {
                TearDown();
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [Trait("Level", "L1")]
        // TODO - this test currently doesn't work on Linux/Mac because the node task-lib trims the values it reads.
        // Remove these SkipOn traits once the task-lib is updated.
        [Trait("SkipOn", "darwin")]
        [Trait("SkipOn", "linux")]
        [Trait("SkipOn", "freebsd")]
        [Trait("Category", "Worker")]
        public async Task Input_HandlesTrailingSpace(bool disableInputTrimming, bool writeToBlobstorageService)
        {
            try
            {
                // Arrange
                SetupL1();
                var message = LoadTemplateMessage();
                // Remove all tasks
                message.Steps.Clear();
                // Add variable setting tasks
                var scriptTask = CreateScriptTask("echo   ");
                Environment.SetEnvironmentVariable("DISABLE_INPUT_TRIMMING", disableInputTrimming.ToString());
                message.Steps.Add(scriptTask);
                message.Variables.Add("agent.LogToBlobstorageService", writeToBlobstorageService.ToString());

                // Act
                var results = await RunWorker(message);

                // Assert
                AssertJobCompleted();

                var steps = GetSteps();
                Assert.Equal(3, steps.Count()); // Init, CmdLine, CmdLine, Finalize
                var outputStep = steps[1];
                var log = GetTimelineLogLines(outputStep);

                if (disableInputTrimming)
                {
                    Assert.True(log.Where(x => x.Contains("echo   ")).Count() > 0, String.Join("\n", log) + " should contain \"echo   \"");
                }
                else
                {
                    Assert.False(log.Where(x => x.Contains("echo   ")).Count() > 0, String.Join("\n", log) + " should not contain \"echo   \"");
                }
            }
            finally
            {
                TearDown();
            }
        }
    }
}

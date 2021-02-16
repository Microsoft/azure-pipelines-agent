// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.TeamFoundation.Framework.Common;

namespace Microsoft.VisualStudio.Services.Agent.Tests
{
    public sealed class ProcessInvokerL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public async Task SuccessExitsWithCodeZero()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                Int32 exitCode = -1;
                using (var processInvoker = new ProcessInvokerWrapper())
                {
                    processInvoker.Initialize(hc);
                    exitCode = (TestUtil.IsWindows())
                        ? await processInvoker.ExecuteAsync("", "cmd.exe", "/c \"dir >nul\"", null, CancellationToken.None)
                        : await processInvoker.ExecuteAsync("", "bash", "-c echo .", null, CancellationToken.None);

                    trace.Info("Exit Code: {0}", exitCode);
                    Assert.Equal(0, exitCode);
                }
            }
        }

        //Run a process that normally takes 20sec to finish and cancel it.
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        [Trait("SkipOn", "windows")]
        [Trait("SkipOn", "freebsd")]
        public async Task TestCancel()
        {
            const int SecondsToRun = 20;
            using (TestHostContext hc = new TestHostContext(this))
            using (var tokenSource = new CancellationTokenSource())
            {
                Tracing trace = hc.GetTrace();
                using (var processInvoker = new ProcessInvokerWrapper())
                {
                    processInvoker.Initialize(hc);
                    Stopwatch watch = Stopwatch.StartNew();
                    Task execTask = processInvoker.ExecuteAsync("", "bash", $"-c \"sleep {SecondsToRun}s\"", null, tokenSource.Token);

                    await Task.Delay(500);
                    tokenSource.Cancel();
                    try
                    {
                        await execTask;
                    }
                    catch (OperationCanceledException)
                    {
                        trace.Info("Get expected OperationCanceledException.");
                    }

                    Assert.True(execTask.IsCompleted);
                    Assert.True(!execTask.IsFaulted);
                    Assert.True(execTask.IsCanceled);
                    watch.Stop();
                    long elapsedSeconds = watch.ElapsedMilliseconds / 1000;

                    // if cancellation fails, then execution time is more than 15 seconds
                    long expectedSeconds = (SecondsToRun * 3) / 4;

                    Assert.True(elapsedSeconds <= expectedSeconds, $"cancellation failed, because task took too long to run. {elapsedSeconds}");
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public async Task RedirectSTDINCloseStream()
        {
            using (TestHostContext hc = new TestHostContext(this))
            using (var redirectSTDIN = new InputQueue<string>())
            {
                Tracing trace = hc.GetTrace();
                Int32 exitCode = -1;
                List<string> stdout = new List<string>();
                redirectSTDIN.Enqueue("Single line of STDIN");

                using (var cancellationTokenSource = new CancellationTokenSource() )
                using (var processInvoker = new ProcessInvokerWrapper())
                {
                    processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        stdout.Add(e.Data);
                    };

                    processInvoker.Initialize(hc);
                    var proc = (TestUtil.IsWindows())
                        ? processInvoker.ExecuteAsync("", "cmd.exe", "/c more", null, false, null, false, redirectSTDIN, false, false, cancellationTokenSource.Token)
                        : processInvoker.ExecuteAsync("", "bash", "-c \"read input; echo $input; read input; echo $input; read input; echo $input;\"", null, false, null, false, redirectSTDIN, false, false, cancellationTokenSource.Token);

                    redirectSTDIN.Enqueue("More line of STDIN");
                    redirectSTDIN.Enqueue("More line of STDIN");
                    await Task.Delay(100);
                    redirectSTDIN.Enqueue("More line of STDIN");
                    redirectSTDIN.Enqueue("More line of STDIN");
                    await Task.Delay(100);
                    redirectSTDIN.Enqueue("More line of STDIN");
                    cancellationTokenSource.CancelAfter(100);

                    try
                    {
                        exitCode = await proc;
                        trace.Info("Exit Code: {0}", exitCode);
                    }
                    catch (Exception ex)
                    {
                        trace.Error(ex);
                    }

                    trace.Info("STDOUT: {0}", string.Join(Environment.NewLine, stdout));
                    Assert.False(stdout.Contains("More line of STDIN"), "STDIN should be closed after first input line.");
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public async Task RedirectSTDINKeepStreamOpen()
        {
            using (TestHostContext hc = new TestHostContext(this))
            using (var redirectSTDIN = new InputQueue<string>())
            {
                Tracing trace = hc.GetTrace();
                Int32 exitCode = -1;
                List<string> stdout = new List<string>();
                redirectSTDIN.Enqueue("Single line of STDIN");
                using (var cancellationTokenSource = new CancellationTokenSource() )
                using (var processInvoker = new ProcessInvokerWrapper())
                {
                    processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        stdout.Add(e.Data);
                    };

                    processInvoker.Initialize(hc);
                    var proc = (TestUtil.IsWindows())
                        ? processInvoker.ExecuteAsync("", "cmd.exe", "/c more", null, false, null, false, redirectSTDIN, false, true, cancellationTokenSource.Token)
                        : processInvoker.ExecuteAsync("", "bash", "-c \"read input; echo $input; read input; echo $input; read input; echo $input;\"", null, false, null, false, redirectSTDIN, false, true, cancellationTokenSource.Token);

                    redirectSTDIN.Enqueue("More line of STDIN");
                    redirectSTDIN.Enqueue("More line of STDIN");
                    await Task.Delay(100);
                    redirectSTDIN.Enqueue("More line of STDIN");
                    redirectSTDIN.Enqueue("More line of STDIN");
                    await Task.Delay(100);
                    redirectSTDIN.Enqueue("More line of STDIN");
                    cancellationTokenSource.CancelAfter(100);

                    try
                    {
                        exitCode = await proc;
                        trace.Info("Exit Code: {0}", exitCode);
                    }
                    catch (Exception ex)
                    {
                        trace.Error(ex);
                    }

                    trace.Info("STDOUT: {0}", string.Join(Environment.NewLine, stdout));
                    Assert.True(stdout.Contains("More line of STDIN"), "STDIN should keep open and accept more inputs after first input line.");
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        [Trait("SkipOn", "darwin")]
        [Trait("SkipOn", "windows")]
        public async Task OomScoreAdjIsWriten_Default()
        {
            // We are on a system that supports oom_score_adj in procfs as assumed by ProcessInvoker
            string testProcPath = $"/proc/{Process.GetCurrentProcess().Id}/oom_score_adj";
            if (File.Exists(testProcPath))
            {
                using (TestHostContext hc = new TestHostContext(this))
                using (var tokenSource = new CancellationTokenSource())
                {
                    Tracing trace = hc.GetTrace();
                    using (var processInvoker = new ProcessInvokerWrapper())
                    {
                        processInvoker.Initialize(hc);
                        int oomScoreAdj = -9999;
                        processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                        {
                            oomScoreAdj = int.Parse(e.Data);
                            tokenSource.Cancel();
                        };
                        try
                        {
                            var proc = await processInvoker.ExecuteAsync("", "bash", "-c \"cat /proc/$$/oom_score_adj\"", null, false, null, false, null, false, false,
                                                                highPriorityProcess: false,
                                                                cancellationToken: tokenSource.Token);
                            Assert.Equal(oomScoreAdj, 500);
                        }
                        catch (OperationCanceledException)
                        {
                            trace.Info("Caught expected OperationCanceledException");
                        }
                    }
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        [Trait("SkipOn", "darwin")]
        [Trait("SkipOn", "windows")]
        public async Task OomScoreAdjIsWriten_FromEnv()
        {
            // We are on a system that supports oom_score_adj in procfs as assumed by ProcessInvoker
            string testProcPath = $"/proc/{Process.GetCurrentProcess().Id}/oom_score_adj";
            if (File.Exists(testProcPath))
            {
                using (TestHostContext hc = new TestHostContext(this))
                using (var tokenSource = new CancellationTokenSource())
                {
                    Tracing trace = hc.GetTrace();
                    using (var processInvoker = new ProcessInvokerWrapper())
                    {
                        processInvoker.Initialize(hc);
                        int oomScoreAdj = -9999;
                        processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                        {
                            oomScoreAdj = int.Parse(e.Data);
                            tokenSource.Cancel();
                        };
                        try
                        {
                            var proc = await processInvoker.ExecuteAsync("", "bash", "-c \"cat /proc/$$/oom_score_adj\"",
                                                                    new Dictionary<string, string> { {"PIPELINE_JOB_OOMSCOREADJ", "1234"} },
                                                                    false, null, false, null, false, false,
                                                                    highPriorityProcess: false,
                                                                    cancellationToken: tokenSource.Token);
                            Assert.Equal(oomScoreAdj, 1234);
                        }
                        catch (OperationCanceledException)
                        {
                            trace.Info("Caught expected OperationCanceledException");
                        }
                    }
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        [Trait("SkipOn", "darwin")]
        [Trait("SkipOn", "windows")]
        public async Task OomScoreAdjIsInherited()
        {
            // We are on a system that supports oom_score_adj in procfs as assumed by ProcessInvoker
            string testProcPath = $"/proc/{Process.GetCurrentProcess().Id}/oom_score_adj";
            if (File.Exists(testProcPath))
            {
                int testProcOomScoreAdj = 123;
                File.WriteAllText(testProcPath, testProcOomScoreAdj.ToString());
                using (TestHostContext hc = new TestHostContext(this))
                using (var tokenSource = new CancellationTokenSource())
                {
                    Tracing trace = hc.GetTrace();
                    using (var processInvoker = new ProcessInvokerWrapper())
                    {
                        processInvoker.Initialize(hc);
                        int oomScoreAdj = -9999;
                        processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                        {
                            oomScoreAdj = int.Parse(e.Data);
                            tokenSource.Cancel();
                        };
                        try
                        {
                            var proc = await processInvoker.ExecuteAsync("", "bash", "-c \"cat /proc/$$/oom_score_adj\"", null, false, null, false, null, false, false,
                                                                highPriorityProcess: true,
                                                                cancellationToken: tokenSource.Token);
                            Assert.Equal(oomScoreAdj, 123);
                        }
                        catch (OperationCanceledException)
                        {
                            trace.Info("Caught expected OperationCanceledException");
                        }
                    }
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public async Task DisableWorkerCommands()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                Int32 exitCode = -1;
                List<string> stdout = new List<string>();

                using (var processInvoker = new ProcessInvokerWrapper())
                {
                    processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        stdout.Add(e.Data);
                    };
                    processInvoker.DisableWorkerCommands = true;
                    processInvoker.Initialize(hc);
                    exitCode = (TestUtil.IsWindows())
                        ? await processInvoker.ExecuteAsync("", "powershell.exe",  $@"-NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command ""Write-Host '##vso somecommand'""", null, CancellationToken.None)
                        : await processInvoker.ExecuteAsync("", "bash", "-c \"echo '##vso somecommand'\"", null, CancellationToken.None);

                    trace.Info("Exit Code: {0}", exitCode);
                    Assert.Equal(0, exitCode);

                    Assert.False(stdout.Contains("##vso somecommand"), $"##vso commands should be escaped.");
                }
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public async Task EnableWorkerCommandsByDefault()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                Int32 exitCode = -1;
                List<string> stdout = new List<string>();

                using (var processInvoker = new ProcessInvokerWrapper())
                {
                    processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
                    {
                        stdout.Add(e.Data);
                    };
                    processInvoker.Initialize(hc);
                    exitCode = (TestUtil.IsWindows())
                        ? await processInvoker.ExecuteAsync("", "powershell.exe",  $@"-NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command ""Write-Host '##vso somecommand'""", null, CancellationToken.None)
                        : await processInvoker.ExecuteAsync("", "bash", "-c \"echo '##vso somecommand'\"", null, CancellationToken.None);

                    trace.Info("Exit Code: {0}", exitCode);
                    Assert.Equal(0, exitCode);

                    Assert.True(stdout.Contains("##vso somecommand"), "##vso commands should not be escaped.");
                }
            }
        }
    }
}

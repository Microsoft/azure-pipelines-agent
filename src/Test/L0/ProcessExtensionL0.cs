// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests
{
    public sealed class ProcessExtensionL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        [Trait("SkipOn", "freebsd")]
        public async Task SuccessReadProcessEnv()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                string envName = Guid.NewGuid().ToString();
                string envValue = Guid.NewGuid().ToString();

                Process sleep = null;
                try
                {
                    // TODO: this was formerly skipped on Windows - why?
                    using (var processInvoker = new ProcessInvokerWrapper())
                    {
                        hc.EnqueueInstance<IProcessInvoker>(processInvoker);

                        // sleep 15 seconds
                        string sleepCmd = (TestUtil.IsWindows())
                            ? "powershell"
                            : "sleep";
                        string sleepArgs = (TestUtil.IsWindows())
                            ? "-Command \"Start-Sleep -s 15\""
                            : "15s";
                        var startInfo = new ProcessStartInfo(sleepCmd, sleepArgs);
                        startInfo.Environment[envName] = envValue;
                        sleep = Process.Start(startInfo);

                        var timeout = Process.GetProcessById(sleep.Id);
                        while (timeout == null)
                        {
                            await Task.Delay(1500);
                            timeout = Process.GetProcessById(sleep.Id);
                        }

                        try
                        {
                            trace.Info($"Read env from {timeout.Id}");
                            int retries = 5;
                            while (retries >= 0)
                            {
                                try
                                {
                                    var value = timeout.GetEnvironmentVariable(hc, envName);
                                    Assert.True(string.Equals(value, envValue, StringComparison.OrdinalIgnoreCase), "Expected environment '" + envValue + "' did not match actual '" + value + "'");
                                    break;
                                }
                                catch (Exception)
                                {
                                    retries--;
                                    if (retries < 0)
                                    {
                                        throw;
                                    }
                                    trace.Info($"Unable to get the environment variable, will retry. {retries} retries remaining");
                                    await Task.Delay(2000);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            trace.Error(ex);
                            Assert.True(false, "Fail to retrive process environment variable due to exception: " + ex.Message + "\n" + ex.StackTrace);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        sleep?.Kill();
                    }
                    catch {  }
                }
            }
        }
    }
}

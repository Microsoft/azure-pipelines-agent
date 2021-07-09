// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Agent.Listener.Configuration;
using Moq;
using Xunit;
using System.Security.Principal;
using Microsoft.VisualStudio.Services.Agent;
using Microsoft.VisualStudio.Services.Agent.Tests;

namespace Test.L0.Listener.Configuration
{
    [Trait("SkipOn", "darwin")]
    [Trait("SkipOn", "linux")]
    [Trait("SkipOn", "freebsd")]
    public class NativeWindowsServiceHelperL0
    {

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public void EnsureGetDefaultServiceAccountShouldReturnNetworkServiceAccount()
        {
            using (TestHostContext tc = new TestHostContext(this, "EnsureGetDefaultServiceAccountShouldReturnNetworkServiceAccount"))
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating an instance of the NativeWindowsServiceHelper class");
                var windowsServiceHelper = new NativeWindowsServiceHelper();

                trace.Info("Trying to get the Default Service Account when a BuildRelease Agent is being configured");
                var defaultServiceAccount = windowsServiceHelper.GetDefaultServiceAccount();
                Assert.True(defaultServiceAccount.ToString().Equals(@"NT AUTHORITY\NETWORK SERVICE"), "If agent is getting configured as build-release agent, default service accout should be 'NT AUTHORITY\\NETWORK SERVICE'");
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public void EnsureGetDefaultAdminServiceAccountShouldReturnLocalSystemAccount()
        {
            using (TestHostContext tc = new TestHostContext(this, "EnsureGetDefaultAdminServiceAccountShouldReturnLocalSystemAccount"))
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating an instance of the NativeWindowsServiceHelper class");
                var windowsServiceHelper = new NativeWindowsServiceHelper();

                trace.Info("Trying to get the Default Service Account when a DeploymentAgent is being configured");
                var defaultServiceAccount = windowsServiceHelper.GetDefaultAdminServiceAccount();
                Assert.True(defaultServiceAccount.ToString().Equals(@"NT AUTHORITY\SYSTEM"), "If agent is getting configured as deployment agent, default service accout should be 'NT AUTHORITY\\SYSTEM'");
            }
        }
    }
}

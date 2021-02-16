// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Listener;
using Microsoft.VisualStudio.Services.Agent.Capabilities;
using Microsoft.VisualStudio.Services.Agent.Listener.Configuration;
using Microsoft.VisualStudio.Services.WebApi;
using Moq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;
using Microsoft.VisualStudio.Services.Location;
using Microsoft.VisualStudio.Services.Common;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Listener.Configuration
{
    public sealed class ConfigurationManagerL0 : IDisposable
    {
        private Mock<IAgentServer> _agentServer;
        private Mock<ILocationServer> _locationServer;
        private Mock<ICredentialManager> _credMgr;
        private Mock<IPromptManager> _promptManager;
        private Mock<IConfigurationStore> _store;
        private Mock<IExtensionManager> _extnMgr;
        private Mock<IDeploymentGroupServer> _machineGroupServer;
        private Mock<IEnvironmentsServer> _environmentsServer;
        private Mock<IVstsAgentWebProxy> _vstsAgentWebProxy;
        private Mock<IAgentCertificateManager> _cert;

        private Mock<IWindowsServiceControlManager> _windowsServiceControlManager;
        private Mock<ILinuxServiceControlManager> _linuxServiceControlManager;
        private Mock<IFreeBSDServiceControlManager> _freebsdServiceControlManager;
        private Mock<IMacOSServiceControlManager> _macServiceControlManager;

        private Mock<IRSAKeyManager> _rsaKeyManager;
        private ICapabilitiesManager _capabilitiesManager;
        private DeploymentGroupAgentConfigProvider _deploymentGroupAgentConfigProvider;
        private string _expectedToken = "expectedToken";
        private string _expectedServerUrl = "https://localhost";
        private string _expectedVSTSServerUrl = "https://L0ConfigTest.visualstudio.com";
        private string _expectedAgentName = "expectedAgentName";
        private string _expectedPoolName = "poolName";
        private string _expectedCollectionName = "testCollectionName";
        private string _expectedProjectName = "testProjectName";
        private string _expectedProjectId = "edf3f94e-d251-49df-bfce-602d6c967409";
        private string _expectedMachineGroupName = "testMachineGroupName";
        private string _expectedAuthType = "pat";
        private string _expectedWorkFolder = "_work";
        private int _expectedPoolId = 1;
        private int _expectedDeploymentMachineId = 81;
        private int _expectedEnvironmentVMResourceId = 71;
        private RSACryptoServiceProvider rsa = null;
        private AgentSettings _configMgrAgentSettings = new AgentSettings();

        public ConfigurationManagerL0()
        {
            _agentServer = new Mock<IAgentServer>();
            _locationServer = new Mock<ILocationServer>();
            _credMgr = new Mock<ICredentialManager>();
            _promptManager = new Mock<IPromptManager>();
            _store = new Mock<IConfigurationStore>();
            _extnMgr = new Mock<IExtensionManager>();
            _rsaKeyManager = new Mock<IRSAKeyManager>();
            _machineGroupServer = new Mock<IDeploymentGroupServer>();
            _environmentsServer = new Mock<IEnvironmentsServer>();
            _vstsAgentWebProxy = new Mock<IVstsAgentWebProxy>();
            _cert = new Mock<IAgentCertificateManager>();

            _windowsServiceControlManager = new Mock<IWindowsServiceControlManager>();
            _linuxServiceControlManager = new Mock<ILinuxServiceControlManager>();
            _freebsdServiceControlManager = new Mock<IFreeBSDServiceControlManager>();
            _macServiceControlManager = new Mock<IMacOSServiceControlManager>();
            _capabilitiesManager = new CapabilitiesManager();

            var expectedAgent = new TaskAgent(_expectedAgentName) { Id = 1 };
            var expectedDeploymentMachine = new DeploymentMachine() { Agent = expectedAgent, Id = _expectedDeploymentMachineId };
            expectedAgent.Authorization = new TaskAgentAuthorization
            {
                ClientId = Guid.NewGuid(),
                AuthorizationUrl = new Uri("http://localhost:8080/tfs"),
            };

            var connectionData = new ConnectionData()
            {
                InstanceId = Guid.NewGuid(),
                DeploymentType = DeploymentFlags.Hosted,
                DeploymentId = Guid.NewGuid()
            };
            _agentServer.Setup(x => x.ConnectAsync(It.IsAny<Uri>(), It.IsAny<VssCredentials>())).Returns(Task.FromResult<object>(null));
            _locationServer.Setup(x => x.ConnectAsync(It.IsAny<VssConnection>())).Returns(Task.FromResult<object>(null));
            _locationServer.Setup(x => x.GetConnectionDataAsync()).Returns(Task.FromResult<ConnectionData>(connectionData));
            _machineGroupServer.Setup(x => x.ConnectAsync(It.IsAny<VssConnection>())).Returns(Task.FromResult<object>(null));
            _machineGroupServer.Setup(x => x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<List<DeploymentMachine>>()));
            _machineGroupServer.Setup(x => x.AddDeploymentTargetAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DeploymentMachine>())).Returns(Task.FromResult(expectedDeploymentMachine));
            _machineGroupServer.Setup(x => x.ReplaceDeploymentTargetAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DeploymentMachine>())).Returns(Task.FromResult(expectedDeploymentMachine));
            _machineGroupServer.Setup(x => x.GetDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>())).Returns(Task.FromResult(new List<DeploymentMachine>() { }));
            _machineGroupServer.Setup(x => x.DeleteDeploymentTargetAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns(Task.FromResult<object>(null));

            _store.Setup(x => x.IsConfigured()).Returns(false);
            _store.Setup(x => x.HasCredentials()).Returns(false);
            _store.Setup(x => x.GetSettings()).Returns(() => _configMgrAgentSettings);

            _store.Setup(x => x.SaveSettings(It.IsAny<AgentSettings>()))
                .Callback((AgentSettings settings) =>
                {
                    _configMgrAgentSettings = settings;
                });

            _credMgr.Setup(x => x.GetCredentialProvider(It.IsAny<string>())).Returns(new TestAgentCredential());

            _linuxServiceControlManager.Setup(x => x.GenerateScripts(It.IsAny<AgentSettings>()));
            _freebsdServiceControlManager.Setup(x => x.GenerateScripts(It.IsAny<AgentSettings>()));
            _macServiceControlManager.Setup(x => x.GenerateScripts(It.IsAny<AgentSettings>()));

            var expectedPools = new List<TaskAgentPool>() { new TaskAgentPool(_expectedPoolName) { Id = _expectedPoolId } };
            _agentServer.Setup(x => x.GetAgentPoolsAsync(It.IsAny<string>(), It.IsAny<TaskAgentPoolType>())).Returns(Task.FromResult(expectedPools));

            var expectedAgents = new List<TaskAgent>();
            _agentServer.Setup(x => x.GetAgentsAsync(It.IsAny<int>(), It.IsAny<string>())).Returns(Task.FromResult(expectedAgents));

            _agentServer.Setup(x => x.AddAgentAsync(It.IsAny<int>(), It.IsAny<TaskAgent>())).Returns(Task.FromResult(expectedAgent));
            _agentServer.Setup(x => x.UpdateAgentAsync(It.IsAny<int>(), It.IsAny<TaskAgent>())).Returns(Task.FromResult(expectedAgent));

            rsa = new RSACryptoServiceProvider(2048);

            _rsaKeyManager.Setup(x => x.CreateKey()).Returns(rsa);
        }

        private TestHostContext CreateTestContext([CallerMemberName] String testName = "")
        {
            TestHostContext tc = new TestHostContext(this, testName);
            tc.SetSingleton<ICredentialManager>(_credMgr.Object);
            tc.SetSingleton<IPromptManager>(_promptManager.Object);
            tc.SetSingleton<IConfigurationStore>(_store.Object);
            tc.SetSingleton<IExtensionManager>(_extnMgr.Object);
            tc.SetSingleton<IAgentServer>(_agentServer.Object);
            tc.SetSingleton<ILocationServer>(_locationServer.Object);
            tc.SetSingleton<IDeploymentGroupServer>(_machineGroupServer.Object);
            tc.SetSingleton<IEnvironmentsServer>(_environmentsServer.Object);
            tc.SetSingleton<ICapabilitiesManager>(_capabilitiesManager);
            tc.SetSingleton<IVstsAgentWebProxy>(_vstsAgentWebProxy.Object);
            tc.SetSingleton<IAgentCertificateManager>(_cert.Object);

            tc.SetSingleton<IWindowsServiceControlManager>(_windowsServiceControlManager.Object);
            tc.SetSingleton<ILinuxServiceControlManager>(_linuxServiceControlManager.Object);
            tc.SetSingleton<IFreeBSDServiceControlManager>(_freebsdServiceControlManager.Object);
            tc.SetSingleton<IMacOSServiceControlManager>(_macServiceControlManager.Object);

            tc.SetSingleton<IRSAKeyManager>(_rsaKeyManager.Object);

            return tc;
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public async Task CanEnsureConfigure()
        {
            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating config manager");
                IConfigurationManager configManager = new ConfigurationManager();
                configManager.Initialize(tc);

                trace.Info("Preparing command line arguments");
                var command = new CommandSettings(
                    tc,
                    new[]
                    {
                       "configure",
                       "--acceptteeeula",
                       "--url", _expectedServerUrl,
                       "--agent", _expectedAgentName,
                       "--pool", _expectedPoolName,
                       "--work", _expectedWorkFolder,
                       "--auth", _expectedAuthType,
                       "--token", _expectedToken
                    });
                trace.Info("Constructed.");
                _store.Setup(x => x.IsConfigured()).Returns(false);
                _configMgrAgentSettings = null;

                _extnMgr.Setup(x => x.GetExtensions<IConfigurationProvider>()).Returns(GetConfigurationProviderList(tc));

                trace.Info("Ensuring all the required parameters are available in the command line parameter");
                await configManager.ConfigureAsync(command);

                _store.Setup(x => x.IsConfigured()).Returns(true);

                trace.Info("Configured, verifying all the parameter value");
                var s = configManager.LoadSettings();
                Assert.NotNull(s);
                Assert.True(s.ServerUrl.Equals(_expectedServerUrl));
                Assert.True(s.AgentName.Equals(_expectedAgentName));
                Assert.True(s.PoolId.Equals(_expectedPoolId));
                Assert.True(s.WorkFolder.Equals(_expectedWorkFolder));

                // validate GetAgentPoolsAsync gets called once with automation pool type
                _agentServer.Verify(x => x.GetAgentPoolsAsync(It.IsAny<string>(), It.Is<TaskAgentPoolType>(p => p == TaskAgentPoolType.Automation)), Times.Once);

                // validate GetAgentPoolsAsync not called with deployment pool type
                _agentServer.Verify(x => x.GetAgentPoolsAsync(It.IsAny<string>(), It.Is<TaskAgentPoolType>(p => p == TaskAgentPoolType.Deployment)), Times.Never);

                // For build and release agent / deployment pool, tags logic should not get trigger;
                _machineGroupServer.Verify(x =>
                     x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<List<DeploymentMachine>>()), Times.Never);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public async Task CanEnsureConfigureForDeploymentPool()
        {
            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating config manager");
                IConfigurationManager configManager = new ConfigurationManager();
                configManager.Initialize(tc);

                trace.Info("Preparing command line arguments");
                var command = new CommandSettings(
                    tc,
                    new[]
                    {
                       "configure",
                       "--acceptteeeula",
                       "--url", _expectedServerUrl,
                       "--agent", _expectedAgentName,
                       "--deploymentpoolname", _expectedPoolName,
                       "--work", _expectedWorkFolder,
                       "--auth", _expectedAuthType,
                       "--token", _expectedToken,
                       "--deploymentpool"
                    });
                trace.Info("Constructed.");
                _store.Setup(x => x.IsConfigured()).Returns(false);
                _configMgrAgentSettings = null;

                _extnMgr.Setup(x => x.GetExtensions<IConfigurationProvider>()).Returns(GetConfigurationProviderList(tc));

                trace.Info("Ensuring all the required parameters are available in the command line parameter");
                await configManager.ConfigureAsync(command);

                _store.Setup(x => x.IsConfigured()).Returns(true);

                trace.Info("Configured, verifying all the parameter value");
                var s = configManager.LoadSettings();
                Assert.NotNull(s);
                Assert.True(s.ServerUrl.Equals(_expectedServerUrl));
                Assert.True(s.AgentName.Equals(_expectedAgentName));
                Assert.True(s.PoolId.Equals(_expectedPoolId));
                Assert.True(s.WorkFolder.Equals(_expectedWorkFolder));

                // validate GetAgentPoolsAsync gets called once with deployment pool type
                _agentServer.Verify(x => x.GetAgentPoolsAsync(It.IsAny<string>(), It.Is<TaskAgentPoolType>(p => p == TaskAgentPoolType.Deployment)), Times.Once);

                // validate GetAgentPoolsAsync not called with Automation pool type
                _agentServer.Verify(x => x.GetAgentPoolsAsync(It.IsAny<string>(), It.Is<TaskAgentPoolType>(p => p == TaskAgentPoolType.Automation)), Times.Never);

                // For build and release agent / deployment pool, tags logic should not get trigger;
                _machineGroupServer.Verify(x =>
                     x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<List<DeploymentMachine>>()), Times.Never);
            }
        }


        /*
         * Agent configuartion as deployment agent against VSTS account
         * Collectioion name is not required
         */
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public async Task CanEnsureMachineGroupAgentConfigureVSTSScenario()
        {
            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating config manager");
                IConfigurationManager configManager = new ConfigurationManager();
                configManager.Initialize(tc);

                trace.Info("Preparing command line arguments for vsts scenario");
                var command = new CommandSettings(
                    tc,
                    new[]
                    {
                        "configure",
                       "--acceptteeeula",
                        "--machinegroup",
                        "--url", _expectedVSTSServerUrl,
                        "--agent", _expectedAgentName,
                        "--projectname", _expectedProjectName,
                        "--machinegroupname", _expectedMachineGroupName,
                        "--work", _expectedWorkFolder,
                        "--auth", _expectedAuthType,
                        "--token", _expectedToken
                    });
                trace.Info("Constructed.");

                _store.Setup(x => x.IsConfigured()).Returns(false);
                _configMgrAgentSettings = null;

                _extnMgr.Setup(x => x.GetExtensions<IConfigurationProvider>()).Returns(GetConfigurationProviderList(tc));
                _machineGroupServer.Setup(x => x.GetDeploymentGroupsAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.FromResult(GetDeploymentGroups(18, 27)));

                trace.Info("Ensuring all the required parameters are available in the command line parameter");
                await configManager.ConfigureAsync(command);

                _store.Setup(x => x.IsConfigured()).Returns(true);

                trace.Info("Configured, verifying all the parameter value");
                var s = configManager.LoadSettings();
                Assert.NotNull(s);
                Assert.True(s.ServerUrl.Equals(_expectedVSTSServerUrl, StringComparison.CurrentCultureIgnoreCase));
                Assert.True(s.AgentName.Equals(_expectedAgentName));
                Assert.True(s.PoolId.Equals(27));
                Assert.True(s.WorkFolder.Equals(_expectedWorkFolder));
                Assert.True(s.MachineGroupId.Equals(0));
                Assert.True(s.DeploymentGroupId.Equals(18));
                Assert.Null(s.ProjectName);
                Assert.True(s.ProjectId.Equals(_expectedProjectId));

                // Tags logic should not get trigger
                _machineGroupServer.Verify(x =>
                    x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<List<DeploymentMachine>>()), Times.Never);
            }
        }

        /*
        * Agent configuartion as deployment agent against on prem tfs
        * Collectioion name is required
        */
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public async Task CanEnsureMachineGroupAgentConfigureOnPremScenario()
        {
            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating config manager");
                IConfigurationManager configManager = new ConfigurationManager();
                configManager.Initialize(tc);

                var onPremTfsUrl = "http://localtfs:8080/tfs";

                trace.Info("Preparing command line arguments for vsts scenario");
                var command = new CommandSettings(
                    tc,
                    new[]
                    {
                        "configure",
                       "--acceptteeeula",
                        "--deploymentgroup",
                        "--url", onPremTfsUrl,
                        "--agent", _expectedAgentName,
                        "--collectionname", _expectedCollectionName,
                        "--projectname", _expectedProjectName,
                        "--deploymentgroupname", _expectedMachineGroupName,
                        "--work", _expectedWorkFolder,
                        "--auth", _expectedAuthType,
                        "--token", _expectedToken
                    });
                trace.Info("Constructed.");

                _store.Setup(x => x.IsConfigured()).Returns(false);
                _configMgrAgentSettings = null;

                _extnMgr.Setup(x => x.GetExtensions<IConfigurationProvider>()).Returns(GetConfigurationProviderList(tc));

                _machineGroupServer.Setup(x => x.GetDeploymentGroupsAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.FromResult(GetDeploymentGroups(3, 7)));

                trace.Info("Ensuring all the required parameters are available in the command line parameter");
                await configManager.ConfigureAsync(command);

                _store.Setup(x => x.IsConfigured()).Returns(true);

                trace.Info("Configured, verifying all the parameter value");
                var s = configManager.LoadSettings();
                Assert.NotNull(s);
                Assert.True(s.ServerUrl.Equals(onPremTfsUrl));
                Assert.True(s.AgentName.Equals(_expectedAgentName));
                Assert.True(s.PoolId.Equals(7));
                Assert.True(s.WorkFolder.Equals(_expectedWorkFolder));
                Assert.True(s.MachineGroupId.Equals(0));
                Assert.True(s.DeploymentGroupId.Equals(3));
                Assert.Null(s.ProjectName);
                Assert.True(s.ProjectId.Equals(_expectedProjectId));

                // Tags logic should not get trigger
                _machineGroupServer.Verify(x =>
                    x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<List<DeploymentMachine>>()), Times.Never);
            }
        }

        /*
        * Agent configuartion as deployment agent against VSTS account
        * Collectioion name is not required
        */
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public async Task CanEnsureMachineGroupAgentConfigureVSTSScenarioWithTags()
        {
            Guid receivedProjectId = Guid.Empty;
            string expectedProcessedTags = string.Empty;
            string tags = "Tag3, ,, Tag4  , , ,  Tag1,  , tag3 ";
            string expectedTags = "Tag3,Tag4,Tag1";
            int receivedMachineId = -1;
            int expectedDeploymentGroupId = 7;
            int receivedDeploymentGroupId = -1;

            _machineGroupServer.Setup(x =>
                x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(),
                    It.IsAny<List<DeploymentMachine>>())).Callback((Guid project, int deploymentGroupId, List<DeploymentMachine> deploymentMachine) =>
                    {
                        receivedProjectId = project;
                        expectedProcessedTags = string.Join(",", deploymentMachine.FirstOrDefault().Tags.ToArray());
                        receivedMachineId = deploymentMachine.FirstOrDefault().Id;
                        receivedDeploymentGroupId = deploymentGroupId;
                    }
                );

            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating config manager");
                IConfigurationManager configManager = new ConfigurationManager();
                configManager.Initialize(tc);

                trace.Info("Preparing command line arguments for vsts scenario");
                var command = new CommandSettings(
                    tc,
                    new[]
                    {
                        "configure",
                       "--acceptteeeula",
                        "--machinegroup",
                        "--adddeploymentgrouptags",
                        "--url", _expectedVSTSServerUrl,
                        "--agent", _expectedAgentName,
                        "--projectname", _expectedProjectName,
                        "--deploymentgroupname", _expectedMachineGroupName,
                        "--work", _expectedWorkFolder,
                        "--auth", _expectedAuthType,
                        "--token", _expectedToken,
                        "--deploymentgrouptags", tags
                    });
                trace.Info("Constructed.");

                _store.Setup(x => x.IsConfigured()).Returns(false);
                _configMgrAgentSettings = null;

                _extnMgr.Setup(x => x.GetExtensions<IConfigurationProvider>()).Returns(GetConfigurationProviderList(tc));


                _machineGroupServer.Setup(x => x.GetDeploymentGroupsAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.FromResult(GetDeploymentGroups(expectedDeploymentGroupId, 3)));

                trace.Info("Ensuring all the required parameters are available in the command line parameter");
                await configManager.ConfigureAsync(command);

                _store.Setup(x => x.IsConfigured()).Returns(true);

                trace.Info("Configured, verifying all the parameter value");
                var s = configManager.LoadSettings();
                Assert.NotNull(s);
                Assert.True(s.ServerUrl.Equals(_expectedVSTSServerUrl, StringComparison.CurrentCultureIgnoreCase));
                Assert.True(s.AgentName.Equals(_expectedAgentName));
                Assert.True(s.PoolId.Equals(3));
                Assert.True(s.DeploymentGroupId.Equals(7));
                Assert.True(s.WorkFolder.Equals(_expectedWorkFolder));
                Assert.True(s.MachineGroupId.Equals(0));
                Assert.Null(s.ProjectName);
                Assert.True(s.ProjectId.Equals(_expectedProjectId));

                Assert.True(receivedProjectId.Equals(new Guid(_expectedProjectId)), "UpdateDeploymentMachinesGroupAsync should get call with correct project name");
                Assert.True(expectedTags.Equals(expectedProcessedTags), "Before applying the tags, should get processed ( Trim, Remove duplicate)");
                Assert.True(receivedMachineId.Equals(_expectedDeploymentMachineId), "UpdateDeploymentMachinesGroupAsync should get call with correct machine id");
                Assert.True(receivedDeploymentGroupId.Equals(expectedDeploymentGroupId), "UpdateDeploymentMachinesGroupAsync should get call with correct deployment group id");
                // Tags logic should get trigger
                _machineGroupServer.Verify(x =>
                    x.UpdateDeploymentTargetsAsync(It.IsAny<Guid>(), It.IsAny<int>(),
                        It.IsAny<List<DeploymentMachine>>()), Times.Once);
            }
        }

        /*
         * Agent configuartion as deployment agent against VSTS account
         * Collectioion name is not required
         */
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "ConfigurationManagement")]
        public async Task CanEnsureEnvironmentVMResourceConfigureVSTSScenario()
        {
            SetEnvironmentVMResourceMocks();
            var projectId = Guid.NewGuid();

            using (TestHostContext tc = CreateTestContext())
            {
                Tracing trace = tc.GetTrace();

                trace.Info("Creating config manager");
                IConfigurationManager configManager = new ConfigurationManager();
                configManager.Initialize(tc);

                trace.Info("Preparing command line arguments for Environment VM resource config vsts scenario");
                var command = new CommandSettings(
                    tc,
                    new[]
                    {
                        "configure",
                       "--acceptteeeula",
                        "--environment",
                        "--url", _expectedVSTSServerUrl,
                        "--agent", "environmentVMResourceName",
                        "--projectname", "environmentPrj",
                        "--environmentname", "env1",
                        "--work", _expectedWorkFolder,
                        "--auth", _expectedAuthType,
                        "--token", _expectedToken
                    });
                trace.Info("Constructed.");

                _store.Setup(x => x.IsConfigured()).Returns(false);
                _configMgrAgentSettings = null;

                _extnMgr.Setup(x => x.GetExtensions<IConfigurationProvider>()).Returns(GetConfigurationProviderList(tc));
                _environmentsServer.Setup(x => x.GetEnvironmentsAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.FromResult(GetEnvironments("environmentPrj", projectId)));

                trace.Info("Ensuring all the required parameters are available in the command line parameter");
                await configManager.ConfigureAsync(command);

                _store.Setup(x => x.IsConfigured()).Returns(true);

                trace.Info("Configured, verifying all the parameter value");
                var s = configManager.LoadSettings();
                Assert.NotNull(s);
                Assert.True(s.ServerUrl.Equals(_expectedVSTSServerUrl, StringComparison.CurrentCultureIgnoreCase));
                Assert.True(s.AgentName.Equals("environmentVMResourceName"));
                Assert.True(s.AgentId.Equals(35));
                Assert.True(s.PoolId.Equals(57));
                Assert.True(s.WorkFolder.Equals(_expectedWorkFolder));
                Assert.True(s.MachineGroupId.Equals(0));
                Assert.True(s.DeploymentGroupId.Equals(0));
                Assert.True(s.EnvironmentId.Equals(54));
                Assert.True(s.ProjectName.Equals("environmentPrj"));
                Assert.True(s.ProjectId.Equals(projectId.ToString()));
                Assert.True(s.EnvironmentVMResourceId.Equals(_expectedEnvironmentVMResourceId));

                // Validate mock calls
                _environmentsServer.Verify( x => x.ConnectAsync(It.IsAny<VssConnection>()), Times.Once);
                _environmentsServer.Verify(x => x.AddEnvironmentVMAsync(It.IsAny<Guid>(), It.Is<int>(e => e == 54 ), It.Is<VirtualMachineResource>( v => v.Agent.Name == "environmentVMResourceName")), Times.Once);
                _environmentsServer.Verify(x => x.GetEnvironmentVMsAsync(It.IsAny<Guid>(), It.Is<int>(e => e == 54), It.Is<string>( v => v == "environmentVMResourceName")), Times.Once);
                _environmentsServer.Verify(x => x.GetEnvironmentsAsync(It.IsAny<string>(), It.Is<string>( e => e == "env1")), Times.Once);
                _environmentsServer.Verify(x => x.GetEnvironmentPoolAsync(It.Is<Guid>( p => p == projectId ), It.Is<int>( e => e == 54)), Times.Once);
            }
        }

        private void SetEnvironmentVMResourceMocks()
        {
            var expectedAgent = new TaskAgent("environmentVMResourceName") { Id = 35 };

            expectedAgent.Authorization = new TaskAgentAuthorization
            {
                ClientId = Guid.NewGuid(),
                AuthorizationUrl = new Uri("http://localhost:8080/tfs"),
            };

            var environmentPool = new TaskAgentPoolReference
            {
                Id = 57
            };

            var expectedEnvironmentVMResource = new VirtualMachineResource { Agent = expectedAgent, Id = _expectedEnvironmentVMResourceId, Name = "environmentVMResourceName" };

            _environmentsServer.Setup(x => x.ConnectAsync(It.IsAny<VssConnection>())).Returns(Task.FromResult<object>(null));
            _environmentsServer.Setup(x => x.AddEnvironmentVMAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<VirtualMachineResource>())).Returns(Task.FromResult(expectedEnvironmentVMResource));
            _environmentsServer.Setup(x => x.ReplaceEnvironmentVMAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<VirtualMachineResource>())).Returns(Task.FromResult(expectedEnvironmentVMResource));
            _environmentsServer.Setup(x => x.GetEnvironmentVMsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>())).Returns(Task.FromResult(new List<VirtualMachineResource>() { }));
            _environmentsServer.Setup(x => x.DeleteEnvironmentVMAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns(Task.FromResult<object>(null));
            _environmentsServer.Setup(x => x.GetEnvironmentPoolAsync(It.IsAny<Guid>(), It.IsAny<int>())).Returns(Task.FromResult(environmentPool));
        }

        private List<DeploymentGroup> GetDeploymentGroups(int dgId, int poolId)
        {
            var dgJson = "{'id':" + dgId.ToString() + ",'project':{'id':'" + _expectedProjectId + "','name':'Test-Project1'},'name':'ch-test','pool':{'id':" + poolId.ToString() + ",'scope':'0efb4611-d565-4cd1-9a64-7d6cb6d7d5f0'}}";
            var deploymentGroup = JsonConvert.DeserializeObject<DeploymentGroup>(dgJson);
            return new List<DeploymentGroup>() { deploymentGroup };
        }

        private List<EnvironmentInstance> GetEnvironments(string projectName, Guid projectId)
        {
            var environmentJson = "{'id':54, 'project':{'id':'" + projectId + "','name':'" + projectName  +"'},'name':'env1'}";
            var env = JsonConvert.DeserializeObject<EnvironmentInstance>(environmentJson);

            return new List<EnvironmentInstance>{ env };
        }

        // Init the Agent Config Provider
        private List<IConfigurationProvider> GetConfigurationProviderList(TestHostContext tc)
        {
            IConfigurationProvider buildReleasesAgentConfigProvider = new BuildReleasesAgentConfigProvider();
            buildReleasesAgentConfigProvider.Initialize(tc);

            _deploymentGroupAgentConfigProvider = new DeploymentGroupAgentConfigProvider();
            _deploymentGroupAgentConfigProvider.Initialize(tc);

            IConfigurationProvider sharedDeploymentAgentConfiguration = new SharedDeploymentAgentConfigProvider();
            sharedDeploymentAgentConfiguration.Initialize(tc);

            IConfigurationProvider environmentVMResourceConfiguration = new EnvironmentVMResourceConfigProvider();
            environmentVMResourceConfiguration.Initialize(tc);

            return new List<IConfigurationProvider> { buildReleasesAgentConfigProvider, _deploymentGroupAgentConfigProvider, sharedDeploymentAgentConfiguration, environmentVMResourceConfiguration };
        }
        // TODO Unit Test for IsConfigured - Rename config file and make sure it returns false

        public void Dispose()
        {
            rsa?.Dispose();
        }

    }
}
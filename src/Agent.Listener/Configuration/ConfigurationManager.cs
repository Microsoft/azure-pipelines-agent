// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Capabilities;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(ConfigurationManager))]
    public interface IConfigurationManager : IAgentService
    {
        bool IsConfigured();
        Task ConfigureAsync(CommandSettings command);
        Task UnconfigureAsync(CommandSettings command);
        AgentSettings LoadSettings();
    }

    public sealed class ConfigurationManager : AgentService, IConfigurationManager
    {
        private IConfigurationStore _store;
        private IAgentServer _agentServer;
        private ITerminal _term;

        public override void Initialize(IHostContext hostContext)
        {
            ArgUtil.NotNull(hostContext, nameof(hostContext));
            base.Initialize(hostContext);
            Trace.Verbose("Creating _store");
            _store = hostContext.GetService<IConfigurationStore>();
            Trace.Verbose("store created");
            _term = hostContext.GetService<ITerminal>();
        }

        public bool IsConfigured()
        {
            bool result = _store.IsConfigured();
            Trace.Info($"Is configured: {result}");
            return result;
        }

        public AgentSettings LoadSettings()
        {
            Trace.Info(nameof(LoadSettings));
            if (!IsConfigured())
            {
                throw new InvalidOperationException("Not configured");
            }

            AgentSettings settings = _store.GetSettings();
            Trace.Info("Settings Loaded");

            return settings;
        }

        public async Task ConfigureAsync(CommandSettings command)
        {
            ArgUtil.NotNull(command, nameof(command));

            if (PlatformUtil.RunningOnWindows)
            { 
                CheckAgentRootDirectorySecure();
            }

            Trace.Info(nameof(ConfigureAsync));
            if (IsConfigured())
            {
                throw new InvalidOperationException(StringUtil.Loc("AlreadyConfiguredError"));
            }

            // Populate proxy setting from commandline args
            var vstsProxy = HostContext.GetService<IVstsAgentWebProxy>();
            bool saveProxySetting = false;
            string proxyUrl = command.GetProxyUrl();
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                if (!Uri.IsWellFormedUriString(proxyUrl, UriKind.Absolute))
                {
                    throw new ArgumentOutOfRangeException(nameof(proxyUrl));
                }

                Trace.Info("Reset proxy base on commandline args.");
                string proxyUserName = command.GetProxyUserName();
                string proxyPassword = command.GetProxyPassword();
                (vstsProxy as VstsAgentWebProxy).SetupProxy(proxyUrl, proxyUserName, proxyPassword);
                saveProxySetting = true;
            }

            // Populate cert setting from commandline args
            var agentCertManager = HostContext.GetService<IAgentCertificateManager>();
            bool saveCertSetting = false;
            bool skipCertValidation = command.GetSkipCertificateValidation();
            string caCert = command.GetCACertificate();
            string clientCert = command.GetClientCertificate();
            string clientCertKey = command.GetClientCertificatePrivateKey();
            string clientCertArchive = command.GetClientCertificateArchrive();
            string clientCertPassword = command.GetClientCertificatePassword();

            // We require all Certificate files are under agent root.
            // So we can set ACL correctly when configure as service
            if (!string.IsNullOrEmpty(caCert))
            {
                caCert = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), caCert);
                ArgUtil.File(caCert, nameof(caCert));
            }

            if (!string.IsNullOrEmpty(clientCert) &&
                !string.IsNullOrEmpty(clientCertKey) &&
                !string.IsNullOrEmpty(clientCertArchive))
            {
                // Ensure all client cert pieces are there.
                clientCert = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), clientCert);
                clientCertKey = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), clientCertKey);
                clientCertArchive = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), clientCertArchive);

                ArgUtil.File(clientCert, nameof(clientCert));
                ArgUtil.File(clientCertKey, nameof(clientCertKey));
                ArgUtil.File(clientCertArchive, nameof(clientCertArchive));
            }
            else if (!string.IsNullOrEmpty(clientCert) ||
                     !string.IsNullOrEmpty(clientCertKey) ||
                     !string.IsNullOrEmpty(clientCertArchive))
            {
                // Print out which args are missing.
                ArgUtil.NotNullOrEmpty(Constants.Agent.CommandLine.Args.SslClientCert, Constants.Agent.CommandLine.Args.SslClientCert);
                ArgUtil.NotNullOrEmpty(Constants.Agent.CommandLine.Args.SslClientCertKey, Constants.Agent.CommandLine.Args.SslClientCertKey);
                ArgUtil.NotNullOrEmpty(Constants.Agent.CommandLine.Args.SslClientCertArchive, Constants.Agent.CommandLine.Args.SslClientCertArchive);
            }

            if (skipCertValidation || !string.IsNullOrEmpty(caCert) || !string.IsNullOrEmpty(clientCert))
            {
                Trace.Info("Reset agent cert setting base on commandline args.");
                (agentCertManager as AgentCertificateManager).SetupCertificate(skipCertValidation, caCert, clientCert, clientCertKey, clientCertArchive, clientCertPassword);
                saveCertSetting = true;
            }

            AgentSettings agentSettings = new AgentSettings();
            // TEE EULA
            agentSettings.AcceptTeeEula = false;
            switch (PlatformUtil.HostOS)
            {
                case PlatformUtil.OS.OSX:
                case PlatformUtil.OS.Linux:
                case PlatformUtil.OS.FreeBSD:
                    // Write the section header.
                    WriteSection(StringUtil.Loc("EulasSectionHeader"));

                    // Verify the EULA exists on disk in the expected location.
                    string eulaFile = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), Constants.Path.TeeDirectory, "license.html");
                    ArgUtil.File(eulaFile, nameof(eulaFile));

                    // Write elaborate verbiage about the TEE EULA.
                    _term.WriteLine(StringUtil.Loc("TeeEula", eulaFile));
                    _term.WriteLine();

                    // Prompt to acccept the TEE EULA.
                    agentSettings.AcceptTeeEula = command.GetAcceptTeeEula();
                    break;
                case PlatformUtil.OS.Windows:
                    // Warn and continue if .NET 4.6 is not installed.
                    if (!NetFrameworkUtil.Test(new Version(4, 6), Trace))
                    {
                        WriteSection(StringUtil.Loc("PrerequisitesSectionHeader")); // Section header.
                        _term.WriteLine(StringUtil.Loc("MinimumNetFrameworkTfvc")); // Warning.
                    }

                    break;
                default:
                    throw new NotSupportedException();
            }

            // Create the configuration provider as per agent type.
            string agentType;
            if (command.GetDeploymentOrMachineGroup())
            {
                agentType = Constants.Agent.AgentConfigurationProvider.DeploymentAgentConfiguration;
            }
            else if (command.GetDeploymentPool())
            {
                agentType = Constants.Agent.AgentConfigurationProvider.SharedDeploymentAgentConfiguration;
            }
            else if (command.GetEnvironmentVMResource())
            {
                agentType = Constants.Agent.AgentConfigurationProvider.EnvironmentVMResourceConfiguration;
            }
            else
            {
                agentType = Constants.Agent.AgentConfigurationProvider.BuildReleasesAgentConfiguration;
            }

            var extensionManager = HostContext.GetService<IExtensionManager>();
            IConfigurationProvider agentProvider =
                (extensionManager.GetExtensions<IConfigurationProvider>())
                .FirstOrDefault(x => x.ConfigurationProviderType == agentType);
            ArgUtil.NotNull(agentProvider, agentType);

            bool isHostedServer = false;
            // Loop getting url and creds until you can connect
            ICredentialProvider credProvider = null;
            VssCredentials creds = null;
            WriteSection(StringUtil.Loc("ConnectSectionHeader"));
            while (true)
            {
                // Get the URL
                agentProvider.GetServerUrl(agentSettings, command);

                // Get the credentials
                credProvider = GetCredentialProvider(command, agentSettings.ServerUrl);
                creds = credProvider.GetVssCredentials(HostContext);
                Trace.Info("cred retrieved");
                try
                {
                    // Determine the service deployment type based on connection data. (Hosted/OnPremises)
                    isHostedServer = await IsHostedServer(agentSettings.ServerUrl, creds);

                    // Get the collection name for deployment group
                    agentProvider.GetCollectionName(agentSettings, command, isHostedServer);

                    // Validate can connect.
                    await agentProvider.TestConnectionAsync(agentSettings, creds, isHostedServer);
                    Trace.Info("Test Connection complete.");
                    break;
                }
                catch (Exception e) when (!command.Unattended())
                {
                    _term.WriteError(e);
                    _term.WriteError(StringUtil.Loc("FailedToConnect"));
                }
            }

            _agentServer = HostContext.GetService<IAgentServer>();
            // We want to use the native CSP of the platform for storage, so we use the RSACSP directly
            RSAParameters publicKey;
            var keyManager = HostContext.GetService<IRSAKeyManager>();
            using (var rsa = keyManager.CreateKey())
            {
                publicKey = rsa.ExportParameters(false);
            }

            // Loop getting agent name and pool name
            WriteSection(StringUtil.Loc("RegisterAgentSectionHeader"));

            while (true)
            {
                try
                {
                    await agentProvider.GetPoolIdAndName(agentSettings, command);
                    break;
                }
                catch (Exception e) when (!command.Unattended())
                {
                    _term.WriteError(e);
                    _term.WriteError(agentProvider.GetFailedToFindPoolErrorString());
                }
            }

            TaskAgent agent;
            while (true)
            {
                agentSettings.AgentName = command.GetAgentName();

                // Get the system capabilities.
                // TODO: Hook up to ctrl+c cancellation token.
                _term.WriteLine(StringUtil.Loc("ScanToolCapabilities"));
                Dictionary<string, string> systemCapabilities = await HostContext.GetService<ICapabilitiesManager>().GetCapabilitiesAsync(agentSettings, CancellationToken.None);

                _term.WriteLine(StringUtil.Loc("ConnectToServer"));
                agent = await agentProvider.GetAgentAsync(agentSettings);
                if (agent != null)
                {
                    if (command.GetReplace())
                    {
                        // Update existing agent with new PublicKey, agent version and SystemCapabilities.
                        agent = UpdateExistingAgent(agent, publicKey, systemCapabilities);

                        try
                        {
                            agent = await agentProvider.UpdateAgentAsync(agentSettings, agent, command);
                            _term.WriteLine(StringUtil.Loc("AgentReplaced"));
                            break;
                        }
                        catch (Exception e) when (!command.Unattended())
                        {
                            _term.WriteError(e);
                            _term.WriteError(StringUtil.Loc("FailedToReplaceAgent"));
                        }
                    }
                    else if (command.Unattended())
                    {
                        // if not replace and it is unattended config.
                        agentProvider.ThrowTaskAgentExistException(agentSettings);
                    }
                }
                else
                {
                    // Create a new agent.
                    agent = CreateNewAgent(agentSettings.AgentName, publicKey, systemCapabilities);

                    try
                    {
                        agent = await agentProvider.AddAgentAsync(agentSettings, agent, command);
                        _term.WriteLine(StringUtil.Loc("AgentAddedSuccessfully"));
                        break;
                    }
                    catch (Exception e) when (!command.Unattended())
                    {
                        _term.WriteError(e);
                        _term.WriteError(StringUtil.Loc("AddAgentFailed"));
                    }
                }
            }
            // Add Agent Id to settings
            agentSettings.AgentId = agent.Id;

            // respect the serverUrl resolve by server.
            // in case of agent configured using collection url instead of account url.
            string agentServerUrl;
            if (agent.Properties.TryGetValidatedValue<string>("ServerUrl", out agentServerUrl) &&
                !string.IsNullOrEmpty(agentServerUrl))
            {
                Trace.Info($"Agent server url resolve by server: '{agentServerUrl}'.");

                // we need make sure the Schema/Host/Port component of the url remain the same.
                UriBuilder inputServerUrl = new UriBuilder(agentSettings.ServerUrl);
                UriBuilder serverReturnedServerUrl = new UriBuilder(agentServerUrl);
                if (Uri.Compare(inputServerUrl.Uri, serverReturnedServerUrl.Uri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    inputServerUrl.Path = serverReturnedServerUrl.Path;
                    Trace.Info($"Replace server returned url's scheme://host:port component with user input server url's scheme://host:port: '{inputServerUrl.Uri.AbsoluteUri}'.");
                    agentSettings.ServerUrl = inputServerUrl.Uri.AbsoluteUri;
                }
                else
                {
                    agentSettings.ServerUrl = agentServerUrl;
                }
            }

            // See if the server supports our OAuth key exchange for credentials
            if (agent.Authorization != null &&
                agent.Authorization.ClientId != Guid.Empty &&
                agent.Authorization.AuthorizationUrl != null)
            {
                // We use authorizationUrl as the oauth endpoint url by default.
                // For TFS, we need make sure the Schema/Host/Port component of the oauth endpoint url also match configuration url. (Incase of customer's agent configure URL and TFS server public URL are different)
                // Which means, we will keep use the original authorizationUrl in the VssOAuthJwtBearerClientCredential (authorizationUrl is the audience),
                // But might have different Url in VssOAuthCredential (connection url)
                // We can't do this for VSTS, since its SPS/TFS urls are different.
                UriBuilder configServerUrl = new UriBuilder(agentSettings.ServerUrl);
                UriBuilder oauthEndpointUrlBuilder = new UriBuilder(agent.Authorization.AuthorizationUrl);
                if (!isHostedServer && Uri.Compare(configServerUrl.Uri, oauthEndpointUrlBuilder.Uri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    oauthEndpointUrlBuilder.Scheme = configServerUrl.Scheme;
                    oauthEndpointUrlBuilder.Host = configServerUrl.Host;
                    oauthEndpointUrlBuilder.Port = configServerUrl.Port;
                    Trace.Info($"Set oauth endpoint url's scheme://host:port component to match agent configure url's scheme://host:port: '{oauthEndpointUrlBuilder.Uri.AbsoluteUri}'.");
                }

                var credentialData = new CredentialData
                {
                    Scheme = Constants.Configuration.OAuth,
                    Data =
                    {
                        { "clientId", agent.Authorization.ClientId.ToString("D") },
                        { "authorizationUrl", agent.Authorization.AuthorizationUrl.AbsoluteUri },
                        { "oauthEndpointUrl", oauthEndpointUrlBuilder.Uri.AbsoluteUri },
                    },
                };

                // Save the negotiated OAuth credential data
                _store.SaveCredential(credentialData);
            }
            else
            {
                switch (PlatformUtil.HostOS)
                {
                    case PlatformUtil.OS.OSX:
                    case PlatformUtil.OS.Linux:
                    case PlatformUtil.OS.FreeBSD:
                        // Save the provided admin cred for compat with previous agent.
                        _store.SaveCredential(credProvider.CredentialData);
                        break;
                    case PlatformUtil.OS.Windows:
                        // Not supported against TFS 2015.
                        _term.WriteError(StringUtil.Loc("Tfs2015NotSupported"));
                        return;
                    default:
                        throw new NotSupportedException();
                }
            }

            // Testing agent connection, detect any protential connection issue, like local clock skew that cause OAuth token expired.
            _term.WriteLine(StringUtil.Loc("TestAgentConnection"));
            var credMgr = HostContext.GetService<ICredentialManager>();
            VssCredentials credential = credMgr.LoadCredentials();
            var agentSvr = HostContext.GetService<IAgentServer>();
            try
            {
                await agentSvr.ConnectAsync(new Uri(agentSettings.ServerUrl), credential);
            }
            catch (VssOAuthTokenRequestException ex) when (ex.Message.Contains("Current server time is"))
            {
                // there are two exception messages server send that indicate clock skew.
                // 1. The bearer token expired on {jwt.ValidTo}. Current server time is {DateTime.UtcNow}.
                // 2. The bearer token is not valid until {jwt.ValidFrom}. Current server time is {DateTime.UtcNow}.
                Trace.Error("Catch exception during test agent connection.");
                Trace.Error(ex);
                throw new InvalidOperationException(StringUtil.Loc("LocalClockSkewed"));
            }

            // We will Combine() what's stored with root.  Defaults to string a relative path
            agentSettings.WorkFolder = command.GetWork();

            // notificationPipeName for Hosted agent provisioner.
            agentSettings.NotificationPipeName = command.GetNotificationPipeName();

            agentSettings.MonitorSocketAddress = command.GetMonitorSocketAddress();

            agentSettings.NotificationSocketAddress = command.GetNotificationSocketAddress();

            agentSettings.DisableLogUploads = command.GetDisableLogUploads();

            agentSettings.AlwaysExtractTask = command.GetAlwaysExtractTask();
            
            _store.SaveSettings(agentSettings);

            if (saveProxySetting)
            {
                Trace.Info("Save proxy setting to disk.");
                (vstsProxy as VstsAgentWebProxy).SaveProxySetting();
            }

            if (saveCertSetting)
            {
                Trace.Info("Save agent cert setting to disk.");
                (agentCertManager as AgentCertificateManager).SaveCertificateSetting();
            }

            _term.WriteLine(StringUtil.Loc("SavedSettings", DateTime.UtcNow));

            bool saveRuntimeOptions = false;
            var runtimeOptions = new AgentRuntimeOptions();
            if (PlatformUtil.RunningOnWindows && command.GetGitUseSChannel())
            {
                saveRuntimeOptions = true;
                runtimeOptions.GitUseSecureChannel = true;
            }
            if (saveRuntimeOptions)
            {
                Trace.Info("Save agent runtime options to disk.");
                _store.SaveAgentRuntimeOptions(runtimeOptions);
            }

            if (PlatformUtil.RunningOnWindows)
            {
                // config windows service
                bool runAsService = command.GetRunAsService();
                if (runAsService)
                {
                    Trace.Info("Configuring to run the agent as service");
                    var serviceControlManager = HostContext.GetService<IWindowsServiceControlManager>();
                    serviceControlManager.ConfigureService(agentSettings, command);
                }
                // config auto logon
                else if (command.GetRunAsAutoLogon())
                {
                    Trace.Info("Agent is going to run as process setting up the 'AutoLogon' capability for the agent.");
                    var autoLogonConfigManager = HostContext.GetService<IAutoLogonManager>();
                    await autoLogonConfigManager.ConfigureAsync(command);
                    //Important: The machine may restart if the autologon user is not same as the current user
                    //if you are adding code after this, keep that in mind
                }
            }
            else if (PlatformUtil.RunningOnLinux)
            {
                // generate service config script for Linux
                var serviceControlManager = HostContext.GetService<ILinuxServiceControlManager>();
                serviceControlManager.GenerateScripts(agentSettings);
            }
            else if (PlatformUtil.RunningOnFreeBSD)
            {
                // generate service config script for FreeBSD
                var serviceControlManager = HostContext.GetService<IFreeBSDServiceControlManager>();
                serviceControlManager.GenerateScripts(agentSettings);
            }
            else if (PlatformUtil.RunningOnMacOS)
            {
                // generate service config script for macOS
                var serviceControlManager = HostContext.GetService<IMacOSServiceControlManager>();
                serviceControlManager.GenerateScripts(agentSettings);
            }
        }

        public async Task UnconfigureAsync(CommandSettings command)
        {
            ArgUtil.NotNull(command, nameof(command));
            string currentAction = string.Empty;
            try
            {
                //stop, uninstall service and remove service config file
                if (_store.IsServiceConfigured())
                {
                    currentAction = StringUtil.Loc("UninstallingService");
                    _term.WriteLine(currentAction);
                    if (PlatformUtil.RunningOnWindows)
                    {
                        var serviceControlManager = HostContext.GetService<IWindowsServiceControlManager>();
                        serviceControlManager.UnconfigureService();
                        _term.WriteLine(StringUtil.Loc("Success") + currentAction);
                    }
                    else if (PlatformUtil.RunningOnLinux)
                    {
                        // unconfig systemd service first
                        throw new InvalidOperationException(StringUtil.Loc("UnconfigureSystemDService"));
                    }
                    else if (PlatformUtil.RunningOnFreeBSD)
                    {
                        // unconfig daemon service first
                        throw new InvalidOperationException(StringUtil.Loc("UnconfigureDaemonService"));
                    }
                    else if (PlatformUtil.RunningOnMacOS)
                    {
                        // unconfig macOS service first
                        throw new InvalidOperationException(StringUtil.Loc("UnconfigureOSXService"));
                    }
                }
                else
                {
                    if (PlatformUtil.RunningOnWindows)
                    {
                        //running as process, unconfigure autologon if it was configured
                        if (_store.IsAutoLogonConfigured())
                        {
                            currentAction = StringUtil.Loc("UnconfigAutologon");
                            _term.WriteLine(currentAction);
                            var autoLogonConfigManager = HostContext.GetService<IAutoLogonManager>();
                            autoLogonConfigManager.Unconfigure();
                            _term.WriteLine(StringUtil.Loc("Success") + currentAction);
                        }
                        else
                        {
                            Trace.Info("AutoLogon was not configured on the agent.");
                        }
                    }
                }

                //delete agent from the server
                currentAction = StringUtil.Loc("UnregisteringAgent");
                _term.WriteLine(currentAction);
                bool isConfigured = _store.IsConfigured();
                bool hasCredentials = _store.HasCredentials();
                if (isConfigured && hasCredentials)
                {
                    AgentSettings settings = _store.GetSettings();
                    var credentialManager = HostContext.GetService<ICredentialManager>();

                    // Get the credentials
                    var credProvider = GetCredentialProvider(command, settings.ServerUrl);
                    VssCredentials creds = credProvider.GetVssCredentials(HostContext);
                    Trace.Info("cred retrieved");

                    bool isEnvironmentVMResource = false;
                    bool isDeploymentGroup = (settings.MachineGroupId > 0) || (settings.DeploymentGroupId > 0);
                    if(!isDeploymentGroup)
                    {
                        isEnvironmentVMResource = settings.EnvironmentId > 0;
                    }

                    Trace.Info("Agent configured for deploymentGroup : {0}", isDeploymentGroup.ToString());

                    string agentType = isDeploymentGroup
                   ? Constants.Agent.AgentConfigurationProvider.DeploymentAgentConfiguration
                   : isEnvironmentVMResource
                   ? Constants.Agent.AgentConfigurationProvider.EnvironmentVMResourceConfiguration
                   : Constants.Agent.AgentConfigurationProvider.BuildReleasesAgentConfiguration;

                    var extensionManager = HostContext.GetService<IExtensionManager>();
                    IConfigurationProvider agentProvider = (extensionManager.GetExtensions<IConfigurationProvider>()).FirstOrDefault(x => x.ConfigurationProviderType == agentType);
                    ArgUtil.NotNull(agentProvider, agentType);

                    // Determine the service deployment type based on connection data. (Hosted/OnPremises)
                    bool isHostedServer = await IsHostedServer(settings.ServerUrl, creds);
                    await agentProvider.TestConnectionAsync(settings, creds, isHostedServer);

                    TaskAgent agent = await agentProvider.GetAgentAsync(settings);
                    if (agent == null)
                    {
                        _term.WriteLine(StringUtil.Loc("Skipping") + currentAction);
                    }
                    else
                    {
                        await agentProvider.DeleteAgentAsync(settings);
                        _term.WriteLine(StringUtil.Loc("Success") + currentAction);
                    }
                }
                else
                {
                    _term.WriteLine(StringUtil.Loc("MissingConfig"));
                }

                //delete credential config files
                currentAction = StringUtil.Loc("DeletingCredentials");
                _term.WriteLine(currentAction);
                if (hasCredentials)
                {
                    _store.DeleteCredential();
                    var keyManager = HostContext.GetService<IRSAKeyManager>();
                    keyManager.DeleteKey();
                    _term.WriteLine(StringUtil.Loc("Success") + currentAction);
                }
                else
                {
                    _term.WriteLine(StringUtil.Loc("Skipping") + currentAction);
                }

                //delete settings config file
                currentAction = StringUtil.Loc("DeletingSettings");
                _term.WriteLine(currentAction);
                if (isConfigured)
                {
                    // delete proxy setting
                    (HostContext.GetService<IVstsAgentWebProxy>() as VstsAgentWebProxy).DeleteProxySetting();

                    // delete agent cert setting
                    (HostContext.GetService<IAgentCertificateManager>() as AgentCertificateManager).DeleteCertificateSetting();

                    // delete agent runtime option
                    _store.DeleteAgentRuntimeOptions();

                    _store.DeleteSettings();
                    _term.WriteLine(StringUtil.Loc("Success") + currentAction);
                }
                else
                {
                    _term.WriteLine(StringUtil.Loc("Skipping") + currentAction);
                }
            }
            catch (Exception)
            {
                _term.WriteLine(StringUtil.Loc("Failed") + currentAction);
                throw;
            }
        }

        private ICredentialProvider GetCredentialProvider(CommandSettings command, string serverUrl)
        {
            Trace.Info(nameof(GetCredentialProvider));

            var credentialManager = HostContext.GetService<ICredentialManager>();
            // Get the default auth type.
            // Use PAT as long as the server uri scheme is Https and looks like a FQDN
            // Otherwise windows use Integrated, linux/mac use negotiate.
            string defaultAuth = string.Empty;
            Uri server = new Uri(serverUrl);
            if (server.Scheme == Uri.UriSchemeHttps && server.Host.Contains('.'))
            {
                defaultAuth = Constants.Configuration.PAT;
            }
            else
            {
                defaultAuth = PlatformUtil.RunningOnWindows ? Constants.Configuration.Integrated : Constants.Configuration.Negotiate;
            }

            string authType = command.GetAuth(defaultValue: defaultAuth);

            // Create the credential.
            Trace.Info("Creating credential for auth: {0}", authType);
            var provider = credentialManager.GetCredentialProvider(authType);
            if (provider.RequireInteractive && command.Unattended())
            {
                throw new NotSupportedException($"Authentication type '{authType}' is not supported for unattended configuration.");
            }

            provider.EnsureCredential(HostContext, command, serverUrl);
            return provider;
        }

        private TaskAgent UpdateExistingAgent(TaskAgent agent, RSAParameters publicKey, Dictionary<string, string> systemCapabilities)
        {
            ArgUtil.NotNull(agent, nameof(agent));
            agent.Authorization = new TaskAgentAuthorization
            {
                PublicKey = new TaskAgentPublicKey(publicKey.Exponent, publicKey.Modulus),
            };

            // update - update instead of delete so we don't lose user capabilities etc...
            agent.Version = BuildConstants.AgentPackage.Version;
            agent.OSDescription = RuntimeInformation.OSDescription;

            foreach (KeyValuePair<string, string> capability in systemCapabilities)
            {
                agent.SystemCapabilities[capability.Key] = capability.Value ?? string.Empty;
            }

            return agent;
        }

        private TaskAgent CreateNewAgent(string agentName, RSAParameters publicKey, Dictionary<string, string> systemCapabilities)
        {
            TaskAgent agent = new TaskAgent(agentName)
            {
                Authorization = new TaskAgentAuthorization
                {
                    PublicKey = new TaskAgentPublicKey(publicKey.Exponent, publicKey.Modulus),
                },
                MaxParallelism = 1,
                ProvisioningState = TaskAgentProvisioningStateConstants.Provisioned,
                Version = BuildConstants.AgentPackage.Version,
                OSDescription = RuntimeInformation.OSDescription,
            };

            foreach (KeyValuePair<string, string> capability in systemCapabilities)
            {
                agent.SystemCapabilities[capability.Key] = capability.Value ?? string.Empty;
            }

            return agent;
        }

        private void WriteSection(string message)
        {
            _term.WriteLine();
            _term.WriteLine($">> {message}:");
            _term.WriteLine();
        }

        private void CheckAgentRootDirectorySecure()
        {
            Trace.Info(nameof(CheckAgentRootDirectorySecure));

            try
            {
                string rootDirPath = HostContext.GetDirectory(WellKnownDirectory.Root);

                if (!String.IsNullOrEmpty(rootDirPath))
                {
                    // Get info about root folder
                    DirectoryInfo dirInfo = new DirectoryInfo(rootDirPath);

                    // Get directory access control list 
                    DirectorySecurity directorySecurityInfo = dirInfo.GetAccessControl();
                    AuthorizationRuleCollection dirAccessRules = directorySecurityInfo.GetAccessRules(true, true, typeof(NTAccount));


                    // Get identity reference of the BUILTIN\Users group
                    IdentityReference bulitInUsersGroup = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Translate(typeof(NTAccount));

                    // Check if BUILTIN\Users group have modify/write rights for the agent root folder
                    List<FileSystemAccessRule> potentiallyInsecureRules = dirAccessRules.OfType<FileSystemAccessRule>().AsParallel()
                                                                          .Where(rule => rule.IdentityReference == bulitInUsersGroup && (rule.FileSystemRights.HasFlag(FileSystemRights.Write) || rule.FileSystemRights.HasFlag(FileSystemRights.Modify)))
                                                                          .ToList<FileSystemAccessRule>();

                    // Notify user if there are some potentially insecure access rules for the agent root folder
                    if (potentiallyInsecureRules.Count != 0)
                    {
                        Trace.Warning("The {0} group have the following permissions to the agent root folder: ", bulitInUsersGroup.ToString());

                        potentiallyInsecureRules.ForEach(accessRule => Trace.Warning("- {0}", accessRule.FileSystemRights.ToString()));

                        _term.Write(StringUtil.Loc("agentRootFolderInsecure", bulitInUsersGroup.ToString()));
                    }
                }
                else 
                {
                    Trace.Warning("Can't get path to the agent root folder, check was skipped.");
                }
            }
            catch (Exception ex) {
                Trace.Warning("Can't check permissions for agent root folder:");
                Trace.Warning(ex.Message);
                _term.Write(StringUtil.Loc("agentRootFolderCheckError"));
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope", MessageId = "locationServer")]
        private async Task<bool> IsHostedServer(string serverUrl, VssCredentials credentials)
        {
            // Determine the service deployment type based on connection data. (Hosted/OnPremises)
            var locationServer = HostContext.GetService<ILocationServer>();
            VssConnection connection = VssUtil.CreateConnection(new Uri(serverUrl), credentials);
            await locationServer.ConnectAsync(connection);
            try
            {
                var connectionData = await locationServer.GetConnectionDataAsync();
                Trace.Info($"Server deployment type: {connectionData.DeploymentType}");
                return connectionData.DeploymentType.HasFlag(DeploymentFlags.Hosted);
            }
            catch (Exception ex)
            {
                // Since the DeploymentType is Enum, deserialization exception means there is a new Enum member been added.
                // It's more likely to be Hosted since OnPremises is always behind and customer can update their agent if are on-prem
                Trace.Error(ex);
                return true;
            }
        }
    }
}
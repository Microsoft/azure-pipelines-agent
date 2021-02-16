// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Content.Common.Telemetry;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Agent.Sdk.Knob;

namespace Agent.Sdk
{
    public interface IAgentTaskPlugin
    {
        Guid Id { get; }
        string Stage { get; }
        Task RunAsync(AgentTaskPluginExecutionContext executionContext, CancellationToken token);
    }

    public class WellKnownJobSettings
    {
        public static readonly string HasMultipleCheckouts = "HasMultipleCheckouts";
        public static readonly string FirstRepositoryCheckedOut = "FirstRepositoryCheckedOut";
        public static readonly string WorkspaceIdentifier = "WorkspaceIdentifier";
    }

    public class AgentTaskPluginExecutionContext : ITraceWriter, IKnobValueContext
    {
        private VssConnection _connection;
        private readonly object _stdoutLock = new object();
        private readonly ITraceWriter _trace; // for unit tests
        private static string _failTaskCommand = "##vso[task.complete result=Failed;]";

        public AgentTaskPluginExecutionContext()
            : this(null)
        { }

        public AgentTaskPluginExecutionContext(ITraceWriter trace)
        {
            _trace = trace;
            this.Endpoints = new List<ServiceEndpoint>();
            this.Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.Repositories = new List<Pipelines.RepositoryResource>();
            this.TaskVariables = new Dictionary<string, VariableValue>(StringComparer.OrdinalIgnoreCase);
            this.Variables = new Dictionary<string, VariableValue>(StringComparer.OrdinalIgnoreCase);
        }

        public List<ServiceEndpoint> Endpoints { get; set; }
        public List<Pipelines.RepositoryResource> Repositories { get; set; }
        public Dictionary<string, VariableValue> Variables { get; set; }
        public Dictionary<string, VariableValue> TaskVariables { get; set; }
        public Dictionary<string, string> Inputs { get; set; }
        public ContainerInfo Container {get; set; }
        public Dictionary<string, string> JobSettings { get; set; }

        [JsonIgnore]
        public VssConnection VssConnection
        {
            get
            {
                if (_connection == null)
                {
                    _connection = InitializeVssConnection();
                }
                return _connection;
            }
        }

        public VssConnection InitializeVssConnection()
        {
            var headerValues = new List<ProductInfoHeaderValue>();
            headerValues.Add(new ProductInfoHeaderValue($"VstsAgentCore-Plugin", Variables.GetValueOrDefault("agent.version")?.Value ?? "Unknown"));
            headerValues.Add(new ProductInfoHeaderValue($"({RuntimeInformation.OSDescription.Trim()})"));

            if (VssClientHttpRequestSettings.Default.UserAgent != null && VssClientHttpRequestSettings.Default.UserAgent.Count > 0)
            {
                headerValues.AddRange(VssClientHttpRequestSettings.Default.UserAgent);
            }

            VssClientHttpRequestSettings.Default.UserAgent = headerValues;

            if (PlatformUtil.RunningOnLinux || PlatformUtil.RunningOnFreeBSD || PlatformUtil.RunningOnMacOS)
            {
                // The .NET Core 2.1 runtime switched its HTTP default from HTTP 1.1 to HTTP 2.
                // This causes problems with some versions of the Curl handler.
                // See GitHub issue https://github.com/dotnet/corefx/issues/32376
                VssClientHttpRequestSettings.Default.UseHttp11 = true;
            }

            var certSetting = GetCertConfiguration();
            if (certSetting != null)
            {
                if (!string.IsNullOrEmpty(certSetting.ClientCertificateArchiveFile))
                {
                    VssClientHttpRequestSettings.Default.ClientCertificateManager = new AgentClientCertificateManager(certSetting.ClientCertificateArchiveFile, certSetting.ClientCertificatePassword);
                }

                if (certSetting.SkipServerCertificateValidation)
                {
                    VssClientHttpRequestSettings.Default.ServerCertificateValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
            }

            var proxySetting = GetProxyConfiguration();
            if (proxySetting != null)
            {
                if (!string.IsNullOrEmpty(proxySetting.ProxyAddress))
                {
                    VssHttpMessageHandler.DefaultWebProxy = new AgentWebProxy(proxySetting.ProxyAddress, proxySetting.ProxyUsername, proxySetting.ProxyPassword, proxySetting.ProxyBypassList);
                }
            }

            ServiceEndpoint systemConnection = this.Endpoints.FirstOrDefault(e => string.Equals(e.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            ArgUtil.NotNull(systemConnection, nameof(systemConnection));
            ArgUtil.NotNull(systemConnection.Url, nameof(systemConnection.Url));

            VssCredentials credentials = VssUtil.GetVssCredential(systemConnection);
            ArgUtil.NotNull(credentials, nameof(credentials));
            return VssUtil.CreateConnection(systemConnection.Url, credentials);
        }

        public string GetInput(string name, bool required = false)
        {
            string value = null;
            if (this.Inputs.ContainsKey(name))
            {
                value = this.Inputs[name];
            }

            if (string.IsNullOrEmpty(value) && required)
            {
                throw new ArgumentNullException(name);
            }

            return value;
        }

        public void Info(string message)
        {
            ArgUtil.NotNull(message, nameof(message));
            Debug(message);
        }

        public void Verbose(string message)
        {
            ArgUtil.NotNull(message, nameof(message));
#if DEBUG
            Debug(message);
#else
            string vstsAgentTrace = AgentKnobs.TraceVerbose.GetValue(UtilKnobValueContext.Instance()).AsString();
            if (!string.IsNullOrEmpty(vstsAgentTrace))
            {
                Debug(message);
            }
#endif
        }

        public void Error(string message)
        {
            ArgUtil.NotNull(message, nameof(message));
            Output($"##vso[task.logissue type=error;]{Escape(message)}");
            Output(_failTaskCommand);
        }

        public void Debug(string message)
        {
            ArgUtil.NotNull(message, nameof(message));
            Output($"##vso[task.debug]{Escape(message)}");
        }

        public void Warning(string message)
        {
            ArgUtil.NotNull(message, nameof(message));
            Output($"##vso[task.logissue type=warning;]{Escape(message)}");
        }

        public void PublishTelemetry(string area, string feature, Dictionary<string, string> properties)
        {
            ArgUtil.NotNull(area, nameof(area));
            ArgUtil.NotNull(feature, nameof(feature));
            ArgUtil.NotNull(properties, nameof(properties));
            string propertiesAsJson = StringUtil.ConvertToJson(properties, Formatting.None);
            Output($"##vso[telemetry.publish area={area};feature={feature}]{Escape(propertiesAsJson)}");
        }

        public void PublishTelemetry(string area, string feature, Dictionary<string, object> properties)
        {
            ArgUtil.NotNull(area, nameof(area));
            ArgUtil.NotNull(feature, nameof(feature));
            ArgUtil.NotNull(properties, nameof(properties));
            string propertiesAsJson = StringUtil.ConvertToJson(properties, Formatting.None);
            Output($"##vso[telemetry.publish area={area};feature={feature}]{Escape(propertiesAsJson)}");
        }

        public void PublishTelemetry(string area, string feature, TelemetryRecord record)
            => PublishTelemetry(area, feature, record?.GetAssignedProperties());

        public void Output(string message)
        {
            ArgUtil.NotNull(message, nameof(message));
            lock (_stdoutLock)
            {
                if (_trace == null)
                {
                    Console.WriteLine(message);
                }
                else
                {
                    _trace.Info(message);
                }
            }
        }

        public bool IsSystemDebugTrue()
        {
             if (Variables.TryGetValue("system.debug", out VariableValue systemDebugVar))
            {
                return string.Equals(systemDebugVar?.Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public void PrependPath(string directory)
        {
            ArgUtil.NotNull(directory, nameof(directory));
            PathUtil.PrependPath(directory);
            Output($"##vso[task.prependpath]{Escape(directory)}");
        }

        public void Progress(int progress, string operation)
        {
            ArgUtil.NotNull(operation, nameof(operation));
            if (progress < 0 || progress > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(progress));
            }

            Output($"##vso[task.setprogress value={progress}]{Escape(operation)}");
        }

        public void SetSecret(string secret)
        {
            ArgUtil.NotNull(secret, nameof(secret));
            Output($"##vso[task.setsecret]{Escape(secret)}");
        }

        public void SetVariable(string variable, string value, bool isSecret = false)
        {
            ArgUtil.NotNull(variable, nameof(variable));
            ArgUtil.NotNull(value, nameof(value));
            this.Variables[variable] = new VariableValue(value, isSecret);
            Output($"##vso[task.setvariable variable={Escape(variable)};issecret={isSecret.ToString()};]{Escape(value)}");
        }

        public void SetTaskVariable(string variable, string value, bool isSecret = false)
        {
            ArgUtil.NotNull(variable, nameof(variable));
            ArgUtil.NotNull(value, nameof(value));
            this.TaskVariables[variable] = new VariableValue(value, isSecret);
            Output($"##vso[task.settaskvariable variable={Escape(variable)};issecret={isSecret.ToString()};]{Escape(value)}");
        }

        public void Command(string command)
        {
            ArgUtil.NotNull(command, nameof(command));
            Output($"##[command]{Escape(command)}");
        }

        public void UpdateRepositoryPath(string alias, string path)
        {
            ArgUtil.NotNull(alias, nameof(alias));
            ArgUtil.NotNull(path, nameof(path));
            Output($"##vso[plugininternal.updaterepositorypath alias={Escape(alias)};]{path}");
        }

        public AgentCertificateSettings GetCertConfiguration()
        {
            bool skipCertValidation = StringUtil.ConvertToBoolean(this.Variables.GetValueOrDefault("Agent.SkipCertValidation")?.Value);
            string caFile = this.Variables.GetValueOrDefault("Agent.CAInfo")?.Value;
            string clientCertFile = this.Variables.GetValueOrDefault("Agent.ClientCert")?.Value;

            if (!string.IsNullOrEmpty(caFile) || !string.IsNullOrEmpty(clientCertFile) || skipCertValidation)
            {
                var certConfig = new AgentCertificateSettings();
                certConfig.SkipServerCertificateValidation = skipCertValidation;
                certConfig.CACertificateFile = caFile;

                if (!string.IsNullOrEmpty(clientCertFile))
                {
                    certConfig.ClientCertificateFile = clientCertFile;
                    string clientCertKey = this.Variables.GetValueOrDefault("Agent.ClientCertKey")?.Value;
                    string clientCertArchive = this.Variables.GetValueOrDefault("Agent.ClientCertArchive")?.Value;
                    string clientCertPassword = this.Variables.GetValueOrDefault("Agent.ClientCertPassword")?.Value;

                    certConfig.ClientCertificatePrivateKeyFile = clientCertKey;
                    certConfig.ClientCertificateArchiveFile = clientCertArchive;
                    certConfig.ClientCertificatePassword = clientCertPassword;

                    certConfig.VssClientCertificateManager = new AgentClientCertificateManager(clientCertArchive, clientCertPassword);
                }

                return certConfig;
            }
            else
            {
                return null;
            }
        }

        public AgentWebProxySettings GetProxyConfiguration()
        {
            string proxyUrl = this.Variables.GetValueOrDefault("Agent.ProxyUrl")?.Value;
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                string proxyUsername = this.Variables.GetValueOrDefault("Agent.ProxyUsername")?.Value;
                string proxyPassword = this.Variables.GetValueOrDefault("Agent.ProxyPassword")?.Value;
                List<string> proxyBypassHosts = StringUtil.ConvertFromJson<List<string>>(this.Variables.GetValueOrDefault("Agent.ProxyBypassList")?.Value ?? "[]");
                return new AgentWebProxySettings()
                {
                    ProxyAddress = proxyUrl,
                    ProxyUsername = proxyUsername,
                    ProxyPassword = proxyPassword,
                    ProxyBypassList = proxyBypassHosts,
                    WebProxy = new AgentWebProxy(proxyUrl, proxyUsername, proxyPassword, proxyBypassHosts)
                };
            }
            // back-compat of proxy configuration via environment variables
            else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSTS_HTTP_PROXY")))
            {
                var ProxyUrl = Environment.GetEnvironmentVariable("VSTS_HTTP_PROXY");
                ProxyUrl = ProxyUrl.Trim();
                var ProxyUsername = Environment.GetEnvironmentVariable("VSTS_HTTP_PROXY_USERNAME");
                var ProxyPassword = Environment.GetEnvironmentVariable("VSTS_HTTP_PROXY_PASSWORD");
                return new AgentWebProxySettings()
                {
                    ProxyAddress = ProxyUrl,
                    ProxyUsername = ProxyUsername,
                    ProxyPassword = ProxyPassword,
                    WebProxy = new AgentWebProxy(proxyUrl, ProxyUsername, ProxyPassword, null)
                };
            }
            else
            {
                return null;
            }
        }

        private string Escape(string input)
        {
            if (AgentKnobs.DecodePercents.GetValue(this).AsBoolean())
            {
                input = input.Replace("%", "%AZP25");
            }
            foreach (var mapping in _commandEscapeMappings)
            {
                input = input.Replace(mapping.Key, mapping.Value);
            }

            return input;
        }

        string IKnobValueContext.GetVariableValueOrDefault(string variableName)
        {
            return Variables.GetValueOrDefault(variableName)?.Value;
        }

        IScopedEnvironment IKnobValueContext.GetScopedEnvironment()
        {
            return new SystemEnvironment();
        }

        private Dictionary<string, string> _commandEscapeMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {
                ";", "%3B"
            },
            {
                "\r", "%0D"
            },
            {
                "\n", "%0A"
            },
            {
                "]", "%5D"
            },
        };
    }
}

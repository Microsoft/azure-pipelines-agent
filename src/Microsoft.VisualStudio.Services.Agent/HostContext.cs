// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Http;
using System.Diagnostics.Tracing;
using Microsoft.TeamFoundation.DistributedTask.Logging;
using System.Net.Http.Headers;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent
{
    public interface IHostContext : IDisposable, IKnobValueContext
    {
        StartupType StartupType { get; set; }
        CancellationToken AgentShutdownToken { get; }
        ShutdownReason AgentShutdownReason { get; }
        ISecretMasker SecretMasker { get; }
        ProductInfoHeaderValue UserAgent { get; }
        string GetDirectory(WellKnownDirectory directory);
        string GetConfigFile(WellKnownConfigFile configFile);
        Tracing GetTrace(string name);
        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
        T CreateService<T>() where T : class, IAgentService;
        T GetService<T>() where T : class, IAgentService;
        void SetDefaultCulture(string name);
        event EventHandler Unloading;
        void ShutdownAgent(ShutdownReason reason);
        void WritePerfCounter(string counter);
        ContainerInfo CreateContainerInfo(Pipelines.ContainerResource container, Boolean isJobContainer = true);
    }

    public enum StartupType
    {
        Manual,
        Service,
        AutoStartup
    }

    public class HostContext : EventListener, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>>, IHostContext
    {
        private const int _defaultLogPageSize = 8;  //MB

        private static int _defaultLogRetentionDays = 30;
        private static int[] _vssHttpMethodEventIds = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 24 };
        private static int[] _vssHttpCredentialEventIds = new int[] { 11, 13, 14, 15, 16, 17, 18, 20, 21, 22, 27, 29 };
        private readonly ConcurrentDictionary<Type, object> _serviceInstances = new ConcurrentDictionary<Type, object>();
        protected readonly ConcurrentDictionary<Type, Type> ServiceTypes = new ConcurrentDictionary<Type, Type>();
        private readonly ISecretMasker _secretMasker = new SecretMasker();
        private readonly ProductInfoHeaderValue _userAgent = new ProductInfoHeaderValue($"VstsAgentCore-{BuildConstants.AgentPackage.PackageName}", BuildConstants.AgentPackage.Version);
        private CancellationTokenSource _agentShutdownTokenSource = new CancellationTokenSource();
        private object _perfLock = new object();
        private Tracing _trace;
        private Tracing _vssTrace;
        private Tracing _httpTrace;
        private ITraceManager _traceManager;
        private AssemblyLoadContext _loadContext;
        private IDisposable _httpTraceSubscription;
        private IDisposable _diagListenerSubscription;
        private StartupType _startupType;
        private string _perfFile;
        public event EventHandler Unloading;
        public CancellationToken AgentShutdownToken => _agentShutdownTokenSource.Token;
        public ShutdownReason AgentShutdownReason { get; private set; }
        public ISecretMasker SecretMasker => _secretMasker;
        public ProductInfoHeaderValue UserAgent => _userAgent;
        public HostContext(string hostType, string logFile = null)
        {
            // Validate args.
            ArgUtil.NotNullOrEmpty(hostType, nameof(hostType));

            _loadContext = AssemblyLoadContext.GetLoadContext(typeof(HostContext).GetTypeInfo().Assembly);
            _loadContext.Unloading += LoadContext_Unloading;

            this.SecretMasker.AddValueEncoder(ValueEncoders.JsonStringEscape);
            this.SecretMasker.AddValueEncoder(ValueEncoders.UriDataEscape);
            this.SecretMasker.AddValueEncoder(ValueEncoders.BackslashEscape);
            this.SecretMasker.AddRegex(AdditionalMaskingRegexes.UrlSecretPattern);
            if (AgentKnobs.MaskUsingCredScanRegexes.GetValue(this).AsBoolean())
            {
                foreach (var pattern in AdditionalMaskingRegexes.CredScanPatterns)
                {
                    this.SecretMasker.AddRegex(pattern);
                }
            }

            // Create the trace manager.
            if (string.IsNullOrEmpty(logFile))
            {
                int logPageSize;
                string logSizeEnv = Environment.GetEnvironmentVariable($"{hostType.ToUpperInvariant()}_LOGSIZE");
                if (!string.IsNullOrEmpty(logSizeEnv) || !int.TryParse(logSizeEnv, out logPageSize))
                {
                    logPageSize = _defaultLogPageSize;
                }

                int logRetentionDays;
                string logRetentionDaysEnv = Environment.GetEnvironmentVariable($"{hostType.ToUpperInvariant()}_LOGRETENTION");
                if (!string.IsNullOrEmpty(logRetentionDaysEnv) || !int.TryParse(logRetentionDaysEnv, out logRetentionDays))
                {
                    logRetentionDays = _defaultLogRetentionDays;
                }

                // this should give us _diag folder under agent root directory
                string diagLogDirectory = Path.Combine(new DirectoryInfo(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)).Parent.FullName, Constants.Path.DiagDirectory);
                _traceManager = new TraceManager(new HostTraceListener(diagLogDirectory, hostType, logPageSize, logRetentionDays), this.SecretMasker);
            }
            else
            {
                _traceManager = new TraceManager(new HostTraceListener(logFile), this.SecretMasker);
            }

            _trace = GetTrace(nameof(HostContext));
            _vssTrace = GetTrace(nameof(VisualStudio) + nameof(VisualStudio.Services));  // VisualStudioService

            // Enable Http trace
            if (AgentKnobs.HttpTrace.GetValue(this).AsBoolean())
            {
                _trace.Warning("*****************************************************************************************");
                _trace.Warning("**                                                                                     **");
                _trace.Warning("** Http trace is enabled, all your http traffic will be dumped into agent diag log.    **");
                _trace.Warning("** DO NOT share the log in public place! The trace may contains secrets in plain text. **");
                _trace.Warning("**                                                                                     **");
                _trace.Warning("*****************************************************************************************");

                _httpTrace = GetTrace("HttpTrace");
                _diagListenerSubscription = DiagnosticListener.AllListeners.Subscribe(this);
            }

            // Enable perf counter trace
            string perfCounterLocation = AgentKnobs.AgentPerflog.GetValue(this).AsString();
            if (!string.IsNullOrEmpty(perfCounterLocation))
            {
                try
                {
                    Directory.CreateDirectory(perfCounterLocation);
                    _perfFile = Path.Combine(perfCounterLocation, $"{hostType}.perf");
                }
                catch (Exception ex)
                {
                    _trace.Error(ex);
                }
            }
        }

        public virtual string GetDirectory(WellKnownDirectory directory)
        {
            string path;
            switch (directory)
            {
                case WellKnownDirectory.Bin:
                    path = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    break;

                case WellKnownDirectory.Diag:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        Constants.Path.DiagDirectory);
                    break;

                case WellKnownDirectory.Externals:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        Constants.Path.ExternalsDirectory);
                    break;

                case WellKnownDirectory.LegacyPSHost:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.LegacyPSHostDirectory);
                    break;

                case WellKnownDirectory.Root:
                    path = new DirectoryInfo(GetDirectory(WellKnownDirectory.Bin)).Parent.FullName;
                    break;

                case WellKnownDirectory.ServerOM:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.ServerOMDirectory);
                    break;

                case WellKnownDirectory.Tf:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.TfDirectory);
                    break;

                case WellKnownDirectory.Tee:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Externals),
                        Constants.Path.TeeDirectory);
                    break;

                case WellKnownDirectory.Temp:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.TempDirectory);
                    break;

                case WellKnownDirectory.Tasks:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.TasksDirectory);
                    break;

                case WellKnownDirectory.TaskZips:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.TaskZipsDirectory);
                    break;

                case WellKnownDirectory.Tools:
                    path = AgentKnobs.AgentToolsDirectory.GetValue(this).AsString();
                    if (string.IsNullOrEmpty(path))
                    {
                        path = Path.Combine(
                            GetDirectory(WellKnownDirectory.Work),
                            Constants.Path.ToolDirectory);
                    }
                    break;

                case WellKnownDirectory.Update:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Work),
                        Constants.Path.UpdateDirectory);
                    break;

                case WellKnownDirectory.Work:
                    var configurationStore = GetService<IConfigurationStore>();
                    AgentSettings settings = configurationStore.GetSettings();
                    ArgUtil.NotNull(settings, nameof(settings));
                    ArgUtil.NotNullOrEmpty(settings.WorkFolder, nameof(settings.WorkFolder));
                    path = Path.GetFullPath(Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        settings.WorkFolder));
                    break;

                default:
                    throw new NotSupportedException($"Unexpected well known directory: '{directory}'");
            }

            _trace.Info($"Well known directory '{directory}': '{path}'");
            return path;
        }

        public string GetConfigFile(WellKnownConfigFile configFile)
        {
            string path;
            switch (configFile)
            {
                case WellKnownConfigFile.Agent:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".agent");
                    break;

                case WellKnownConfigFile.Credentials:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".credentials");
                    break;

                case WellKnownConfigFile.RSACredentials:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".credentials_rsaparams");
                    break;

                case WellKnownConfigFile.Service:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".service");
                    break;

                case WellKnownConfigFile.CredentialStore:
                    if (PlatformUtil.RunningOnMacOS)
                    {
                        path = Path.Combine(
                            GetDirectory(WellKnownDirectory.Root),
                            ".credential_store.keychain");
                    }
                    else
                    {
                        path = Path.Combine(
                            GetDirectory(WellKnownDirectory.Root),
                            ".credential_store");
                    }
                    break;

                case WellKnownConfigFile.Certificates:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".certificates");
                    break;

                case WellKnownConfigFile.Proxy:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".proxy");
                    break;

                case WellKnownConfigFile.ProxyCredentials:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".proxycredentials");
                    break;

                case WellKnownConfigFile.ProxyBypass:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".proxybypass");
                    break;

                case WellKnownConfigFile.Autologon:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".autologon");
                    break;

                case WellKnownConfigFile.Options:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".options");
                    break;

                case WellKnownConfigFile.SetupInfo:
                    path = Path.Combine(
                        GetDirectory(WellKnownDirectory.Root),
                        ".setup_info");
                    break;

                default:
                    throw new NotSupportedException($"Unexpected well known config file: '{configFile}'");
            }

            _trace.Info($"Well known config file '{configFile}': '{path}'");
            return path;
        }

        public Tracing GetTrace(string name)
        {
            return _traceManager[name];
        }

        public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
        }

        /// <summary>
        /// Creates a new instance of T.
        /// </summary>
        public T CreateService<T>() where T : class, IAgentService
        {
            Type target = null;
            Type defaultTarget = null;
            Type platformTarget = null;

            if (!ServiceTypes.TryGetValue(typeof(T), out target))
            {
                // Infer the concrete type from the ServiceLocatorAttribute.
                CustomAttributeData attribute = typeof(T)
                    .GetTypeInfo()
                    .CustomAttributes
                    .FirstOrDefault(x => x.AttributeType == typeof(ServiceLocatorAttribute));
                if (!(attribute is null))
                {
                    foreach (CustomAttributeNamedArgument arg in attribute.NamedArguments)
                    {
                        if (string.Equals(arg.MemberName, nameof(ServiceLocatorAttribute.Default), StringComparison.Ordinal))
                        {
                            defaultTarget = arg.TypedValue.Value as Type;
                        }

                        if (PlatformUtil.RunningOnWindows
                            && string.Equals(arg.MemberName, nameof(ServiceLocatorAttribute.PreferredOnWindows), StringComparison.Ordinal))
                        {
                            platformTarget = arg.TypedValue.Value as Type;
                        }
                        else if (PlatformUtil.RunningOnMacOS
                            && string.Equals(arg.MemberName, nameof(ServiceLocatorAttribute.PreferredOnMacOS), StringComparison.Ordinal))
                        {
                            platformTarget = arg.TypedValue.Value as Type;
                        }
                        else if ((PlatformUtil.RunningOnLinux || PlatformUtil.RunningOnFreeBSD)
                            && string.Equals(arg.MemberName, nameof(ServiceLocatorAttribute.PreferredOnLinux), StringComparison.Ordinal))
                        {
                            platformTarget = arg.TypedValue.Value as Type;
                        }
                    }
                }

                target = platformTarget ?? defaultTarget;

                if (target is null)
                {
                    throw new KeyNotFoundException(string.Format(CultureInfo.InvariantCulture, "Service mapping not found for key '{0}'.", typeof(T).FullName));
                }

                ServiceTypes.TryAdd(typeof(T), target);
                target = ServiceTypes[typeof(T)];
            }

            // Create a new instance.
            T svc = Activator.CreateInstance(target) as T;
            svc.Initialize(this);
            return svc;
        }

        /// <summary>
        /// Gets or creates an instance of T.
        /// </summary>
        public T GetService<T>() where T : class, IAgentService
        {
            // Return the cached instance if one already exists.
            object instance;
            if (_serviceInstances.TryGetValue(typeof(T), out instance))
            {
                return instance as T;
            }

            // Otherwise create a new instance and try to add it to the cache.
            _serviceInstances.TryAdd(typeof(T), CreateService<T>());

            // Return the instance from the cache.
            return _serviceInstances[typeof(T)] as T;
        }

        public void SetDefaultCulture(string name)
        {
            ArgUtil.NotNull(name, nameof(name));
            _trace.Verbose($"Setting default culture and UI culture to: '{name}'");
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(name);
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(name);
        }


        public void ShutdownAgent(ShutdownReason reason)
        {
            ArgUtil.NotNull(reason, nameof(reason));
            _trace.Info($"Agent will be shutdown for {reason.ToString()}");
            AgentShutdownReason = reason;
            _agentShutdownTokenSource.Cancel();
        }

        public ContainerInfo CreateContainerInfo(Pipelines.ContainerResource container, Boolean isJobContainer = true)
        {
            ContainerInfo containerInfo = new ContainerInfo(container, isJobContainer);
            Dictionary<string, string> pathMappings = new Dictionary<string, string>();
            if (PlatformUtil.RunningOnWindows)
            {
                pathMappings[this.GetDirectory(WellKnownDirectory.Tools)] = "C:\\__t"; // Tool cache folder may come from ENV, so we need a unique folder to avoid collision
                pathMappings[this.GetDirectory(WellKnownDirectory.Work)] = "C:\\__w";
                pathMappings[this.GetDirectory(WellKnownDirectory.Root)] = "C:\\__a";
                // add -v '\\.\pipe\docker_engine:\\.\pipe\docker_engine' when they are available (17.09)
            }
            else
            {
                pathMappings[this.GetDirectory(WellKnownDirectory.Tools)] = "/__t"; // Tool cache folder may come from ENV, so we need a unique folder to avoid collision
                pathMappings[this.GetDirectory(WellKnownDirectory.Work)] = "/__w";
                pathMappings[this.GetDirectory(WellKnownDirectory.Root)] = "/__a";
            }

            if (containerInfo.IsJobContainer && containerInfo.MapDockerSocket)
            {
                containerInfo.MountVolumes.Add(new MountVolume("/var/run/docker.sock", "/var/run/docker.sock"));
            }

            containerInfo.AddPathMappings(pathMappings);
            return containerInfo;
        }

        public sealed override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public StartupType StartupType
        {
            get
            {
                return _startupType;
            }
            set
            {
                _startupType = value;
            }
        }

        public void WritePerfCounter(string counter)
        {
            ArgUtil.NotNull(counter, nameof(counter));
            if (!string.IsNullOrEmpty(_perfFile))
            {
                string normalizedCounter = counter.Replace(':', '_');
                lock (_perfLock)
                {
                    try
                    {
                        File.AppendAllLines(_perfFile, new[] { $"{normalizedCounter}:{DateTime.UtcNow.ToString("O")}" });
                    }
                    catch (Exception ex)
                    {
                        _trace.Error(ex);
                    }
                }
            }
        }

        string IKnobValueContext.GetVariableValueOrDefault(string variableName)
        {
            throw new NotSupportedException("Method not supported for Microsoft.VisualStudio.Services.Agent.HostContext");
        }

        IScopedEnvironment IKnobValueContext.GetScopedEnvironment()
        {
            return new SystemEnvironment();
        }

        protected virtual void Dispose(bool disposing)
        {
            // TODO: Dispose the trace listener also.
            if (disposing)
            {
                if (_loadContext != null)
                {
                    _loadContext.Unloading -= LoadContext_Unloading;
                    _loadContext = null;
                }
                _httpTraceSubscription?.Dispose();
                _diagListenerSubscription?.Dispose();
                _traceManager?.Dispose();
                _traceManager = null;
                _vssTrace?.Dispose();
                _vssTrace = null;
                _trace?.Dispose();
                _trace = null;
                _httpTrace?.Dispose();
                _httpTrace = null;

                _agentShutdownTokenSource?.Dispose();
                _agentShutdownTokenSource = null;

                base.Dispose();
            }
        }

        private void LoadContext_Unloading(AssemblyLoadContext obj)
        {
            if (Unloading != null)
            {
                Unloading(this, null);
            }
        }

        void IObserver<DiagnosticListener>.OnCompleted()
        {
            _httpTrace.Info("DiagListeners finished transmitting data.");
        }

        void IObserver<DiagnosticListener>.OnError(Exception error)
        {
            _httpTrace.Error(error);
        }

        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener" && _httpTraceSubscription == null)
            {
                _httpTraceSubscription = listener.Subscribe(this);
            }
        }

        void IObserver<KeyValuePair<string, object>>.OnCompleted()
        {
            _httpTrace.Info("HttpHandlerDiagnosticListener finished transmitting data.");
        }

        void IObserver<KeyValuePair<string, object>>.OnError(Exception error)
        {
            _httpTrace.Error(error);
        }

        void IObserver<KeyValuePair<string, object>>.OnNext(KeyValuePair<string, object> value)
        {
            _httpTrace.Info($"Trace {value.Key} event:{Environment.NewLine}{value.Value.ToString()}");
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            ArgUtil.NotNull(source, nameof(source));
            if (source.Name.Equals("Microsoft-VSS-Http"))
            {
                EnableEvents(source, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData == null)
            {
                return;
            }

            string message = eventData.Message;
            object[] payload = new object[0];
            if (eventData.Payload != null && eventData.Payload.Count > 0)
            {
                payload = eventData.Payload.ToArray();
            }

            try
            {
                if (_vssHttpMethodEventIds.Contains(eventData.EventId))
                {
                    payload[0] = Enum.Parse(typeof(VssHttpMethod), ((int)payload[0]).ToString());
                }
                else if (_vssHttpCredentialEventIds.Contains(eventData.EventId))
                {
                    payload[0] = Enum.Parse(typeof(VisualStudio.Services.Common.VssCredentialsType), ((int)payload[0]).ToString());
                }

                if (payload.Length > 0)
                {
                    message = String.Format(eventData.Message.Replace("%n", Environment.NewLine), payload);
                }

                switch (eventData.Level)
                {
                    case EventLevel.Critical:
                    case EventLevel.Error:
                        _vssTrace.Error(message);
                        break;
                    case EventLevel.Warning:
                        _vssTrace.Warning(message);
                        break;
                    case EventLevel.Informational:
                        _vssTrace.Info(message);
                        break;
                    default:
                        _vssTrace.Verbose(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                _vssTrace.Error(ex);
                _vssTrace.Info(eventData.Message);
                _vssTrace.Info(string.Join(", ", eventData.Payload?.ToArray() ?? new string[0]));
            }
        }

        // Copied from VSTS code base, used for EventData translation.
        internal enum VssHttpMethod
        {
            UNKNOWN,
            DELETE,
            HEAD,
            GET,
            OPTIONS,
            PATCH,
            POST,
            PUT,
        }
    }

    public static class HostContextExtension
    {
        public static HttpClientHandler CreateHttpClientHandler(this IHostContext context)
        {
            ArgUtil.NotNull(context, nameof(context));
            HttpClientHandler clientHandler = new HttpClientHandler();
            var agentWebProxy = context.GetService<IVstsAgentWebProxy>();
            clientHandler.Proxy = agentWebProxy.WebProxy;

            var agentCertManager = context.GetService<IAgentCertificateManager>();
            if (agentCertManager.SkipServerCertificateValidation)
            {
                clientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            return clientHandler;
        }
    }

    public enum ShutdownReason
    {
        UserCancelled = 0,
        OperatingSystemShutdown = 1,
    }
}
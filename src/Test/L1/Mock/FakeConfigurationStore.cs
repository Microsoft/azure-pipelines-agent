using System.IO;
using System.Reflection;

namespace Microsoft.VisualStudio.Services.Agent.Tests.L1.Worker
{
    public class FakeConfigurationStore : IConfigurationStore
    {
        public void Initialize(IHostContext hostContext)
        {
            RootFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }
        public string RootFolder { get; internal set; }
        public bool IsConfigured()
        {
            return true;
        }
        public bool IsServiceConfigured()
        {
            return true;
        }
        public bool IsAutoLogonConfigured()
        {
            return true;
        }
        public bool HasCredentials()
        {
            return true;
        }
        public CredentialData GetCredentials()
        {
            return null;
        }
        public AgentSettings GetSettings()
        {
            return new AgentSettings
            {
                AgentName = "TestAgent",
                WorkFolder = RootFolder + "/w"
            };
        }
        public void SaveCredential(CredentialData credential)
        {
        }
        public void SaveSettings(AgentSettings settings)
        {
        }
        public void DeleteCredential()
        {
        }
        public void DeleteSettings()
        {
        }
        public void DeleteAutoLogonSettings()
        {
        }
        public void SaveAutoLogonSettings(AutoLogonSettings settings)
        {
        }
        public AutoLogonSettings GetAutoLogonSettings()
        {
            return null;
        }
        public AgentRuntimeOptions GetAgentRuntimeOptions()
        {
            return null;
        }
        public void SaveAgentRuntimeOptions(AgentRuntimeOptions options)
        {
        }
        public void DeleteAgentRuntimeOptions()
        {
        }
    }
}
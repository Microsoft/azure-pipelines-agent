// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(DaemonControlManager))]
    public interface IFreeBSDServiceControlManager : IAgentService
    {
        void GenerateScripts(AgentSettings settings);
    }

    public class DaemonControlManager : ServiceControlManager, IFreeBSDServiceControlManager
    {
        private const string _svcNamePattern = "vsts.agent.{0}.{1}.{2}.service";
        private const string _svcDisplayPattern = "Azure Pipelines Agent ({0}.{1}.{2})";
        private const string _shTemplate = "daemon.svc.sh.template";
        private const string _shName = "svc.sh";

        public void GenerateScripts(AgentSettings settings)
        {
            try
            {
                string serviceName;
                string serviceDisplayName;
                CalculateServiceName(settings, _svcNamePattern, _svcDisplayPattern, out serviceName, out serviceDisplayName);

                string svcShPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Root), _shName);

                string svcShContent = File.ReadAllText(Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), _shTemplate));
                var tokensToReplace = new Dictionary<string, string>
                                          {
                                              { "{{SvcDescription}}", serviceDisplayName },
                                              { "{{SvcNameVar}}", serviceName }
                                          };

                svcShContent = tokensToReplace.Aggregate(
                    svcShContent,
                    (current, item) => current.Replace(item.Key, item.Value));

                File.WriteAllText(svcShPath, svcShContent, new UTF8Encoding(false));

                var unixUtil = HostContext.CreateService<IUnixUtil>();
                unixUtil.ChmodAsync("755", svcShPath).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.Error(ex);
                throw;
            }
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Collections.Generic;

namespace Agent.Sdk
{
    public interface IBaseExecutionContext : ITraceWriter
    {
        string GetVariableValueOrDefault(string variableName);
        IEnumerable<KeyValuePair<string, string>> EnumeratePublicVariables();
        void PublishTelemetry(string area, string feature, Dictionary<string, string> properties);
        void PrependPath(string directory);
        string GetInput(string name, bool required = false);
    }
}

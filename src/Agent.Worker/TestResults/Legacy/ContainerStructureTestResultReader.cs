// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using TestRunContext = Microsoft.TeamFoundation.TestClient.PublishTestResults.TestRunContext;

namespace Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults
{
    /// <summary>
    /// Reads result output from google container strucutre test.
    /// https://github.com/GoogleContainerTools/container-structure-test
    /// Example JSON: https://gist.github.com/navin22/30edd4041f5eb14b0d860ee07fdc2184
    /// </summary>
    public class ContainerStructureTestResultReader : AgentService, IResultReader
    {
        public bool AddResultsFileToRunLevelAttachments { get; set; }

        public string Name => "ContainerStructure";

        public Type ExtensionType => typeof(IResultReader);

        public ContainerStructureTestResultReader()
        {
            AddResultsFileToRunLevelAttachments = true;
        }

        public TestRunData ReadResults(IExecutionContext executionContext, string filePath, TestRunContext runContext, bool isParallelProcessingFFEnabled = false)
        {
            try
            {
                string jsonTestSummary = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(jsonTestSummary))
                {
                    return null;
                }

                JsonTestSummary testSummary = StringUtil.ConvertFromJson<JsonTestSummary>(jsonTestSummary);

                // Adding the minimum details from the JSON.
                TestRunData testRunData = new TestRunData(name: "Container Structure Test",
                    isAutomated: true,
                    buildId: runContext != null ? runContext.BuildId : 0,
                    buildFlavor: runContext != null ? runContext.Configuration : string.Empty,
                    buildPlatform: runContext != null ? runContext.Platform : string.Empty,
                    releaseUri: runContext != null ? runContext.ReleaseUri : null,
                    releaseEnvironmentUri: runContext != null ? runContext.ReleaseEnvironmentUri : null);

                List<TestCaseResultData> results = new List<TestCaseResultData>();

                foreach (JsonTestResult result in testSummary.Results)
                {
                    TestCaseResultData resultCreateModel = new TestCaseResultData();
                    resultCreateModel.TestCaseTitle = result.Name;
                    resultCreateModel.AutomatedTestName = result.Name;
                    bool outcome = result.Pass.Equals("true", StringComparison.OrdinalIgnoreCase);

                    if (!outcome)
                    {
                        resultCreateModel.ErrorMessage = string.Join("|", result.Errors);
                    }

                    resultCreateModel.State = "Completed";
                    resultCreateModel.AutomatedTestType = Name;
                    resultCreateModel.Outcome = outcome ? TestOutcome.Passed.ToString() : TestOutcome.Failed.ToString();
                    results.Add(resultCreateModel);
                }

                testRunData.Results = results.ToArray();

                return testRunData;
            }
            catch (Exception ex)
            {
                executionContext.Output("Error occured in reading results : " + ex);
            }
            return null;
        }
    }

    public class JsonTestSummary
    {
        public string Name { get; set; }
        public int Pass { get; set; }
        public int Fail { get; set; }
        public JsonTestResult[] Results { get; set; }
        public JsonTestSummary()
        {
        }

    }

    public class JsonTestResult
    {
        public string Name { get; set; }
        public string Pass { get; set; }
        public string[] Errors { get; set; }
        public JsonTestResult()
        {
        }
    }
}
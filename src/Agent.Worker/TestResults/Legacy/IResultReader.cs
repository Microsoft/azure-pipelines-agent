// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using TestRunContext = Microsoft.TeamFoundation.TestClient.PublishTestResults.TestRunContext;

namespace Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults
{
    public interface IResultReader : IExtension
    {
        /// <summary>
        /// Reads a test results file from disk, converts it into a TestCaseResultData array   
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <returns>TestCaseResultData Array</returns>
        TestRunData ReadResults(IExecutionContext executionContext, string filePath, TestRunContext runContext, bool isParallelProcessingFFEnabled = false);

        /// <summary>
        /// Should the run level attachments be uploaded
        /// </summary>
        bool AddResultsFileToRunLevelAttachments { get; set; }

        /// <summary>
        /// Result reader name
        /// </summary>
        string Name { get; }

    }
}
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Agent.Worker.TestResults.Utils;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestRunContext = Microsoft.TeamFoundation.TestClient.PublishTestResults.TestRunContext;

namespace Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults
{
    [ServiceLocator(Default = typeof(TestRunPublisher))]
    public interface ITestRunPublisher : IAgentService
    {
        void InitializePublisher(IExecutionContext executionContext, VssConnection connection, string projectName, IResultReader resultReader, IFeatureFlagService featureFlagService = null);
        Task<TestRun> StartTestRunAsync(TestRunData testRunData, CancellationToken cancellationToken = default(CancellationToken));
        Task AddResultsAsync(TestRun testRun, TestCaseResultData[] testResults, CancellationToken cancellationToken = default(CancellationToken));
        Task<TestRun> EndTestRunAsync(TestRunData testRunData, int testRunId, bool publishAttachmentsAsArchive = false, CancellationToken cancellationToken = default(CancellationToken));
        TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath, string runName);
        TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath);
        /// <summary>
        /// This method returns whether there are any failures in run or not.
        /// It takes into account of flaky results also.
        /// </summary>
        /// <param name="projectName">Name of the Project whose run to be queried.</param>
        /// <param name="testRunId">The Id of the TestRun.</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>Returns true if there are failures which are non-flaky</returns>
        bool IsTestRunFailed(string projectName,
            int testRunId,
            CancellationToken cancellationToken);

        /// <summary>
        /// This method infers whether there are any failures in run summary or not.
        /// It takes into account of flaky results also.
        /// </summary>
        /// <param name="testRunStatistic">TestRunStatistic contains summary by outcome information </param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>Returns true if there are failures which are non-flaky</returns>
        bool IsTestRunFailed(TestRunStatistic testRunStatistics,
            CancellationToken cancellationToken);

        /// <summary>
        /// This method returns the test run summary by outcome .
        /// This method used the vstest task to infer the run if it is failed or passed.
        /// </summary>
        /// <param name="projectName">Name of the Project </param>
        /// <param name="testRunId">The Id of the TestRun</param>
        /// <param name="cancellationToken">Cancellation Token.</param>
        /// <returns>test run summary by outcome.</returns>
        /// <exception cref="TestObjectNotFoundException">throws 404 Not found when test run is not in completed state</exception>
        /// <exception cref="TestObjectNotFoundException">throws 404 Not found when test run summary is not ready, and it is recommended to retry after some time. Retry should be after 1 sec</exception>
        Task<TestRunStatistic> GetTestRunSummaryAsync(string projectName,
            int testRunId,
            bool allowRetry = false,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public class TestRunPublisher : AgentService, ITestRunPublisher
    {
        #region Private
        const int BATCH_SIZE = 1000;
        const int PUBLISH_TIMEOUT = 300;
        const int TCM_MAX_FILECONTENT_SIZE = 100 * 1024 * 1024; //100 MB
        const int TCM_MAX_FILESIZE = 75 * 1024 * 1024; // 75 MB
        private IExecutionContext _executionContext;
        private string _projectName;
        private ITestResultsServer _testResultsServer;
        private IResultReader _resultReader;
        private PublisherInputValidator _publisherInputValidator;
        private IFeatureFlagService _featureFlagService = new FeatureFlagService();
        private const int MaxRetries = 3;
        private bool _isParallelProcessingFFEnabled = false;
        #endregion

        #region Public API
        public void InitializePublisher(IExecutionContext executionContext, VssConnection connection, string projectName, IResultReader resultReader, IFeatureFlagService featureFlagService = null)
        {
            ArgUtil.NotNull(connection, nameof(connection));
            Trace.Entering();
            _executionContext = executionContext;
            _projectName = projectName;
            _resultReader = resultReader;
            connection.InnerHandler.Settings.SendTimeout = TimeSpan.FromSeconds(PUBLISH_TIMEOUT);
            _testResultsServer = HostContext.GetService<ITestResultsServer>();
            _testResultsServer.InitializeServer(connection, executionContext);
            _publisherInputValidator = new PublisherInputValidator(_executionContext);
            if(featureFlagService == null)
            {
                _featureFlagService.InitializeFeatureService(executionContext, connection);
                _isParallelProcessingFFEnabled = _featureFlagService.GetFeatureFlagState("TestManagement.Server.JUnitTestCaseParallelReportingEnabledFF", new Guid("00000054-0000-8888-8000-000000000000"));
            }
            else
            {
                featureFlagService.InitializeFeatureService(executionContext, connection);
                _isParallelProcessingFFEnabled = featureFlagService.GetFeatureFlagState("TestManagement.Server.JUnitTestCaseParallelReportingEnabledFF", new Guid("00000054-0000-8888-8000-000000000000"));
            }
            Trace.Leaving();
        }

        /// <summary>
        /// Publishes the given results to the test run.
        /// </summary>
        /// <param name="testResults">Results to be published.</param>
        public async Task AddResultsAsync(TestRun testRun, TestCaseResultData[] testResults, CancellationToken cancellationToken)
        {
            ArgUtil.NotNull(testRun, nameof(testRun));
            ArgUtil.NotNull(testResults, nameof(testResults));
            Trace.Entering();
            int noOfResultsToBePublished = BATCH_SIZE;

            _executionContext.Output(StringUtil.Loc("PublishingTestResults", testRun.Id));

            for (int i = 0; i < testResults.Length; i += BATCH_SIZE)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i + BATCH_SIZE >= testResults.Length)
                {
                    noOfResultsToBePublished = testResults.Length - i;
                }
                _executionContext.Output(StringUtil.Loc("TestResultsRemaining", (testResults.Length - i), testRun.Id));

                var currentBatch = new TestCaseResultData[noOfResultsToBePublished];
                var testResultsBatch = new TestCaseResult[noOfResultsToBePublished];
                Array.Copy(testResults, i, currentBatch, 0, noOfResultsToBePublished);

                for (int testResultsIndex = 0; testResultsIndex < noOfResultsToBePublished; testResultsIndex++)
                {

                    if (IsMaxLimitReachedForSubresultPreProcessing(currentBatch[testResultsIndex].AutomatedTestName, currentBatch[testResultsIndex].TestCaseSubResultData) == false)
                    {
                        _executionContext.Warning(StringUtil.Loc("MaxHierarchyLevelReached", TestManagementConstants.maxHierarchyLevelForSubresults));
                        currentBatch[testResultsIndex].TestCaseSubResultData = null;
                    }
                    testResultsBatch[testResultsIndex] = new TestCaseResult();
                    TestCaseResultDataConverter.Convert(currentBatch[testResultsIndex], testResultsBatch[testResultsIndex]);
                }

                List<TestCaseResult> uploadedTestResults = await _testResultsServer.AddTestResultsToTestRunAsync(testResultsBatch, _projectName, testRun.Id, cancellationToken);
                for (int j = 0; j < noOfResultsToBePublished; j++)
                {
                    await this.UploadTestResultsAttachmentAsync(testRun.Id, testResults[i + j], uploadedTestResults[j], cancellationToken);
                }
            }

            Trace.Leaving();
        }

        /// <summary>
        /// Start a test run
        /// </summary>
        public async Task<TestRun> StartTestRunAsync(TestRunData testRunData, CancellationToken cancellationToken)
        {
            Trace.Entering();
            var testRun = await _testResultsServer.CreateTestRunAsync(_projectName, testRunData, cancellationToken);
            Trace.Leaving();
            return testRun;
        }

        /// <summary>
        /// Mark the test run as completed
        /// </summary>
        public async Task<TestRun> EndTestRunAsync(TestRunData testRunData, int testRunId, bool publishAttachmentsAsArchive = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            ArgUtil.NotNull(testRunData, nameof(testRunData));
            Trace.Entering();
            RunUpdateModel updateModel = new RunUpdateModel(
                completedDate: testRunData.CompleteDate,
                state: TestRunState.Completed.ToString()
                );
            TestRun testRun = await _testResultsServer.UpdateTestRunAsync(_projectName, testRunId, updateModel, cancellationToken);

            // Uploading run level attachments, only after run is marked completed;
            // so as to make sure that any server jobs that acts on the uploaded data (like CoverAn job does for Coverage files)
            // have a fully published test run results, in case it wants to iterate over results

            if (publishAttachmentsAsArchive)
            {
                await UploadTestRunAttachmentsAsArchiveAsync(testRunId, testRunData.Attachments, cancellationToken);
            }
            else
            {
                await UploadTestRunAttachmentsIndividualAsync(testRunId, testRunData.Attachments, cancellationToken);
            }

            _executionContext.Output(string.Format(CultureInfo.CurrentCulture, "Published Test Run : {0}", testRun.WebAccessUrl));

            return testRun;
        }

        /// <summary>
        /// Converts the given results file to TestRunData object
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <returns>TestRunData</returns>
        public TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath)
        {
            Trace.Entering();
            return _resultReader.ReadResults(_executionContext, filePath, runContext, _isParallelProcessingFFEnabled);
        }

        /// <summary>
        /// Converts the given results file to TestRunData object
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <param name="runName">Run Name</param>
        /// <returns>TestRunData</returns>
        public TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath, string runName)
        {
            ArgUtil.NotNull(runContext, nameof(runContext));
            Trace.Entering();
            runContext.RunName = runName;
            return _resultReader.ReadResults(_executionContext, filePath, runContext, _isParallelProcessingFFEnabled);
        }

        public bool IsTestRunFailed(string projectName,
           int testRunId,
           CancellationToken cancellationToken)
        {
            Trace.Entering();
            _publisherInputValidator.CheckTestRunId(testRunId);

            _executionContext.Output(string.Format(CultureInfo.CurrentCulture, $"TestRunPublisher.IsTestRunFailed: Getting test run summary with run id: {testRunId} using stats api"));
            var stats = _testResultsServer.GetTestRunStatisticsAsync(projectName, testRunId, cancellationToken: cancellationToken).Result;
            return IsTestRunFailed(stats, cancellationToken);
        }

        public bool IsTestRunFailed(TestRunStatistic testRunStatistics,
            CancellationToken cancellationToken)
        {
            Trace.Entering();

            _executionContext.Output(string.Format(CultureInfo.CurrentCulture, "TestRunPublisher.IsTestRunFailed: checking if test run is failed using run summary input"));
            if (testRunStatistics != null && testRunStatistics.RunStatistics?.Count > 0)
            {
                foreach (var stat in testRunStatistics.RunStatistics)
                {
                    if (stat.Outcome == TestOutcome.Failed.ToString() && stat.Count > 0 && stat.ResultMetadata == ResultMetadata.Flaky)
                    {
                        _executionContext.Output(string.Format(CultureInfo.CurrentCulture, "Number of flaky failed tests is: {0}", stat.Count));
                    }

                    if (stat.Outcome == TestOutcome.Failed.ToString() && stat.Count > 0 && stat.ResultMetadata != ResultMetadata.Flaky)
                    {
                        _executionContext.Output(string.Format(CultureInfo.CurrentCulture, "Number of failed tests which are non-flaky is: {0}", stat.Count));
                        return true;
                    }
                }
            }

            Trace.Leaving();

            return false;
        }

        public async Task<TestRunStatistic> GetTestRunSummaryAsync(string projectName,
           int testRunId,
           bool allowRetry,
           CancellationToken cancellationToken)
        {
            Trace.Entering();

            TestRunStatistic testRunStatistic = null;

            _executionContext.Debug(string.Format(CultureInfo.CurrentCulture, $"TestRunPublisher.GetTestRunSummaryAsync: Getting test run summary with run id: {testRunId} using new summary api. Allow Retry: {allowRetry}"));

            if (allowRetry)
            {
                var retryHelper = new RetryHelper(_executionContext, MaxRetries);

                testRunStatistic = await retryHelper.Retry(() => GetTestRunSummaryByOutcome(projectName, testRunId, cancellationToken),
                                                           (retryCounter) => GetTimeIntervalDelay(retryCounter),
                                                           (exception) => IsRetriableException(exception));
            }
            else
            {
                testRunStatistic = await GetTestRunSummaryByOutcome(projectName, testRunId, cancellationToken);
            }

            Trace.Leaving();

            return testRunStatistic;
        }

        #endregion

        private int GetTimeIntervalDelay(int retryCounter)
        {
            //As per Kusto
            //First Time when api returns exception -it will be 550 ms delay.
            //First retry -90th percentile comes around after 1.5 seconds therefore delay - 1.5 - .5 = 1 seconds
            //Second retry execution around - 1.5 to 2.5 seconds = therefore delay = 2.5 - (.5 + 1.0) = 1seconds
            //Third retry = 2.5 to 3,5 seconds = therefore delay = 3.5 - (0.5 + 1 + 1) = 1 seconds
            switch (retryCounter)
            {
                case 0:
                    return 500;  // 90th Percentile

                case 1:
                    return 1 * 1000; // 95th -90th percentile

                case 2:
                    return 2 * 1000; // 99th-95th percentile

                default:
                    return 1 * 1000;
            }
        }

        private bool IsRetriableException(Exception exception)
        {
            var type = exception.GetType();
            if (type == typeof(TestObjectNotFoundException))
            {
                // handle TestObjectNotFoundException for retry
                return true;
            }
            return false;
        }

        private Task<TestRunStatistic> GetTestRunSummaryByOutcome(string projectName, int testRunId, CancellationToken cancellationToken)
        {
            return _testResultsServer.GetTestRunSummaryByOutcomeAsync(projectName, testRunId, cancellationToken: cancellationToken);
        }

        private bool IsMaxLimitReachedForSubresultPreProcessing(string automatedTestName, List<TestCaseSubResultData> subResults, int level = 1)
        {
            int maxSubResultHierarchyLevel = TestManagementConstants.maxHierarchyLevelForSubresults;
            int maxSubResultIterationCount = TestManagementConstants.maxSubResultPerLevel;
            if (subResults == null || subResults.Count == 0)
            {
                return true;
            }
            if (level > maxSubResultHierarchyLevel)
            {
                return false;
            }
            if (subResults.Count > maxSubResultIterationCount)
            {
                _executionContext.Warning(StringUtil.Loc("MaxSubResultLimitReached", automatedTestName, maxSubResultIterationCount));
                subResults.RemoveRange(maxSubResultIterationCount, subResults.Count - maxSubResultIterationCount);
            }
            foreach (var subresult in subResults)
            {
                if (IsMaxLimitReachedForSubresultPreProcessing(automatedTestName, subresult.SubResultData, level + 1) == false)
                {
                    _executionContext.Warning(StringUtil.Loc("MaxHierarchyLevelReached", maxSubResultHierarchyLevel));
                    subresult.SubResultData = null;
                }
            }
            return true;
        }

        private async Task UploadTestResultsAttachmentAsync(int testRunId,
            TestCaseResultData testCaseResultData,
            TestCaseResult testCaseResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (testCaseResult == null || testCaseResultData == null)
            {
                return;
            }

            if (testCaseResultData.AttachmentData != null)
            {
                // Remove duplicate entries
                string[] attachments = testCaseResultData.AttachmentData.AttachmentsFilePathList?.ToArray();
                HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);

                if (attachedFiles != null && attachedFiles.Any())
                {
                    var createAttachmentsTasks = attachedFiles.Select(async attachment =>
                    {
                        TestAttachmentRequestModel reqModel = GetAttachmentRequestModel(attachment);
                        if (reqModel != null)
                        {
                            await _testResultsServer.CreateTestResultAttachmentAsync(reqModel, _projectName, testRunId, testCaseResult.Id, cancellationToken);
                        }
                    });
                    await Task.WhenAll(createAttachmentsTasks);
                }

                // Upload console log as attachment
                string consoleLog = testCaseResultData?.AttachmentData.ConsoleLog;
                TestAttachmentRequestModel attachmentRequestModel = GetConsoleLogAttachmentRequestModel(consoleLog);
                if (attachmentRequestModel != null)
                {
                    await _testResultsServer.CreateTestResultAttachmentAsync(attachmentRequestModel, _projectName, testRunId, testCaseResult.Id, cancellationToken);
                }

                // Upload standard error as attachment
                string standardError = testCaseResultData.AttachmentData.StandardError;
                TestAttachmentRequestModel stdErrAttachmentRequestModel = GetStandardErrorAttachmentRequestModel(standardError);
                if (stdErrAttachmentRequestModel != null)
                {
                    await _testResultsServer.CreateTestResultAttachmentAsync(stdErrAttachmentRequestModel, _projectName, testRunId, testCaseResult.Id, cancellationToken);
                }
            }

            if (testCaseResult.SubResults != null && testCaseResult.SubResults.Any() && testCaseResultData.TestCaseSubResultData != null)
            {
                for (int i = 0; i < testCaseResultData.TestCaseSubResultData.Count; i++)
                {
                    await UploadTestSubResultsAttachmentAsync(testRunId, testCaseResult.Id, testCaseResultData.TestCaseSubResultData[i], testCaseResult.SubResults[i], 1, cancellationToken);
                }
            }
        }
        private async Task UploadTestSubResultsAttachmentAsync(int testRunId,
            int testResultId,
            TestCaseSubResultData subResultData,
            TestSubResult subresult,
            int level,
            CancellationToken cancellationToken)
        {
            if (level > TestManagementConstants.maxHierarchyLevelForSubresults || subresult == null || subResultData == null || subResultData.AttachmentData == null)
            {
                return;
            }

            string[] attachments = subResultData.AttachmentData.AttachmentsFilePathList?.ToArray();

            // remove duplicate entries
            HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);
            if (attachedFiles != null && attachedFiles.Any())
            {
                var createAttachmentsTasks = attachedFiles
                    .Select(async attachment =>
                    {
                        TestAttachmentRequestModel reqModel = GetAttachmentRequestModel(attachment);
                        if (reqModel != null)
                        {
                            await _testResultsServer.CreateTestSubResultAttachmentAsync(reqModel, _projectName, testRunId, testResultId, subresult.Id, cancellationToken);
                        }
                    });
                await Task.WhenAll(createAttachmentsTasks);
            }

            // Upload console log as attachment
            string consoleLog = subResultData.AttachmentData.ConsoleLog;
            TestAttachmentRequestModel attachmentRequestModel = GetConsoleLogAttachmentRequestModel(consoleLog);
            if (attachmentRequestModel != null)
            {
                await _testResultsServer.CreateTestSubResultAttachmentAsync(attachmentRequestModel, _projectName, testRunId, testResultId, subresult.Id, cancellationToken);
            }

            // Upload standard error as attachment
            string standardError = subResultData.AttachmentData.StandardError;
            TestAttachmentRequestModel stdErrAttachmentRequestModel = GetStandardErrorAttachmentRequestModel(standardError);
            if (stdErrAttachmentRequestModel != null)
            {
                await _testResultsServer.CreateTestSubResultAttachmentAsync(stdErrAttachmentRequestModel, _projectName, testRunId, testResultId, subresult.Id, cancellationToken);
            }

            if (subResultData.SubResultData != null)
            {
                for (int i = 0; i < subResultData.SubResultData.Count; ++i)
                {
                    await UploadTestSubResultsAttachmentAsync(testRunId, testResultId, subResultData.SubResultData[i], subresult.SubResults[i], level + 1, cancellationToken);
                }
            }
        }

        private async Task UploadTestRunAttachmentsAsArchiveAsync(int testRunId, string[] attachments, CancellationToken cancellationToken)
        {
            Trace.Entering();
            // Do not upload duplicate entries
            HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);
            try
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(tempDirectory);
                string zipFile = Path.Combine(tempDirectory, "TestResults_" + testRunId + ".zip");

                File.Delete(zipFile); //if there's already file. remove silently without exception
                CreateZipFile(zipFile, attachedFiles);
                await CreateTestRunAttachmentAsync(testRunId, zipFile, cancellationToken);
            }
            catch (Exception ex)
            {
                _executionContext.Warning(StringUtil.Loc("UnableToArchiveResults", ex));
                await UploadTestRunAttachmentsIndividualAsync(testRunId, attachments, cancellationToken);
            }
        }

        private void CreateZipFile(string zipfileName, IEnumerable<string> files)
        {
            Trace.Entering();
            // Create and open a new ZIP file
            using (ZipArchive zip = ZipFile.Open(zipfileName, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    // Add the entry for each file
                    zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                }
            }
        }

        private async Task UploadTestRunAttachmentsIndividualAsync(int testRunId, string[] attachments, CancellationToken cancellationToken)
        {
            Trace.Entering();
            _executionContext.Debug("Uploading test run attachements individually");
            // Do not upload duplicate entries
            HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);
            var attachFilesTasks = attachedFiles.Select(async file =>
            {
                await CreateTestRunAttachmentAsync(testRunId, file, cancellationToken);
            });
            await Task.WhenAll(attachFilesTasks);
        }

        private async Task CreateTestRunAttachmentAsync(int testRunId, string zipFile, CancellationToken cancellationToken)
        {
            Trace.Entering();
            TestAttachmentRequestModel reqModel = GetAttachmentRequestModel(zipFile);
            if (reqModel != null)
            {
                await _testResultsServer.CreateTestRunAttachmentAsync(reqModel, _projectName, testRunId, cancellationToken);
            }
        }

        private string GetAttachmentType(string file)
        {
            Trace.Entering();
            string fileName = Path.GetFileNameWithoutExtension(file);

            if (string.Compare(Path.GetExtension(file), ".coverage", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.CodeCoverage.ToString();
            }
            else if (string.Compare(Path.GetExtension(file), ".trx", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.TmiTestRunSummary.ToString();
            }
            else if (string.Compare(fileName, "testimpact", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.TestImpactDetails.ToString();
            }
            else if (string.Compare(fileName, "SystemInformation", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.IntermediateCollectorData.ToString();
            }
            else
            {
                return AttachmentType.GeneralAttachment.ToString();
            }
        }

        private TestAttachmentRequestModel GetAttachmentRequestModel(string attachment)
        {
            Trace.Entering();
            if (!File.Exists(attachment))
            {
                _executionContext.Warning(StringUtil.Loc("TestAttachmentNotExists", attachment));
                return null;
            }

            // https://stackoverflow.com/questions/13378815/base64-length-calculation
            if (new FileInfo(attachment).Length <= TCM_MAX_FILESIZE)
            {
                byte[] bytes = File.ReadAllBytes(attachment);
                string encodedData = Convert.ToBase64String(bytes);
                if (encodedData.Length <= TCM_MAX_FILECONTENT_SIZE)
                {
                    // Replace colon character with underscore character as on linux environment, some earlier version of .net core task
                    // were creating trx files with ":" in it, but this is not an acceptable character in Results attachments API
                    string attachmentFileName = Path.GetFileName(attachment);
                    attachmentFileName = attachmentFileName.Replace(":", "_");

                    return new TestAttachmentRequestModel(encodedData, attachmentFileName, "", GetAttachmentType(attachment));
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", attachment));
                }
            }
            else
            {
                _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", attachment));
            }

            return null;
        }

        private TestAttachmentRequestModel GetConsoleLogAttachmentRequestModel(string consoleLog)
        {
            Trace.Entering();
            if (!string.IsNullOrWhiteSpace(consoleLog))
            {
                string consoleLogFileName = "Standard_Console_Output.log";

                if (consoleLog.Length <= TCM_MAX_FILESIZE)
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(consoleLog);
                    string encodedData = Convert.ToBase64String(bytes);
                    return new TestAttachmentRequestModel(encodedData, consoleLogFileName, "",
                        AttachmentType.ConsoleLog.ToString());
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", consoleLogFileName));
                }
            }

            return null;
        }

        private TestAttachmentRequestModel GetStandardErrorAttachmentRequestModel(string stdErr)
        {
            Trace.Entering();
            if (string.IsNullOrWhiteSpace(stdErr) == false)
            {
                const string stdErrFileName = "Standard_Console_Error.log";

                if (stdErr.Length <= TCM_MAX_FILESIZE)
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(stdErr);
                    string encodedData = Convert.ToBase64String(bytes);
                    return new TestAttachmentRequestModel(encodedData, stdErrFileName, "",
                        AttachmentType.ConsoleLog.ToString());
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", stdErrFileName));
                }
            }

            return null;
        }

        private HashSet<string> GetUniqueTestRunFiles(string[] attachments)
        {
            var attachedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (attachments != null)
            {
                foreach (string attachment in attachments)
                {
                    attachedFiles.Add(attachment);
                }
            }
            return attachedFiles;
        }
    }
}

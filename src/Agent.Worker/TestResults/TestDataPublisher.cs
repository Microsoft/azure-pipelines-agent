// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.TeamFoundation.TestClient.PublishTestResults;
using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Agent.Worker.TestResults.Utils;
using ITestResultsServer = Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults.ITestResultsServer;

namespace Microsoft.VisualStudio.Services.Agent.Worker.TestResults
{
    [ServiceLocator(Default = typeof(TestDataPublisher))]
    public interface ITestDataPublisher : IAgentService
    {
        void InitializePublisher(IExecutionContext executionContext, string projectName, VssConnection connection, string testRunner);

        Task<bool> PublishAsync(TestRunContext runContext, List<string> testResultFiles, PublishOptions publishOptions, CancellationToken cancellationToken = default(CancellationToken));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA2000:Dispose objects before losing scope", MessageId = "CommandTraceListener")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1001:Types that own disposable fields should be disposable", MessageId = "DisposableFieldsArePassedIn")]
    public sealed class TestDataPublisher : AgentService, ITestDataPublisher
    {
        private IExecutionContext _executionContext;
        private string _projectName;
        private ITestRunPublisher _testRunPublisher;

        private ITestLogStore _testLogStore;
        private IParser _parser;

        private VssConnection _connection;
        private IFeatureFlagService _featureFlagService;
        private string _testRunner;
        private bool _calculateTestRunSummary;
        private TestRunDataPublisherHelper _testRunPublisherHelper;
        private ITestResultsServer _testResultsServer;

        public void InitializePublisher(IExecutionContext context, string projectName, VssConnection connection, string testRunner)
        {
            Trace.Entering();
            _executionContext = context;
            _projectName = projectName;
            _connection = connection;
            _testRunner = testRunner;
            _testRunPublisher = new TestRunPublisher(connection, new CommandTraceListener(context));
            _testLogStore = new TestLogStore(connection, new CommandTraceListener(context));
            _testResultsServer = HostContext.GetService<ITestResultsServer>();
            _testResultsServer.InitializeServer(connection, _executionContext);
            var extensionManager = HostContext.GetService<IExtensionManager>();
            _featureFlagService = HostContext.GetService<IFeatureFlagService>();
            _parser = (extensionManager.GetExtensions<IParser>()).FirstOrDefault(x => _testRunner.Equals(x.Name, StringComparison.OrdinalIgnoreCase));
            _testRunPublisherHelper = new TestRunDataPublisherHelper(_executionContext, _testRunPublisher, null, _testResultsServer);
            Trace.Leaving();
        }

        public async Task<bool> PublishAsync(TestRunContext runContext, List<string> testResultFiles, PublishOptions publishOptions, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                TestDataProvider testDataProvider = ParseTestResultsFile(runContext, testResultFiles);
                var publishTasks = new List<Task>();

                if (testDataProvider != null){
                    var testRunData = testDataProvider.GetTestRunData();
                    //publishing run level attachment
                    Task<IList<TestRun>> publishtestRunDataTask = Task.Run(() => _testRunPublisher.PublishTestRunDataAsync(runContext, _projectName, testRunData, publishOptions, cancellationToken));
                    Task uploadBuildDataAttachmentTask = Task.Run(() => UploadBuildDataAttachment(runContext, testDataProvider.GetBuildData(), cancellationToken));

                    publishTasks.Add(publishtestRunDataTask);

                    //publishing build level attachment
                    publishTasks.Add(uploadBuildDataAttachmentTask);

                    await Task.WhenAll(publishTasks);

                    IList<TestRun> publishedRuns = publishtestRunDataTask.Result;

                    _calculateTestRunSummary = _featureFlagService.GetFeatureFlagState(TestResultsConstants.CalculateTestRunSummaryFeatureFlag, TestResultsConstants.TFSServiceInstanceGuid);

                    var isTestRunOutcomeFailed = GetTestRunOutcome(_executionContext, testRunData, out TestRunSummary testRunSummary);
                    
                    // Storing testrun summary in environment variable, which will be read by PublishPipelineMetadataTask and publish to evidence store.
                    if(_calculateTestRunSummary)
                    {
                        TestResultUtils.StoreTestRunSummaryInEnvVar(_executionContext, testRunSummary, _testRunner, "PublishTestResults");
                    }

                    // Check failed results for flaky aware
                    // Fallback to flaky aware if there are any failures.
                    using (var connection = WorkerUtilities.GetVssConnection(_executionContext))
                    {
                        var featureFlagService = _executionContext.GetHostContext().GetService<IFeatureFlagService>();
                        featureFlagService.InitializeFeatureService(_executionContext, connection);
                        var doUseStateAPI = featureFlagService.GetFeatureFlagState(TestResultsConstants.UseStatAPIFeatureFlag, TestResultsConstants.TFSServiceInstanceGuid);

                        if (isTestRunOutcomeFailed && doUseStateAPI)
                        {
                            // If null is returned then fallback to previous value.
                            var runOutcome = _testRunPublisherHelper.DoesRunsContainsFailures(publishedRuns, _projectName);
                            if (runOutcome != null && runOutcome.HasValue)
                            {
                                isTestRunOutcomeFailed = runOutcome.Value;
                            }
                        }
                    }

                    return isTestRunOutcomeFailed;
                }

                return false;
            }
            catch (Exception ex)
            {
                _executionContext.Warning("Failed to publish test run data: "+ ex.ToString());
            }
            return false;
        }

        private TestDataProvider ParseTestResultsFile(TestRunContext runContext, List<string> testResultFiles)
        {
            if (_parser == null)
            {
                throw new ArgumentException("Unknown test runner");
            }
            return _parser.ParseTestResultFiles(_executionContext, runContext, testResultFiles);
        }

        private bool GetTestRunOutcome(IExecutionContext executionContext, IList<TestRunData> testRunDataList, out TestRunSummary testRunSummary)
        {
            bool anyFailedTests = false;
            testRunSummary = new TestRunSummary();
            foreach (var testRunData in testRunDataList)
            {
                foreach (var testCaseResult in testRunData.TestResults)
                {
                    testRunSummary.Total += 1;
                    Enum.TryParse(testCaseResult.Outcome, out TestOutcome outcome);
                    switch(outcome)
                    {
                        case TestOutcome.Failed:
                        case TestOutcome.Aborted:
                            testRunSummary.Failed += 1;
                            anyFailedTests = true;
                            break;
                        case TestOutcome.Passed:
                            testRunSummary.Passed += 1;
                            break;
                        case TestOutcome.Inconclusive:
                            testRunSummary.Skipped += 1;
                            break;
                        default: break;
                    }

                    if(!_calculateTestRunSummary && anyFailedTests)
                    {
                        return anyFailedTests;
                    }
                }
            }
            return anyFailedTests;
        }

        private async Task UploadRunDataAttachment(TestRunContext runContext, List<TestRunData> testRunData, PublishOptions publishOptions, CancellationToken cancellationToken = default(CancellationToken))
        {
            await _testRunPublisher.PublishTestRunDataAsync(runContext, _projectName, testRunData, publishOptions, cancellationToken);
        }

        private async Task UploadBuildDataAttachment(TestRunContext runContext, List<BuildData> buildDataList, CancellationToken cancellationToken = default(CancellationToken))
        {
            _executionContext.Debug("Uploading build level attachements individually");

            Guid projectId = await GetProjectId(_projectName);

            var attachFilesTasks = new List<Task>();
            HashSet<BuildAttachment> attachments = new HashSet<BuildAttachment>(new BuildAttachmentComparer());

            foreach (var buildData in buildDataList)
            {
                attachFilesTasks.AddRange(buildData.BuildAttachments
                    .Select(
                    async attachment =>
                    {
                        if (attachments.Contains(attachment))
                        {
                            _executionContext.Debug($"Skipping upload of {attachment.Filename} as it was already uploaded.");
                            await Task.Yield();
                        }
                        else
                        {
                            attachments.Add(attachment);
                            await UploadTestBuildLog(projectId, attachment, runContext, cancellationToken);
                        }
                    })
                );
            }

            _executionContext.Debug($"Total build level attachments: {attachFilesTasks.Count}.");
            await Task.WhenAll(attachFilesTasks);
        }

        private async Task UploadTestBuildLog(Guid projectId, BuildAttachment buildAttachment, TestRunContext runContext, CancellationToken cancellationToken)
        {
            await _testLogStore.UploadTestBuildLogAsync(projectId, runContext.BuildId, buildAttachment.TestLogType, buildAttachment.Filename, buildAttachment.Metadata, null, buildAttachment.AllowDuplicateUploads, buildAttachment.TestLogCompressionType, cancellationToken);
        }

        private async Task<Guid> GetProjectId(string projectName)
        {
            var _projectClient = _connection.GetClient<ProjectHttpClient>();

            TeamProject proj = null;

            try
            {
                proj = await _projectClient.GetProject(projectName);
            }
            catch (Exception ex)
            {
                _executionContext.Warning("Get project failed" + projectName + " , exception: " + ex);
            }

            return proj.Id;
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Tests;
using Microsoft.VisualStudio.Services.Agent.Worker;
using Microsoft.VisualStudio.Services.Agent.Worker.Build;
using Moq;
using Moq.Language.Flow;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Worker.Build
{
    public sealed class TrackingConfigL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TrackingConfig_parameterless_ctor_should_return_almost_empty_object()
        {
            using (TestHostContext tc = new TestHostContext(this))
            {
                // Arrange.
                // Act.
                var config = new TrackingConfig();

                // Assert.
                Assert.Equal(3, config.FileFormatVersion);
                Assert.Equal(null, config.FileLocation);
                Assert.Equal(null, config.HashKey);
                Assert.NotNull(config.RepositoryTrackingInfo);
                Assert.Equal(false, config.ShouldSerializeRepositoryTrackingInfo());
                Assert.Equal(null, config.System);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TrackingConfig_copy_legacy_ctor_should_fill_in_fields_correctly()
        {
            using (TestHostContext tc = Setup(out Mock<IExecutionContext> mockExecutionContext))
            {
                // Arrange.
                var legacyConfig = new LegacyTrackingConfig
                {
                    BuildDirectory = Path.Combine("path", "_work", "123"),
                    CollectionId = CollectionId,
                    DefinitionId = DefinitionId.ToString(),
                    HashKey = "some_hash_key",
                    RepositoryUrl = RepositoryUrl,
                    System = "Build",
                };

                // Act.
                var config = new TrackingConfig(mockExecutionContext.Object, legacyConfig, "s", "git", true);

                // Assert.
                Assert.Equal(Path.Combine("123", "a"), config.ArtifactsDirectory);
                Assert.Equal("123", config.BuildDirectory);
                Assert.Equal(CollectionId, config.CollectionId);
                Assert.Equal(CollectionUrl, config.CollectionUrl);
                Assert.Equal(DefinitionId.ToString(), config.DefinitionId);
                Assert.Equal(null, config.DefinitionName);
                Assert.Equal(3, config.FileFormatVersion);
                Assert.Equal(null, config.FileLocation);
                Assert.Equal("some_hash_key", config.HashKey);
                Assert.Equal("git", config.RepositoryType);
                Assert.Equal(RepositoryUrl, config.RepositoryUrl);
                Assert.Equal(Path.Combine("123", "s"), config.SourcesDirectory);
                Assert.Equal("Build", config.System);
                Assert.Equal(Path.Combine("123", "TestResults"), config.TestResultsDirectory);
                Assert.NotNull(config.RepositoryTrackingInfo);
                Assert.Equal(false, config.ShouldSerializeRepositoryTrackingInfo());
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TrackingConfig_ctor_should_fill_in_fields_correctly()
        {
            using (TestHostContext tc = Setup(out Mock<IExecutionContext> mockExecutionContext))
            {
                // Arrange.
                var repository = new RepositoryResource() { Type = RepositoryTypes.Git, Url = new Uri(RepositoryUrl) };

                // Act.
                var config = new TrackingConfig(mockExecutionContext.Object, new[] { repository }, DefinitionId);

                // Assert.
                Assert.Equal(Path.Combine("322", "a"), config.ArtifactsDirectory);
                Assert.Equal("322", config.BuildDirectory);
                Assert.Equal(CollectionId, config.CollectionId);
                Assert.Equal(CollectionUrl, config.CollectionUrl);
                Assert.Equal(DefinitionId.ToString(), config.DefinitionId);
                Assert.Equal(DefinitionName, config.DefinitionName);
                Assert.Equal(3, config.FileFormatVersion);
                Assert.Equal(null, config.FileLocation);
                Assert.Equal("ea7c71421cca06c927f73627b66d6b4f4c3a5f4a", config.HashKey);
                Assert.Equal(RepositoryTypes.Git, config.RepositoryType);
                Assert.Equal(RepositoryUrl, config.RepositoryUrl);
                Assert.Equal(Path.Combine("322", "s"), config.SourcesDirectory);
                Assert.Equal("build", config.System);
                Assert.Equal(Path.Combine("322", "TestResults"), config.TestResultsDirectory);
                Assert.NotNull(config.RepositoryTrackingInfo);
                Assert.Equal(true, config.ShouldSerializeRepositoryTrackingInfo());
                Assert.Equal(1, config.RepositoryTrackingInfo.Count);
                Assert.Equal(RepositoryUrl, config.RepositoryTrackingInfo[0].RepositoryUrl);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void TrackingConfig_clone_should_fill_in_fields_correctly()
        {
            using (TestHostContext tc = Setup(out Mock<IExecutionContext> mockExecutionContext))
            {
                // Arrange.
                var repository = new RepositoryResource() { Type = RepositoryTypes.Git, Url = new Uri(RepositoryUrl) };

                // Act.
                var config = new TrackingConfig(mockExecutionContext.Object, new[] { repository }, DefinitionId);
                var clone = config.Clone();

                // Assert.
                // Verify the original first
                Assert.Equal(Path.Combine("322", "a"), config.ArtifactsDirectory);
                Assert.Equal("322", config.BuildDirectory);
                Assert.Equal(CollectionId, config.CollectionId);
                Assert.Equal(CollectionUrl, config.CollectionUrl);
                Assert.Equal(DefinitionId.ToString(), config.DefinitionId);
                Assert.Equal(DefinitionName, config.DefinitionName);
                Assert.Equal(3, config.FileFormatVersion);
                Assert.Equal(null, config.FileLocation);
                Assert.Equal("ea7c71421cca06c927f73627b66d6b4f4c3a5f4a", config.HashKey);
                Assert.Equal(RepositoryTypes.Git, config.RepositoryType);
                Assert.Equal(RepositoryUrl, config.RepositoryUrl);
                Assert.Equal(Path.Combine("322", "s"), config.SourcesDirectory);
                Assert.Equal("build", config.System);
                Assert.Equal(Path.Combine("322", "TestResults"), config.TestResultsDirectory);
                Assert.NotNull(config.RepositoryTrackingInfo);
                Assert.Equal(true, config.ShouldSerializeRepositoryTrackingInfo());
                Assert.Equal(1, config.RepositoryTrackingInfo.Count);
                Assert.Equal(RepositoryUrl, config.RepositoryTrackingInfo[0].RepositoryUrl);
                // Verify that the clone has the same the values
                Assert.Equal(Path.Combine("322", "a"), clone.ArtifactsDirectory);
                Assert.Equal("322", clone.BuildDirectory);
                Assert.Equal(CollectionId, clone.CollectionId);
                Assert.Equal(CollectionUrl, clone.CollectionUrl);
                Assert.Equal(DefinitionId.ToString(), clone.DefinitionId);
                Assert.Equal(DefinitionName, clone.DefinitionName);
                Assert.Equal(3, clone.FileFormatVersion);
                Assert.Equal(null, clone.FileLocation);
                Assert.Equal("ea7c71421cca06c927f73627b66d6b4f4c3a5f4a", clone.HashKey);
                Assert.Equal(RepositoryTypes.Git, clone.RepositoryType);
                Assert.Equal(RepositoryUrl, clone.RepositoryUrl);
                Assert.Equal(Path.Combine("322", "s"), clone.SourcesDirectory);
                Assert.Equal("build", clone.System);
                Assert.Equal(Path.Combine("322", "TestResults"), clone.TestResultsDirectory);
                Assert.NotNull(clone.RepositoryTrackingInfo);
                Assert.Equal(true, clone.ShouldSerializeRepositoryTrackingInfo());
                Assert.Equal(1, clone.RepositoryTrackingInfo.Count);
                Assert.Equal(RepositoryUrl, clone.RepositoryTrackingInfo[0].RepositoryUrl);


            }
        }

        private TestHostContext Setup(out Mock<IExecutionContext> mockExecutionContext)
        {
            var tc = new TestHostContext(this);

            // Setup the execution context.
            mockExecutionContext = tc.CreateExecutionContext();
            List<string> warnings;
            var variables = new Variables(tc, new Dictionary<string, VariableValue>(), out warnings);
            variables.Set(Constants.Variables.System.CollectionId, CollectionId);
            variables.Set(WellKnownDistributedTaskVariables.TFCollectionUrl, CollectionUrl);
            variables.Set(Constants.Variables.System.DefinitionId, DefinitionId.ToString());
            variables.Set(Constants.Variables.Build.DefinitionName, DefinitionName);
            mockExecutionContext.Setup(x => x.Variables).Returns(variables);

            return tc;
        }

        private const string CollectionId = "226466ab-342b-4ca4-bbee-0b87154d4936";
        private const string CollectionName = "Some collection name";
        private const string CollectionUrl = "http://contoso:8080/tfs/DefaultCollection/";
        private const int DefinitionId = 322;
        private const string DefinitionName = "Some definition name";
        private const string RepositoryUrl = "http://contoso:8080/tfs/DefaultCollection/_git/gitTest";
    }
}

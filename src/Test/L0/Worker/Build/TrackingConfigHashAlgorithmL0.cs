// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Worker;
using Microsoft.VisualStudio.Services.Agent.Worker.Build;
using Moq;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests.Worker.Build
{
    public sealed class TrackingConfigHashAlgorithmL0
    {
        // This test is the original test case and is kept to make sure back compat still works.
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ComputeHash_returns_correct_hash()
        {
            using (TestHostContext tc = new TestHostContext(this))
            {
                // Arrange.
                var collectionId = "7aee6dde-6381-4098-93e7-50a8264cf066";
                var definitionId = "7";
                var executionContext = tc.CreateExecutionContext();
                List<string> warnings;
                executionContext
                    .Setup(x => x.Variables)
                    .Returns(new Variables(tc, copy: new Dictionary<string, VariableValue>(), warnings: out warnings));
                executionContext.Object.Variables.Set(Constants.Variables.System.CollectionId, collectionId);
                executionContext.Object.Variables.Set(Constants.Variables.System.DefinitionId, definitionId);

                var repoInfo = new RepositoryTrackingInfo
                {
                    RepositoryUrl = new Uri("http://contoso:8080/tfs/DefaultCollection/gitTest/_git/gitTest").AbsoluteUri,
                };

                // Act.
                string hashKey = TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new [] { repoInfo });

                // Assert.
                Assert.Equal("5c5c3d7ac33cca6604736eb3af977f23f1cf1146", hashKey);
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public void ComputeHash_should_throw_when_parameters_invalid()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                var repo = new RepositoryTrackingInfo()
                {
                    Identifier = "MyRepo",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                string collectionId = "866A5D79-7735-49E3-87DA-02E76CF8D03A";
                string definitionId = "123";

                Assert.Throws<ArgumentNullException>(() => TrackingConfigHashAlgorithm.ComputeHash(null, null, null));
                Assert.Throws<ArgumentNullException>(() => TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, null));
                Assert.Throws<ArgumentNullException>(() => TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { new RepositoryTrackingInfo() }));
                Assert.Throws<ArgumentNullException>(() => TrackingConfigHashAlgorithm.ComputeHash(null, null, new[] { repo }));
                Assert.Throws<ArgumentNullException>(() => TrackingConfigHashAlgorithm.ComputeHash(null, definitionId, new[] { repo }));
                Assert.Throws<ArgumentNullException>(() => TrackingConfigHashAlgorithm.ComputeHash(collectionId, null, new[] { repo }));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public void ComputeHash_with_single_repo_should_return_correct_hash()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                var repo1 = new RepositoryTrackingInfo()
                {
                    Identifier = "alias",
                    RepositoryType = "git",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                var repo2 = new RepositoryTrackingInfo()
                {
                    Identifier = "alias2",
                    RepositoryType = "git2",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                string collectionId = "866A5D79-7735-49E3-87DA-02E76CF8D03A";
                string definitionId = "123";

                // Make sure that only the coll, def, and url are used in the hash
                Assert.Equal("9a89eaa7b8b603633ef1dd5c46464355c716268f", TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1 }));
                Assert.Equal("9a89eaa7b8b603633ef1dd5c46464355c716268f", TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo2 }));
                Assert.Equal(TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1 }), TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1 }));

                // Make sure that different coll creates different hash
                Assert.Equal("2a41800cd3e7f5983a7643698f67104ed95101f3", TrackingConfigHashAlgorithm.ComputeHash("FFFA5D79-7735-49E3-87DA-02E76CF8D03A", definitionId, new[] { repo1 }));

                // Make sure that different def creates different hash
                Assert.Equal("84b4463d95631b4d358f4b67d8994fe7d5b0c013", TrackingConfigHashAlgorithm.ComputeHash(collectionId, "321", new[] { repo1 }));

                // Make sure that different url creates different hash
                repo1.RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/new_url";
                Assert.Equal("6505a9272091df39b90d6fd359e3bf39a7883e9e", TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1 }));
            }
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public void ComputeHash_with_multi_repos_should_return_correct_hash()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                var repo1 = new RepositoryTrackingInfo()
                {
                    Identifier = "alias",
                    SourcesDirectory = "path/repo1_a",
                    RepositoryType = "git",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                var repo2 = new RepositoryTrackingInfo()
                {
                    Identifier = "alias2",
                    SourcesDirectory = "path/repo1_b",
                    RepositoryType = "git2",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                var repo2_newPath = new RepositoryTrackingInfo()
                {
                    Identifier = "alias2",
                    SourcesDirectory = "path/repo1_c",
                    RepositoryType = "git3",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                var repo1_newUrl = new RepositoryTrackingInfo()
                {
                    Identifier = "alias",
                    SourcesDirectory = "path/repo1_a",
                    RepositoryType = "git",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/new_url",
                };
                var repo1_newAlias = new RepositoryTrackingInfo()
                {
                    Identifier = "alias3",
                    SourcesDirectory = "path/repo1_a",
                    RepositoryType = "git",
                    RepositoryUrl = "https://jpricket@codedev.ms/jpricket/MyFirstProject/_git/repo1_url",
                };
                string collectionId = "866A5D79-7735-49E3-87DA-02E76CF8D03A";
                string definitionId = "123";

                // Make sure we get the same hash every time
                Assert.Equal("502520817d9c9d3002a7a56526f7518709fecd6a", TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1, repo2 }));

                // Make sure that only the coll, def, identifier, and url are used in the hash
                Assert.Equal(
                    TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1, repo2 }),
                    TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1, repo2_newPath }));

                // Make sure that different coll creates different hash
                Assert.Equal("ea81feec2216d9da8adc7f29005d44eafbd12626", TrackingConfigHashAlgorithm.ComputeHash("FFFA5D79-7735-49E3-87DA-02E76CF8D03A", definitionId, new[] { repo1, repo2 }));

                // Make sure that different def creates different hash
                Assert.Equal("8742e9847224e2b9de3884beac15759cfd8403e0", TrackingConfigHashAlgorithm.ComputeHash(collectionId, "321", new[] { repo1, repo2 }));

                // Make sure that different url creates different hash
                Assert.Equal("279dd578a58faba3f6cd23c3d62d452448b1e8cc", TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1_newUrl, repo2 }));

                // Make sure that different alias creates different hash
                Assert.Equal("e3553307993d00df159a011b129a7f720084ee02", TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1_newAlias, repo2 }));

                // Make sure order doesn't change hash
                Assert.Equal(
                    TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo1, repo2 }),
                    TrackingConfigHashAlgorithm.ComputeHash(collectionId, definitionId, new[] { repo2, repo1 }));

            }
        }

    }
}

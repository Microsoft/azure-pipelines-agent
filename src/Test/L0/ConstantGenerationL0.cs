// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests
{
    public sealed class ConstantGenerationL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        public void BuildConstantGenerateSucceed()
        {
            List<string> validPackageNames = new List<string>()
            {
                "win-x64",
                "win-x86",
                "linux-x64",
                "linux-arm",
                "linux-arm64",
                "freebsd-x64",
                "rhel.6-x64",
                "osx-x64"
            };

            Assert.True(BuildConstants.Source.CommitHash.Length == 40, $"CommitHash should be SHA-1 hash {BuildConstants.Source.CommitHash}");
            Assert.True(validPackageNames.Contains(BuildConstants.AgentPackage.PackageName), $"PackageName should be one of the following '{string.Join(", ", validPackageNames)}', current PackageName is '{BuildConstants.AgentPackage.PackageName}'");
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Agent")]
        public void ReleaseBuiltFromGitNotFromTarball()
        {
#if !DEBUG
            // don't ship an agent with an empty commit ID
            Assert.True(BuildConstants.Source.CommitHash != new string('0', 40), $"CommitHash should be non-empty");
#endif
        }
    }
}

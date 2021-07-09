// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.VisualStudio.Services.Agent.Util;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests
{
    public static class TestUtil
    {
        private const string Src = "src";
        private const string TestData = "TestData";

        public static string GetProjectPath(string name = "Test")
        {
            ArgUtil.NotNullOrEmpty(name, nameof(name));
            string projectDir = Path.Combine(
                GetSrcPath(),
                name);
            Assert.True(Directory.Exists(projectDir));
            return projectDir;
        }

        private static string GetThisFilePath([CallerFilePath] string path = null)
        {
            return path;
        }

        public static string GetSrcPath()
        {
            string L0dir = Path.GetDirectoryName(GetThisFilePath());
            string testDir = Path.GetDirectoryName(L0dir);
            string srcDir = Path.GetDirectoryName(testDir);
            ArgUtil.Directory(srcDir, nameof(srcDir));
            Assert.Equal(Src, Path.GetFileName(srcDir));
            return srcDir;
        }

        public static string GetTestDataPath()
        {
            string testDataDir = Path.Combine(GetProjectPath(), TestData);
            Assert.True(Directory.Exists(testDataDir));
            return testDataDir;
        }

        public static string WriteAllTextToTempFile(string content, string extension=null)
        {
            string file = Path.GetTempFileName();
            if (!string.IsNullOrEmpty(extension))
            {
                file = Path.ChangeExtension(file, extension);
            }
            File.WriteAllText(file, content);
            return file;
        }

        public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        public static bool IsFreeBSD() => RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);
        public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}

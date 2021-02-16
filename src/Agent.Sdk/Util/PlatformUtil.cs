// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using Agent.Sdk.Knob;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.Sdk
{
    public static class PlatformUtil
    {
        private static UtilKnobValueContext _knobContext = UtilKnobValueContext.Instance();
        
        // System.Runtime.InteropServices.OSPlatform is a struct, so it is
        // not suitable for switch statements.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1717: Only FlagsAttribute enums should have plural names")]
        public enum OS
        {
            Linux,
            FreeBSD,
            OSX,
            Windows
        }

        public static OS HostOS
        {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1065: Do not raise exceptions in unexpected")]
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return OS.Linux;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
                {
                    return OS.FreeBSD;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return OS.OSX;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return OS.Windows;
                }

                throw new NotImplementedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
            }
        }

        public static bool RunningOnWindows
        {
            get => PlatformUtil.HostOS == PlatformUtil.OS.Windows;
        }

        public static bool RunningOnMacOS
        {
            get => PlatformUtil.HostOS == PlatformUtil.OS.OSX;
        }

        public static bool RunningOnLinux
        {
            get => PlatformUtil.HostOS == PlatformUtil.OS.Linux;
        }

        public static bool RunningOnFreeBSD
        {
            get => PlatformUtil.HostOS == PlatformUtil.OS.FreeBSD;
        }

        public static bool RunningOnRHEL6
        {
            get
            {
                if (!(detectedRHEL6 is null))
                {
                    return (bool)detectedRHEL6;
                }

                DetectRHEL6();

                return (bool)detectedRHEL6;
            }
        }

        private static void DetectRHEL6()
        {
            lock (detectedRHEL6lock)
            {
                if (!RunningOnLinux || !File.Exists("/etc/redhat-release"))
                {
                    detectedRHEL6 = false;
                }
                else
                {
                    detectedRHEL6 = false;
                    try
                    {
                        string redhatVersion = File.ReadAllText("/etc/redhat-release");
                        if (redhatVersion.StartsWith("CentOS release 6.")
                            || redhatVersion.StartsWith("Red Hat Enterprise Linux Server release 6."))
                        {
                            detectedRHEL6 = true;
                        }
                    }
                    catch (IOException)
                    {
                        // IOException indicates we couldn't read that file; probably not RHEL6
                    }
                }
            }
        }

        private static bool? detectedRHEL6 = null;
        private static object detectedRHEL6lock = new object();

        public static Architecture HostArchitecture
        {
            get => RuntimeInformation.OSArchitecture;
        }

        public static bool IsX86
        {
            get => PlatformUtil.HostArchitecture == Architecture.X86;
        }

        public static bool IsX64
        {
            get => PlatformUtil.HostArchitecture == Architecture.X64;
        }

        public static bool IsArm
        {
            get => PlatformUtil.HostArchitecture == Architecture.Arm;
        }

        public static bool IsArm64
        {
            get => PlatformUtil.HostArchitecture == Architecture.Arm64;
        }

        public static bool BuiltOnX86
        {
            get
            {
#if X86
                return true;
#else
                return false;
#endif
            }
        }

        public static bool UseLegacyHttpHandler
        {
            // In .NET Core 2.1, we couldn't use the new SocketsHttpHandler for Windows or Linux
            // On Linux, negotiate auth didn't work if the TFS URL was HTTPS
            // On Windows, proxy was not working
            // But on ARM/ARM64 Linux, the legacy curl dependency is problematic
            // (see https://github.com/dotnet/runtime/issues/28891), so we slowly
            // started to use the new handler.
            //
            // The legacy handler is going away in .NET 5.0, so we'll go ahead
            // and remove its usage now. In case this breaks anyone, adding
            // a temporary knob so they can re-enable it.
            // https://github.com/dotnet/runtime/issues/35365#issuecomment-667467706
            get => AgentKnobs.UseLegacyHttpHandler.GetValue(_knobContext).AsBoolean();
        }
    }
}

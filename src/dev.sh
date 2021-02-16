#!/bin/bash

###############################################################################
#
#  ./dev.sh build/layout/test/package [Debug/Release] [optional: runtime ID]
#
###############################################################################

set -eo pipefail

ALL_ARGS=("$@")
DEV_CMD=$1
DEV_CONFIG=$2
DEV_RUNTIME_ID=$3
DEV_TEST_FILTERS=$4
DEV_ARGS=("${ALL_ARGS[@]:4}")

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

source "$SCRIPT_DIR/.helpers.sh"

DOTNETSDK_ROOT="$SCRIPT_DIR/../_dotnetsdk"
DOTNETSDK_VERSION="3.1.100"
DOTNETSDK_INSTALLDIR="$DOTNETSDK_ROOT/$DOTNETSDK_VERSION"
AGENT_VERSION=$(cat "$SCRIPT_DIR/agentversion")

DOTNET_ERROR_PREFIX="##vso[task.logissue type=error]"
DOTNET_WARNING_PREFIX="##vso[task.logissue type=warning]"

PACKAGE_TYPE=${PACKAGE_TYPE:-agent} # agent or pipelines-agent
if [[ "$PACKAGE_TYPE" == "pipelines-agent" ]]; then
    export INCLUDE_NODE6="false"
fi

pushd "$SCRIPT_DIR"

BUILD_CONFIG="Debug"
if [[ "$DEV_CONFIG" == "Release" ]]; then
    BUILD_CONFIG="Release"
fi

function detect_platform_and_runtime_id ()
{
    heading "Platform / RID detection"

    CURRENT_PLATFORM="windows"
    if [[ ($(uname) == "Linux") || ($(uname) == "FreeBSD") || ($(uname) == "Darwin") ]]; then
        CURRENT_PLATFORM=$(uname | awk '{print tolower($0)}')
    fi

    if [[ "$CURRENT_PLATFORM" == 'windows' ]]; then
        DETECTED_RUNTIME_ID='win-x64'
        if [[ "$PROCESSOR_ARCHITECTURE" == 'x86' ]]; then
            DETECTED_RUNTIME_ID='win-x86'
        fi
    elif [[ "$CURRENT_PLATFORM" == 'linux' ]]; then
        DETECTED_RUNTIME_ID="linux-x64"
        if command -v uname > /dev/null; then
            local CPU_NAME=$(uname -m)
            case $CPU_NAME in
                armv7l) DETECTED_RUNTIME_ID="linux-arm";;
                aarch64) DETECTED_RUNTIME_ID="linux-arm64";;
            esac
        fi

        if [ -e /etc/redhat-release ]; then
            local redhatRelease=$(</etc/redhat-release)
            if [[ $redhatRelease == "CentOS release 6."* || $redhatRelease == "Red Hat Enterprise Linux Server release 6."* ]]; then
                DETECTED_RUNTIME_ID='rhel.6-x64'
            fi
        fi

    elif [[ "$CURRENT_PLATFORM" == 'freebsd' ]]; then
        DETECTED_RUNTIME_ID='freebsd-x64'
    elif [[ "$CURRENT_PLATFORM" == 'darwin' ]]; then
        DETECTED_RUNTIME_ID='osx-x64'
    fi
}

function cmd_build ()
{
    heading "Building"
    TARGET="Build"
    if  [[ "$ADO_ENABLE_LOGISSUE" == "true" ]]; then

        dotnet msbuild -t:${TARGET} -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" -p:CodeAnalysis="true" \
         | sed -e "/\: warning /s/^/${DOTNET_WARNING_PREFIX} /;" \
         | sed -e "/\: error /s/^/${DOTNET_ERROR_PREFIX} /;" \
         || failed build
    else
        dotnet msbuild -t:${TARGET} -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" -p:CodeAnalysis="true" \
         || failed build
    fi


    mkdir -p "${LAYOUT_DIR}/bin/en-US"
    grep --invert-match '^ *"CLI-WIDTH-' ./Misc/layoutbin/en-US/strings.json > "${LAYOUT_DIR}/bin/en-US/strings.json"

}

function cmd_layout ()
{
    heading "Creating layout"
    TARGET="layout"
    if  [[ "$ADO_ENABLE_LOGISSUE" == "true" ]]; then
        dotnet msbuild -t:${TARGET} -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" -p:CodeAnalysis="true" \
         | sed -e "/\: warning /s/^/${DOTNET_WARNING_PREFIX} /;" \
         | sed -e "/\: error /s/^/${DOTNET_ERROR_PREFIX} /;" \
         || failed build
    else
        dotnet msbuild -t:${TARGET} -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" -p:CodeAnalysis="true" \
         || failed build
    fi

    mkdir -p "${LAYOUT_DIR}/bin/en-US"
    grep --invert-match '^ *"CLI-WIDTH-' ./Misc/layoutbin/en-US/strings.json > "${LAYOUT_DIR}/bin/en-US/strings.json"

    #change execution flag to allow running with sudo
    if [[ ("$CURRENT_PLATFORM" == "linux") || ("$CURRENT_PLATFORM" == "freebsd") || ("$CURRENT_PLATFORM" == "darwin") ]]; then
        chmod +x "${LAYOUT_DIR}/bin/Agent.Listener"
        chmod +x "${LAYOUT_DIR}/bin/Agent.Worker"
        chmod +x "${LAYOUT_DIR}/bin/Agent.PluginHost"
        chmod +x "${LAYOUT_DIR}/bin/installdependencies.sh"
    fi

    heading "Setup externals folder for $RUNTIME_ID agent's layout"
    bash ./Misc/externals.sh $RUNTIME_ID || checkRC externals.sh
}

function cmd_test_l0 ()
{
    heading "Testing L0"

    if [[ ("$CURRENT_PLATFORM" == "linux") || ("$CURRENT_PLATFORM" == "freebsd") || ("$CURRENT_PLATFORM" == "darwin") ]]; then
        ulimit -n 1024
    fi

    TestFilters="Level=L0&SkipOn!=${CURRENT_PLATFORM}"
    if [[ "$DEV_TEST_FILTERS" != "" ]]; then
        TestFilters="$TestFilters&$DEV_TEST_FILTERS"
    fi

    dotnet msbuild -t:testl0 -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" -p:TestFilters="${TestFilters}" "${DEV_ARGS[@]}" || failed "failed tests"
}

function cmd_test_l1 ()
{
    heading "Clean"
    dotnet msbuild -t:cleanl1 -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" || failed build

    heading "Setup externals folder for $RUNTIME_ID agent's layout"
    bash ./Misc/externals.sh $RUNTIME_ID "" "_l1" "true" || checkRC externals.sh

    heading "Testing L1"

    if [[ ("$CURRENT_PLATFORM" == "linux") || ("$CURRENT_PLATFORM" == "freebsd") || ("$CURRENT_PLATFORM" == "darwin") ]]; then
        ulimit -n 1024
    fi

    TestFilters="Level=L1&SkipOn!=${CURRENT_PLATFORM}"
    if [[ "$DEV_TEST_FILTERS" != "" ]]; then
        TestFilters="$TestFilters&$DEV_TEST_FILTERS"
    fi

    dotnet msbuild -t:testl1 -p:PackageRuntime="${RUNTIME_ID}" -p:PackageType="${PACKAGE_TYPE}" -p:BUILDCONFIG="${BUILD_CONFIG}" -p:AgentVersion="${AGENT_VERSION}" -p:LayoutRoot="${LAYOUT_DIR}" -p:TestFilters="${TestFilters}" "${DEV_ARGS[@]}" || failed "failed tests"
}

function cmd_test ()
{
    cmd_test_l0

    cmd_test_l1
}

function cmd_package ()
{
    if [ ! -d "${LAYOUT_DIR}/bin" ]; then
        echo "You must build first.  Expecting to find ${LAYOUT_DIR}/bin"
    fi

    agent_ver=$(cat "${SCRIPT_DIR}/agentversion" | tail -n 1) || failed "version"

    if [[ ("$PACKAGE_TYPE" == "pipelines-agent") ]]; then
        agent_pkg_name="pipelines-agent-${RUNTIME_ID}-${agent_ver}"
    else
        agent_pkg_name="vsts-agent-${RUNTIME_ID}-${agent_ver}"
    fi

    # TEMPORARY - need to investigate why Agent.Listener --version is throwing an error on OS X
    if [ $("${LAYOUT_DIR}/bin/Agent.Listener" --version | wc -l) -gt 1 ]; then
        echo "Error thrown during --version call!"
        log_file=$("${LAYOUT_DIR}/bin/Agent.Listener" --version | head -n 2 | tail -n 1 | cut -d\  -f6)
        cat "${log_file}"
    fi
    # END TEMPORARY

    heading "Packaging ${agent_pkg_name}"

    rm -Rf "${LAYOUT_DIR:?}/_diag"
    find "${LAYOUT_DIR}/bin" -type f -name '*.pdb' -delete

    mkdir -p "$PACKAGE_DIR"
    rm -Rf "${PACKAGE_DIR:?}"/*

    pushd "$PACKAGE_DIR" > /dev/null

    if [[ ("$CURRENT_PLATFORM" == "linux") || ("$CURRENT_PLATFORM" == "freebsd") || ("$CURRENT_PLATFORM" == "darwin") ]]; then
        tar_name="${agent_pkg_name}.tar.gz"
        echo "Creating $tar_name in ${PACKAGE_DIR}"
        tar -czf "${tar_name}" -C "${LAYOUT_DIR}" .
    elif [[ ("$CURRENT_PLATFORM" == "windows") ]]; then
        zip_name="${agent_pkg_name}.zip"
        echo "Convert ${LAYOUT_DIR} to Windows style path"
        window_path=${LAYOUT_DIR:1}
        window_path=${window_path:0:1}:${window_path:1}
        echo "Creating $zip_name in ${window_path}"
        powershell -NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command "Add-Type -Assembly \"System.IO.Compression.FileSystem\"; [System.IO.Compression.ZipFile]::CreateFromDirectory(\"${window_path}\", \"${zip_name}\")"
    fi

    popd > /dev/null
}

function cmd_report ()
{
    heading "Generating Reports"

    if [[ ("$CURRENT_PLATFORM" != "windows") ]]; then
        echo "Coverage reporting only available on Windows"
        exit -1
    fi

    mkdir -p "$REPORT_DIR"

    LATEST_COVERAGE_FILE=$(find "${SCRIPT_DIR}/Test/TestResults" -type f -name '*.coverage' -print0 | xargs -r -0 ls -1 -t | head -1)

    if [[ ("$LATEST_COVERAGE_FILE" == "") ]]; then
        echo "No coverage file found. Skipping coverage report generation."
    else
        COVERAGE_REPORT_DIR=$REPORT_DIR/coverage
        mkdir -p "$COVERAGE_REPORT_DIR"
        rm -Rf "${COVERAGE_REPORT_DIR:?}"/*

        echo "Found coverage file $LATEST_COVERAGE_FILE"
        COVERAGE_XML_FILE="$COVERAGE_REPORT_DIR/coverage.xml"
        echo "Converting to XML file $COVERAGE_XML_FILE"

        # for some reason CodeCoverage.exe will only write the output file in the current directory
        pushd $COVERAGE_REPORT_DIR > /dev/null
        "${HOME}/.nuget/packages/microsoft.codecoverage/15.9.2/build/netstandard1.0/CodeCoverage/CodeCoverage.exe" analyze  "/output:coverage.xml" "$LATEST_COVERAGE_FILE"
        popd > /dev/null

        if ! command -v reportgenerator.exe > /dev/null; then
            echo "reportgenerator not installed. Skipping generation of HTML reports"
            echo "To install: "
            echo "  % dotnet tool install --global dotnet-reportgenerator-globaltool"
            exit 0
        fi

        echo "Generating HTML report"
        reportgenerator.exe "-reports:$COVERAGE_XML_FILE" "-reporttypes:Html;Cobertura" "-targetdir:$COVERAGE_REPORT_DIR/coveragereport"
    fi
}

detect_platform_and_runtime_id
echo "Current platform: $CURRENT_PLATFORM"
echo "Current runtime ID: $DETECTED_RUNTIME_ID"

if [ "$DEV_RUNTIME_ID" ]; then
    RUNTIME_ID=$DEV_RUNTIME_ID
else
    RUNTIME_ID=$DETECTED_RUNTIME_ID
fi

_VALID_RIDS='linux-x64:linux-arm:linux-arm64:freebsd-x64:rhel.6-x64:osx-x64:win-x64:win-x86'
if [[ ":$_VALID_RIDS:" != *:$RUNTIME_ID:* ]]; then
    failed "must specify a valid target runtime ID (one of: $_VALID_RIDS)"
fi

echo "Building for runtime ID: $RUNTIME_ID"


LAYOUT_DIR="$SCRIPT_DIR/../_layout/$RUNTIME_ID"
DOWNLOAD_DIR="$SCRIPT_DIR/../_downloads/$RUNTIME_ID/netcore2x"
PACKAGE_DIR="$SCRIPT_DIR/../_package/$RUNTIME_ID"
REPORT_DIR="$SCRIPT_DIR/../_reports/$RUNTIME_ID"
INTEGRATION_DIR="$SCRIPT_DIR/../_layout/integrations"
NODE="${LAYOUT_DIR}/externals/node10/bin/node"

if [[ (! -d "${DOTNETSDK_INSTALLDIR}") || (! -e "${DOTNETSDK_INSTALLDIR}/.${DOTNETSDK_VERSION}") || (! -e "${DOTNETSDK_INSTALLDIR}/dotnet") ]]; then

    # Download dotnet SDK to ../_dotnetsdk directory
    heading "Install .NET SDK"

    # _dotnetsdk
    #           \1.0.x
    #                            \dotnet
    #                            \.1.0.x
    echo "Download dotnetsdk into ${DOTNETSDK_INSTALLDIR}"
    rm -Rf "${DOTNETSDK_DIR}"

    # run dotnet-install.ps1 on windows, dotnet-install.sh on linux
    if [[ ("$CURRENT_PLATFORM" == "windows") ]]; then
        echo "Convert ${DOTNETSDK_INSTALLDIR} to Windows style path"
        sdkinstallwindow_path=${DOTNETSDK_INSTALLDIR:1}
        sdkinstallwindow_path=${sdkinstallwindow_path:0:1}:${sdkinstallwindow_path:1}
        powershell -NoLogo -Sta -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command "& \"./Misc/dotnet-install.ps1\" -Version ${DOTNETSDK_VERSION} -InstallDir \"${sdkinstallwindow_path}\" -NoPath; exit \$LastExitCode;" || checkRC dotnet-install.ps1
    else
        bash ./Misc/dotnet-install.sh --version ${DOTNETSDK_VERSION} --install-dir "${DOTNETSDK_INSTALLDIR}" --no-path || checkRC dotnet-install.sh
    fi

    echo "${DOTNETSDK_VERSION}" > "${DOTNETSDK_INSTALLDIR}/.${DOTNETSDK_VERSION}"
fi


heading ".NET SDK to path"

echo "Adding .NET to PATH (${DOTNETSDK_INSTALLDIR})"
export PATH=${DOTNETSDK_INSTALLDIR}:$PATH
echo "Path = $PATH"
echo ".NET Version = $(dotnet --version)"

heading "Pre-caching external resources for $RUNTIME_ID"
mkdir -p "${LAYOUT_DIR}" >/dev/null
bash ./Misc/externals.sh $RUNTIME_ID "Pre-Cache" || checkRC "externals.sh Pre-Cache"

if [[ "$CURRENT_PLATFORM" == 'windows' ]]; then
    vswhere=$(find "$DOWNLOAD_DIR" -name vswhere.exe | head -1)
    vs_location=$("$vswhere" -latest -property installationPath)
    msbuild_location="$vs_location""\MSBuild\15.0\Bin\msbuild.exe"

    if [[ ! -e "${msbuild_location}" ]]; then
        msbuild_location="$vs_location""\MSBuild\Current\Bin\msbuild.exe"

        if [[ ! -e "${msbuild_location}" ]]; then
            failed "Can not find msbuild location, failing build"
        fi
    fi

    export DesktopMSBuild="$msbuild_location"
fi

case $DEV_CMD in
   "build") cmd_build;;
   "b") cmd_build;;
   "test") cmd_test;;
   "t") cmd_test;;
   "testl0") cmd_test_l0;;
   "l0") cmd_test_l0;;
   "testl1") cmd_test_l1;;
   "l1") cmd_test_l1;;
   "layout") cmd_layout;;
   "l") cmd_layout;;
   "package") cmd_package;;
   "p") cmd_package;;
   "report") cmd_report;;
   *) echo "Invalid command. Use (l)ayout, (b)uild, (t)est, test(l0), test(l1), or (p)ackage.";;
esac

popd
echo
echo Done.
echo

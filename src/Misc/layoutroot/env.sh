#!/bin/bash

varCheckList=(
    'LANG'
    'JAVA_HOME'
    'ANT_HOME'
    'M2_HOME'
    'ANDROID_HOME'
    'GRADLE_HOME'
    'NVM_BIN'
    'NVM_PATH'
    'VSTS_HTTP_PROXY'
    'VSTS_HTTP_PROXY_USERNAME'
    'VSTS_HTTP_PROXY_PASSWORD'
    'LD_LIBRARY_PATH'
    'PERL5LIB'
    'AGENT_TOOLSDIRECTORY'
    )

# Allows the caller to specify additional vars on the commandline, for example:
# ./env.sh DOTNET_SYSTEM_GLOBALIZATION_INVARIANT DOTNET_ROOT
for arg in "$@"
do
    if [[ ! " ${varCheckList[@]} " =~ " ${arg} " ]]; then
        varCheckList+=($arg)
    fi
done


envContents=""

if [ -f ".env" ]; then
    envContents=`cat .env`
else
    touch .env
fi

function writeVar()
{
    checkVar="$1"
    checkDelim="${1}="
    if test "${envContents#*$checkDelim}" = "$envContents"
    then
        if [ ! -z "${!checkVar}" ]; then
            echo "${checkVar}=${!checkVar}">>.env
        fi
    fi
}

echo $PATH>.path

for var_name in ${varCheckList[@]}
do
    writeVar "${var_name}"
done

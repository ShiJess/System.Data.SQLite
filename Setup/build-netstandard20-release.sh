#!/bin/bash

scriptdir=`dirname "$BASH_SOURCE"`

if [[ -z "$SQLITE_NET_YEAR" ]]; then
  SQLITE_NET_YEAR=NetStandard20
fi

pushd "$scriptdir/.."
dotnet build SQLite.NET.$SQLITE_NET_YEAR.MSBuild.sln /property:Configuration=Release /property:ConfigurationSuffix=$SQLITE_NET_CONFIGURATION_SUFFIX /property:InteropCodec=false /property:InteropLog=false "$@"
popd

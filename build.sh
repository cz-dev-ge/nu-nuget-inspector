#!/usr/bin/env bash
#
# Copyright (c) nexB Inc. and others. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
# See http://www.apache.org/licenses/LICENSE-2.0 for the license text.
# See https://github.com/nexB/nuget-inpector for support or download.
# See https://aboutcode.org for more information about nexB OSS projects.
#

# TODO: add --framework
# TODO: add --version-suffix based on git
# TODO: add --arch
# TODO: add --os
#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:+${1#v}}"

mkdir -p build

PUBLISH_ARGS=(
  --runtime linux-x64
  --self-contained true
  --configuration Release
  -p:PublishSingleFile=true
  --output build
  src/nuget-inspector/nuget-inspector.csproj
)

if [[ -n "$VERSION" ]]; then
  echo "Version: $VERSION"
  PUBLISH_ARGS=(
    -p:IncludeSourceRevisionInInformationalVersion=false
    -p:Version="$VERSION"
    "${PUBLISH_ARGS[@]}"
  )
else
  echo "No version supplied, using project default version"
fi

dotnet publish "${PUBLISH_ARGS[@]}"


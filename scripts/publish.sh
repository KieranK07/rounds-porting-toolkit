#!/bin/sh
# Builds the release files: rounds-port for each platform (self-contained, one file, no .NET needed to run it) and
# the Hot Reload plugin, plus SHA256SUMS.txt. Run on macOS so the Mac binaries get signed (ad hoc), which Apple
# Silicon requires. Output: dist/
set -eu
cd "$(dirname "$0")/.."
rm -rf dist && mkdir -p dist
for rid in win-x64 osx-arm64 osx-x64 linux-x64; do
  dotnet publish src/rounds-port -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:ContinuousIntegrationBuild=true \
    -o "src/rounds-port/obj/publish/$rid" --nologo -v quiet
  ext=""; [ "$rid" = win-x64 ] && ext=".exe"
  cp "src/rounds-port/obj/publish/$rid/rounds-port$ext" "dist/rounds-port-$rid$ext"
done
cp src/rounds-port/embedded/HotReload.dll src/rounds-port/embedded/HotReload.pdb dist/
(cd dist && shasum -a 256 rounds-port-* HotReload.* > SHA256SUMS.txt)
ls -lh dist

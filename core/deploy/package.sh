#!/bin/sh
# Builds vigil-core.tar.gz: the self-contained Linux build of vigil-core plus the dashboard's
# static build beside it, ready to unpack into /opt/vigil-core on the host. Runs anywhere the
# .NET SDK and node are installed, Windows included (under Git Bash): nothing in it is native.
set -eu
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/.package/vigil-core"
rm -rf "$root/.package" && mkdir -p "$out"

(cd "$root/web" && npm ci && npm run build)
cp -r "$root/web/build" "$out/web"

# Self-contained: the host needs no .NET runtime. Single-file, so /opt/vigil-core holds one
# binary and the web directory. Not trimmed: ASP.NET and System.Text.Json's reflection paths
# are not trim-safe, and the size saved is not worth a runtime failure on the NAS.
dotnet publish "$root/core/Vigil.Core" -c Release -r linux-x64 --self-contained \
	-p:PublishSingleFile=true -p:AssemblyName=vigil-core -o "$out"

cp "$root/core/deploy/vigil-core.service" "$root/core/deploy/vigil-core.env" "$out/"
tar -czf "$root/vigil-core.tar.gz" -C "$root/.package" vigil-core
rm -rf "$root/.package"
echo "wrote $root/vigil-core.tar.gz"

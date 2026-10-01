#!/bin/sh
# Builds vigil-core.tar.gz: the self-contained Linux build of vigil-core plus the dashboard's
# static build beside it, ready to unpack into /opt/vigil-core on the host. Runs anywhere the
# .NET SDK and node are installed, Windows included (under Git Bash): nothing in it is native.
#
# linux-x64 (glibc), for the Debian guest vigil-core runs in. Self-contained still leaves a few
# native libraries to the system; on Debian they are all in a standard install:
#   apt install libstdc++6 libgcc-s1 libssl3 zlib1g ca-certificates
# libssl3 and ca-certificates are for TLS (ntfy, and Postgres if it asks for TLS). No libicu:
# InvariantGlobalization is on.
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
# Modes and ownership set explicitly rather than taken from the filesystem: on Windows there
# are no Unix permission bits to take, and the binary would arrive without its execute bit.
archive="$root/vigil-core.tar"
rm -f "$archive" "$archive.gz"
tar -cf "$archive" --owner=0 --group=0 --numeric-owner --mode='u=rwX,go=rX' 	--exclude=vigil-core/vigil-core -C "$root/.package" vigil-core
tar -rf "$archive" --owner=0 --group=0 --numeric-owner --mode=0755 -C "$root/.package" vigil-core/vigil-core
gzip -9 "$archive"
rm -rf "$root/.package"
echo "wrote $root/vigil-core.tar.gz"

#!/bin/sh
# Builds vigil-web and produces vigil-web.tar.gz: the built app plus its
# production node_modules, ready to unpack into /opt/vigil-web in the container.
set -eu
cd "$(dirname "$0")/.."
npm ci
npm run build
rm -rf .package && mkdir -p .package/vigil-web
cp -r build package.json package-lock.json .package/vigil-web/
(cd .package/vigil-web && npm ci --omit=dev --ignore-scripts)
cp deploy/vigil-web.service deploy/vigil-web.env .package/vigil-web/
tar -czf ../vigil-web.tar.gz -C .package vigil-web
rm -rf .package
echo "wrote $(realpath ../vigil-web.tar.gz)"

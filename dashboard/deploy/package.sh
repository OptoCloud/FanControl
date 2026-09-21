#!/bin/sh
# Builds the dashboard and produces fancontrol-dashboard.tar.gz: the built app plus its
# production node_modules, ready to unpack into /opt/fancontrol-dashboard in the container.
set -eu
cd "$(dirname "$0")/.."
npm ci
npm run build
rm -rf .package && mkdir -p .package/fancontrol-dashboard
cp -r build package.json package-lock.json .package/fancontrol-dashboard/
(cd .package/fancontrol-dashboard && npm ci --omit=dev --ignore-scripts)
cp deploy/fancontrol-dashboard.service deploy/fancontrol-dashboard.env .package/fancontrol-dashboard/
tar -czf ../fancontrol-dashboard.tar.gz -C .package fancontrol-dashboard
rm -rf .package
echo "wrote $(realpath ../fancontrol-dashboard.tar.gz)"

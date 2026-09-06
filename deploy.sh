#!/bin/bash
set -e

PROJ="/home/atom/dev/MediaBox2026/MediaBox2026/MediaBox2026.csproj"
PUBLISH_DIR="/home/atom/dev/MediaBox2026/publish"
PROD_DIR="/home/atom/MediaBox2026"

echo "Building..."
dotnet publish "$PROJ" -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained false -o "$PUBLISH_DIR"

echo "Deploying to $PROD_DIR..."
# Stop -> deploy -> start, not restart. The publish above has already run, so the
# service is only down for the rsync; swapping a binary under a live process is
# what this ordering exists to avoid.
echo "Stopping service..."
sudo systemctl stop mediabox

# -rlptDv preserves perms/times/symlinks but NOT owner/group, avoiding benign
# chgrp "Operation not permitted" errors that previously made rsync exit 23 and
# abort the deploy (under set -e) before the service restart.
rsync -rlptDv --exclude='data/' --exclude='appsettings.Secrets.json' --exclude='appsettings.json' \
  "$PUBLISH_DIR/" "$PROD_DIR/"

echo "Setting execute permissions..."
chmod +x "$PROD_DIR/MediaBox2026"

echo "Starting service..."
sudo systemctl start mediabox

echo "Done. Status:"
sudo systemctl status mediabox --no-pager -l

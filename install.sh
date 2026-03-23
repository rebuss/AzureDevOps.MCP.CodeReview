#!/usr/bin/env bash
set -euo pipefail

REPO="rebuss/CodeReview.MCP"
TOOL_ID="CodeReview.MCP"
CMD="rebuss-pure"

echo "Fetching latest release from GitHub..."

RELEASE=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")
VERSION=$(echo "$RELEASE" | grep '"tag_name"' | sed 's/.*"v\([^"]*\)".*/\1/')
ASSET_URL=$(echo "$RELEASE" | grep '"browser_download_url"' | grep '\.nupkg' | head -1 | sed 's/.*"\(https[^"]*\)".*/\1/')
ASSET_NAME=$(basename "$ASSET_URL")

if [ -z "$ASSET_URL" ]; then
    echo "Error: No .nupkg found in the latest release. Aborting." >&2
    exit 1
fi

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Downloading $ASSET_NAME..."
curl -fsSL -o "$TMP_DIR/$ASSET_NAME" "$ASSET_URL"

# Uninstall previous version if present
if dotnet tool list -g | grep -q "$TOOL_ID"; then
    echo "Uninstalling previous version..."
    dotnet tool uninstall -g "$TOOL_ID"
fi

echo "Installing $TOOL_ID $VERSION..."
dotnet tool install -g "$TOOL_ID" --add-source "$TMP_DIR" --version "$VERSION"

echo ""
echo "Installation complete. Run: $CMD --help"

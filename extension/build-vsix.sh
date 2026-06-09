#!/bin/bash
# If on Linux/macOS, run: chmod +x build-vsix.sh
set -e

cd "$(dirname "$0")"

echo "=== Cocoa VS Code Extension Packager ==="

echo "Checking Node.js..."
if ! command -v node &> /dev/null; then
    echo "Node.js is not installed. Please install Node.js 16+."
    exit 1
fi
echo "Node.js $(node --version)"

echo "Installing dependencies..."
npm install

echo "Compiling TypeScript..."
npx tsc -p ./

echo "Removing old .vsix..."
rm -f *.vsix 2>/dev/null

echo "Packaging extension..."
npx -y @vscode/vsce package

VSIX_FILE=$(ls -t *.vsix 2>/dev/null | head -1)
if [ -n "$VSIX_FILE" ]; then
    echo "=== Generated: $VSIX_FILE ==="
fi

#!/bin/bash

# Vars
slndir="$(dirname "${BASH_SOURCE[0]}")"

# Restore + Build
dotnet build "$slndir/Cocoa.Interactive" --nologo || exit

# Run
dotnet run -p "$slndir/Cocoa.Interactive" --no-build

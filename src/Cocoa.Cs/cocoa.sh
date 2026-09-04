#!/bin/bash

# Vars
slndir="$(dirname "${BASH_SOURCE[0]}")"

# Restore + Build
dotnet build "$slndir/Cli/Cocoa.Cli" --nologo || exit

# Run
dotnet run -p "$slndir/Cli/Cocoa.Cli" --no-build -- "$@"

#!/bin/bash

# Vars
slndir="$(dirname "${BASH_SOURCE[0]}")"

# Restore + Build
dotnet build "$slndir/Tools/Cli/Cocoa.Cli" --nologo || exit

# Run
dotnet run -p "$slndir/Tools/Cli/Cocoa.Cli" --no-build -- "$@"

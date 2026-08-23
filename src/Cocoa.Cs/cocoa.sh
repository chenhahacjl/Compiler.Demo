#!/bin/bash

# Vars
slndir="$(dirname "${BASH_SOURCE[0]}")"

# Restore + Build
dotnet build "$slndir/Cocoa.Compiler" --nologo || exit

# Run
dotnet run -p "$slndir/Cocoa.Compiler" --no-build -- "$@"

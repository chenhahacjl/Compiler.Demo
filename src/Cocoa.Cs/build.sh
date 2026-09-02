#!/bin/bash

# Vars
slndir="$(dirname "${BASH_SOURCE[0]}")"

# Restore + Build
dotnet build "$slndir/Cocoa.slnx" --nologo || exit

# Test
dotnet test "$slndir/Cocoa.Tests" --nologo --no-build

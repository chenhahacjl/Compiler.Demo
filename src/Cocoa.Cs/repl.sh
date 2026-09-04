#!/bin/bash

# Delegate to the main CLI in interactive (REPL) mode
exec "$(dirname "${BASH_SOURCE[0]}")/cocoa.sh" --interactive "$@"

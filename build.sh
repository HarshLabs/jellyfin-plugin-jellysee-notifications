#!/bin/sh
set -eu
cd "$(dirname "$0")"
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --no-restore
python3 -m unittest discover -s scripts -p 'test_*.py'

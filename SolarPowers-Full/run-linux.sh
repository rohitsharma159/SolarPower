#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  echo ".NET SDK not found. Install the .NET 8 SDK, then run this script again."
  exit 1
fi
dotnet restore
dotnet run --urls http://localhost:5080

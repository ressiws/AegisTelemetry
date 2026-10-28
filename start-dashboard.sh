#!/bin/bash
echo "Starting AegisTelemetry Web Dashboard (Port 5050)..."
cd "$(dirname "$0")/dashboard"
dotnet run -c Release

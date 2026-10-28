@echo off
title AegisTelemetry Calibration Dashboard
echo ========================================================
echo  Starting AegisTelemetry Web Dashboard (Port 5050)...
echo ========================================================
cd /d "%~dp0dashboard"
dotnet run -c Release
pause

@echo off
rem run-pilot.cmd <match|online> <profile> <minutes> <log>: a pilot run, detached from the caller
cd /d %~dp0
set PYTHONUTF8=1
set ROUNDS_PILOT_MINUTES=%3
py -3 -u pilot.py %1 %2 > %4 2>&1

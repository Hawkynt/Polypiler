@echo off
setlocal EnableExtensions EnableDelayedExpansion
set /a "total=7"
for /l %%I in (3,1,1) do (
  set /a "total=total+!neverbound!"
)
exit /b %total%

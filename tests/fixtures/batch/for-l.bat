@echo off
setlocal EnableExtensions DisableDelayedExpansion
set /a "total=0"
for /l %%I in (5,-2,1) do set /a "total=total+%%I"
exit /b %total%

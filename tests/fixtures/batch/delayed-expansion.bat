@echo off
setlocal EnableExtensions EnableDelayedExpansion
set /a "n=1"
set /a "total=0"
for /l %%I in (1,1,3) do (
  set /a "n=n+%%I"
  set /a "total=total+10*!n!+%n%"
)
setlocal DisableDelayedExpansion
set /a "n=9"
endlocal
set /a "total=total+!n!"
exit /b %total%

@echo off
setlocal EnableExtensions DisableDelayedExpansion
set /a "n=6"
set /a "sum=0"
:again
if %n% LEQ 0 goto done
set /a "sum=sum+n*2"
set /a "n=n-1"
goto again
:done
exit /b %sum%

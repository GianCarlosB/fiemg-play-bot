@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ----- Localiza o compilador C# in-box do Windows (.NET Framework) -----
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERRO] csc.exe nao encontrado. E necessario .NET Framework 4.x ^(ja vem no Windows^).
  pause
  exit /b 1
)

echo Compilando FIEMGPlayBot.exe ...
"%CSC%" /nologo /noconfig /optimize+ /target:exe /out:FIEMGPlayBot.exe ^
  /r:mscorlib.dll /r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll ^
  FIEMGPlayBot.cs

if errorlevel 1 (
  echo.
  echo [ERRO] Falha na compilacao.
  pause
  exit /b 1
)

echo Compilado com sucesso. Executando...
echo.
FIEMGPlayBot.exe
echo.
pause

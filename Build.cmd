@ECHO off
SETLOCAL

REM Parse timestamp
FOR /f "tokens=2 delims==" %%I IN ('wmic os get localdatetime /format:list') DO SET TIMESTAMP=%%I
SET TIMESTAMP=%TIMESTAMP:~0,8%.%TIMESTAMP:~8,6%

REM Parse command line arg if present
SET ARG1=%~1

REM If command line arg present, set the BUILD_CONFIG
REM otherwise, prompt the user
IF NOT "%ARG1%" == "" SET BUILD_CONFIG=%ARG1:~-1%
IF "%ARG1%" == "" SET /P BUILD_CONFIG=Please select the build configuration to use (r = Release, d = Debug [Default]):

REM Covert build config flag to an actual config string
if "%BUILD_CONFIG%" == "r" (
  SET BUILD_CONFIG=Release
) else (
  SET BUILD_CONFIG=Debug
)

REM Load the build version from the version.txt file
SET /P BUILD_VERSION=<version.txt

REM If configured for debug mode, append an '-alpha+timestamp' 
REM suffix to the version number
IF "%BUILD_CONFIG%" == "Debug" (
  SET BUILD_VERSION=%BUILD_VERSION%-alpha+%TIMESTAMP%
)

REM Trigger the build
CALL Build\Tools\NuGet\NuGet.exe restore Source\TeaCommerce.PaymentProviders.sln
CALL "%programfiles(x86)%\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\amd64\MsBuild.exe" Build\Project.Build.xml

ENDLOCAL

IF %ERRORLEVEL% NEQ 0 GOTO err
EXIT /B 0
:err
PAUSE
EXIT /B 1


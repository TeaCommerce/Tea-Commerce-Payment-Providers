ECHO off

REM SET /P RELEASE_MODE=Select which mode to compile in (r = Release, d = Debug [Default]):

REM if "%RELEASE_MODE%" == "r" (
REM   SET RELEASE_MODE="Release"
REM ) else (
REM   SET RELEASE_MODE="Debug"
REM )

SET RELEASE_MODE="Release"

CALL Build\Tools\NuGet\NuGet.exe restore Source\TeaCommerce.PaymentProviders.sln
CALL "%programfiles(x86)%\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\amd64\MsBuild.exe" Build\Project.Build.xml /property:Configuration=%RELEASE_MODE%

@IF %ERRORLEVEL% NEQ 0 GOTO err
@EXIT /B 0
:err
@PAUSE
@EXIT /B 1
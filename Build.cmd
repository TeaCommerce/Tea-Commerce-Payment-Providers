ECHO off

SET /P BUILD_VERSION=Please enter your package version (e.g. 1.0.5):
SET /P BUILD_CONFIG=Please select the build configuration to use (r = Release [Default], d = Debug):

if "%BUILD_VERSION%" == "" (
  SET BUILD_VERSION=0.0.0
)

if "%BUILD_CONFIG%" == "d" (
   SET BUILD_CONFIG=Debug
) else (
   SET BUILD_CONFIG=Release
)

CALL Build\Tools\NuGet\NuGet.exe restore Source\TeaCommerce.PaymentProviders.sln
CALL "%programfiles(x86)%\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\amd64\MsBuild.exe" Build\Project.Build.xml

@IF %ERRORLEVEL% NEQ 0 GOTO err
@EXIT /B 0
:err
@PAUSE
@EXIT /B 1
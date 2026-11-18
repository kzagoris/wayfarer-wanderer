@REM Licensed to the Apache Software Foundation (ASF) under the Apache License, Version 2.0.
@echo off
setlocal
set SCRIPT_DIR=%~dp0
for /f "tokens=1,* delims==" %%A in (%SCRIPT_DIR%\.mvn\wrapper\maven-wrapper.properties) do (
  if "%%A"=="distributionUrl" set DISTRIBUTION_URL=%%B
  if "%%A"=="distributionSha256Sum" set DISTRIBUTION_SHA=%%B
)
if not defined JAVA_HOME (
  echo JAVA_HOME is not set. Choose JDK 21+ and set JAVA_HOME, then retry. 1>&2
  exit /b 1
)
if not exist "%JAVA_HOME%\bin\java.exe" (
  echo JAVA_HOME does not name a JDK. Choose JDK 21+ and retry. 1>&2
  exit /b 1
)
set MAVEN_HOME=%LOCALAPPDATA%\mvn-wrapper\maven-{{MAVEN_VERSION}}
if not exist "%MAVEN_HOME%\bin\mvn.cmd" (
  powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; $root='%MAVEN_HOME%'; $parent=Split-Path $root; $zip=\"$root.zip\"; New-Item -ItemType Directory -Force $parent | Out-Null; try { Invoke-WebRequest -UseBasicParsing -Uri '%DISTRIBUTION_URL%' -OutFile $zip; $sha=[Security.Cryptography.SHA256]::Create(); try { $actual=[BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($zip))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }; if ($actual -ne '%DISTRIBUTION_SHA%') { throw 'Maven download checksum mismatch' }; [IO.Compression.ZipFile]::ExtractToDirectory($zip,$parent); $found=Get-Item \"$parent\apache-maven-{{MAVEN_VERSION}}\"; if ($found.FullName -ne $root) { Move-Item $found.FullName $root -Force } } catch { Write-Error $_; exit 1 } finally { Remove-Item -Force $zip -ErrorAction SilentlyContinue }"
  if errorlevel 1 (
    echo Maven download failed. Reconnect, verify access to the Maven repository, then retry mvnw.cmd. 1>&2
    exit /b 2
  )
)
call "%MAVEN_HOME%\bin\mvn.cmd" %*
endlocal
exit /b %ERRORLEVEL%

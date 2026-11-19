@rem Copyright 2015 the original authors.
@rem Licensed under the Apache License, Version 2.0.
@rem
@echo off
setlocal
if defined WAYFARER_WRAPPER_MARKER >>"%WAYFARER_WRAPPER_MARKER%" echo gradle
set APP_HOME=%~dp0
if defined JAVA_HOME goto findJavaFromJavaHome
set JAVA_EXE=java.exe
%JAVA_EXE% -version >NUL 2>&1
if "%ERRORLEVEL%" == "0" goto execute
echo ERROR: JAVA_HOME is not set and no java command could be found. Choose JDK 21+ and retry. 1>&2
exit /b 1

:findJavaFromJavaHome
set JAVA_EXE=%JAVA_HOME%\bin\java.exe
if exist "%JAVA_EXE%" goto execute
echo ERROR: JAVA_HOME points to an invalid JDK. Choose JDK 21+ and retry. 1>&2
exit /b 1

:execute
set CLASSPATH=%APP_HOME%\gradle\wrapper\gradle-wrapper.jar
"%JAVA_EXE%" %DEFAULT_JVM_OPTS% %JAVA_OPTS% %GRADLE_OPTS% "-Dorg.gradle.appname=gradlew" -classpath "%CLASSPATH%" org.gradle.wrapper.GradleWrapperMain %*
if errorlevel 1 echo Gradle failed. Check the connection and cached dependencies, then run this command again. 1>&2
endlocal
exit /b %ERRORLEVEL%


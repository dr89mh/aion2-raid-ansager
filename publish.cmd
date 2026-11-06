@echo off
rem Baut dist\RaidAnsager.exe: eine Datei, .NET enthalten, keine Installation noetig (Npcap muss installiert sein).
cd /d "%~dp0"
dotnet publish RaidAnsager.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -o dist || exit /b 1
copy /y LIESMICH.md dist\LIESMICH.md >nul
echo.
echo Fertig: %~dp0dist\RaidAnsager.exe

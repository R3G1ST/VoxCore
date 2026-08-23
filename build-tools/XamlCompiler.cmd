@echo off
set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
set LANG=en_US
set LC_ALL=en_US
"%USERPROFILE%\.nuget\packages\microsoft.windowsappsdk.winui\2.3.6\tools\net472\XamlCompiler.exe" %*

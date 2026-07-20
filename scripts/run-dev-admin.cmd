@echo off
set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project "%~dp0..\src\IPRO.Admin\IPRO.Admin.csproj" --urls http://localhost:5050

@echo off
set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project "%~dp0..\src\IPRO.Web\IPRO.Web.csproj" --urls http://localhost:5000

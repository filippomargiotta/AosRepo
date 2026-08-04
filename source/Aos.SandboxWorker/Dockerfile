FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src
COPY Aos.Core/Aos.Core.csproj Aos.Core/
COPY Aos.SandboxWorker/Aos.SandboxWorker.csproj Aos.SandboxWorker/
RUN dotnet restore Aos.SandboxWorker/Aos.SandboxWorker.csproj
COPY Aos.Core/ Aos.Core/
COPY Aos.SandboxWorker/ Aos.SandboxWorker/
RUN dotnet publish Aos.SandboxWorker/Aos.SandboxWorker.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:68d35011fe04a39cca38208d392ed48f2df15653633dca16dbc4582d07342b9f
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Aos.SandboxWorker.dll"]

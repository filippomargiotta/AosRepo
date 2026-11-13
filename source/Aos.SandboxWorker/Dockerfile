FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src
COPY Aos.Core/Aos.Core.csproj Aos.Core/
COPY Aos.RepositoryFixture/Aos.RepositoryFixture.csproj Aos.RepositoryFixture/
COPY Aos.SandboxWorker/Aos.SandboxWorker.csproj Aos.SandboxWorker/
RUN dotnet restore Aos.SandboxWorker/Aos.SandboxWorker.csproj
COPY Aos.Core/ Aos.Core/
COPY Aos.RepositoryFixture/ Aos.RepositoryFixture/
COPY Aos.SandboxWorker/ Aos.SandboxWorker/
RUN dotnet publish Aos.SandboxWorker/Aos.SandboxWorker.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:11.0@sha256:3fe5f5495141c97986a49d3a4e76061f6225c3f93f94e8a7e522781376193863
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Aos.SandboxWorker.dll"]

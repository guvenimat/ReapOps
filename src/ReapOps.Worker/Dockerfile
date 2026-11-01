FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY ReapOps.Domain/ReapOps.Domain.csproj ReapOps.Domain/
COPY ReapOps.Application/ReapOps.Application.csproj ReapOps.Application/
COPY ReapOps.Infrastructure/ReapOps.Infrastructure.csproj ReapOps.Infrastructure/
COPY ReapOps.Worker/ReapOps.Worker.csproj ReapOps.Worker/
RUN dotnet restore ReapOps.Worker/ReapOps.Worker.csproj -r linux-x64
COPY ReapOps.Domain/ ReapOps.Domain/
COPY ReapOps.Application/ ReapOps.Application/
COPY ReapOps.Infrastructure/ ReapOps.Infrastructure/
COPY ReapOps.Worker/ ReapOps.Worker/
RUN dotnet publish ReapOps.Worker/ReapOps.Worker.csproj -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["./ReapOps.Worker"]

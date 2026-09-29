FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["NuGet.Config", "."]
COPY ["src/DevBrain.Core/DevBrain.Core.csproj", "src/DevBrain.Core/"]
COPY ["src/DevBrain.Server/DevBrain.Server.csproj", "src/DevBrain.Server/"]
RUN dotnet restore "src/DevBrain.Server/DevBrain.Server.csproj"
COPY ["src/DevBrain.Core/", "src/DevBrain.Core/"]
COPY ["src/DevBrain.Server/", "src/DevBrain.Server/"]
RUN dotnet publish "src/DevBrain.Server/DevBrain.Server.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DevBrain.Server.dll"]

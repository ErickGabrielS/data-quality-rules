FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DataQualityRules.Api/DataQualityRules.Api.csproj src/DataQualityRules.Api/
RUN dotnet restore src/DataQualityRules.Api/DataQualityRules.Api.csproj
COPY src/DataQualityRules.Api/ src/DataQualityRules.Api/
RUN dotnet publish src/DataQualityRules.Api/DataQualityRules.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Rules="Data Source=/data/rules.db"
RUN mkdir -p /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "DataQualityRules.Api.dll"]

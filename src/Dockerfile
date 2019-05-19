FROM mcr.microsoft.com/dotnet/core/sdk:2.2.105 as sdk
WORKDIR /app
COPY *.csproj .
COPY *.cs ./
RUN dotnet restore
RUN dotnet publish -c Release -o ../build

FROM mcr.microsoft.com/dotnet/core/runtime:2.2 as runtime
COPY --from=sdk build .
ENTRYPOINT [ "dotnet", "MessageDelivery.dll" ]
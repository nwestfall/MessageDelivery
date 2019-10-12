FROM mcr.microsoft.com/dotnet/core/sdk:3.0.100 as sdk
WORKDIR /app
COPY *.csproj .
COPY *.cs ./
RUN dotnet restore
RUN dotnet publish -c Release -o ../build

FROM mcr.microsoft.com/dotnet/core/runtime:3.0 as runtime
COPY --from=sdk build .
ENTRYPOINT [ "dotnet", "MessageDelivery.dll" ]
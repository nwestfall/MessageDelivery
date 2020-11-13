FROM mcr.microsoft.com/dotnet/sdk:5.0 as sdk
WORKDIR /app
COPY *.csproj .
COPY *.cs ./
RUN dotnet restore
RUN dotnet publish -c Release -o ../build

FROM mcr.microsoft.com/dotnet/runtime:5.0 as runtime
COPY --from=sdk build .
ENTRYPOINT [ "dotnet", "MessageDelivery.dll" ]
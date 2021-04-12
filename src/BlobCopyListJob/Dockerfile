FROM mcr.microsoft.com/dotnet/sdk:5.0 as installer-env
COPY . /src/

RUN cd /src && \
    mkdir -p /home/app && \
    dotnet publish *.csproj --output /home/app

FROM mcr.microsoft.com/dotnet/runtime:5.0
COPY --from=installer-env ["/home/app", "/home/app"]

WORKDIR /home/app
ENTRYPOINT ["dotnet", "BlobCopyListJob.dll"]
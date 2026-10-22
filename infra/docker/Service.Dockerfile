# One image definition for every HTTP service. The service is selected by the
# SERVICE build argument, so adding a service means adding a compose entry,
# not another Dockerfile. Same convention as the sibling SuggestX and JameX
# projects.
#
#   docker build -f infra/docker/Service.Dockerfile --build-arg SERVICE=Payment .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src

# Restore from project files alone, so editing source does not invalidate the
# (slow) package restore layer.
COPY src/shared/PayX.Contracts/PayX.Contracts.csproj               src/shared/PayX.Contracts/
COPY src/shared/PayX.ServiceDefaults/PayX.ServiceDefaults.csproj   src/shared/PayX.ServiceDefaults/
COPY src/services/PayX.Gateway/PayX.Gateway.csproj                 src/services/PayX.Gateway/
COPY src/services/PayX.Identity/PayX.Identity.csproj               src/services/PayX.Identity/
COPY src/services/PayX.Payment/PayX.Payment.csproj                 src/services/PayX.Payment/
COPY src/services/PayX.Vault/PayX.Vault.csproj                     src/services/PayX.Vault/
COPY src/services/PayX.PspSimulator/PayX.PspSimulator.csproj       src/services/PayX.PspSimulator/
RUN dotnet restore "src/services/PayX.${SERVICE}/PayX.${SERVICE}.csproj"

COPY src/ src/
RUN dotnet publish "src/services/PayX.${SERVICE}/PayX.${SERVICE}.csproj" \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SERVICE
WORKDIR /app
COPY --from=build /app ./

# Started without a shell. `sh -c "exec dotnet …"` (the obvious way to pick
# the DLL from a variable) silently drops environment variables whose names
# aren't valid shell names, e.g. ReverseProxy__…__payment-1__Address, so
# compose's overrides never reached the Gateway (found while setting this up).
# The publish output's native launcher has the DLL name built in, so a copy
# under a fixed name runs the right service, as PID 1 (docker stop's SIGTERM
# reaches it and shutdown is graceful), with the environment untouched.
RUN cp "PayX.${SERVICE}" service
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["/app/service"]

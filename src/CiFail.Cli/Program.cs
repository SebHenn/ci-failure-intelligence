using CiFail.Cli;

#if CIFAIL_EXTERNAL_DB
// Full/Docker build: make the external database providers selectable. The slim default
// binary is compiled without this symbol and stays SQLite-only.
CiFail.Providers.ExternalProviders.RegisterAll();
#endif

// Everything else lives in CliApp so the same app can be built by tests.
return CliApp.Run(args);

using CiFail.Cli.Commands;
using Spectre.Console.Cli;

#if CIFAIL_EXTERNAL_DB
// Full/Docker build: make the external database providers selectable. The slim default
// binary is compiled without this symbol and stays SQLite-only.
CiFail.Providers.ExternalProviders.RegisterAll();
#endif

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("cifail");

    config.AddCommand<AnalyzeCommand>("analyze")
        .WithDescription("Read a build/test log and explain what broke and how to fix it.")
        .WithExample("analyze", "build.log")
        .WithExample("analyze", "--json", "test.log")
        .WithExample("analyze", "--type", "dotnet", "build.log");

    config.AddCommand<HistoryCommand>("history")
        .WithDescription("List the failures you've analyzed before (or show one by its number).")
        .WithExample("history")
        .WithExample("history", "42");

    config.AddCommand<ResolveCommand>("resolve")
        .WithDescription("Save how you fixed a failure, so cifail can remind you next time.")
        .WithExample("resolve", "42", "--note", "\"Fixed the package name typo\"");

    config.AddCommand<ReconcileCommand>("reconcile")
        .WithDescription("Auto-resolve past failures that no longer happen at the current commit.")
        .WithExample("reconcile");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Install git hooks so cifail auto-resolves fixed failures on each commit.")
        .WithExample("init");

    config.AddBranch("rules", rules =>
    {
        rules.SetDescription("See the failure patterns cifail knows about.");
        rules.AddCommand<RulesListCommand>("list")
            .WithDescription("List every pattern cifail can recognize.");
    });
});

return app.Run(args);

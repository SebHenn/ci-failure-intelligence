using CiFail.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("cifail");

    config.AddCommand<AnalyzeCommand>("analyze")
        .WithDescription("Analyze a CI/build/test log: what broke and how to fix it.")
        .WithExample("analyze", "build.log")
        .WithExample("analyze", "--json", "test.log")
        .WithExample("analyze", "--type", "dotnet", "build.log");

    config.AddCommand<HistoryCommand>("history")
        .WithDescription("Browse past analyses, or show one by id.")
        .WithExample("history")
        .WithExample("history", "42");

    config.AddCommand<ResolveCommand>("resolve")
        .WithDescription("Record how a past failure was fixed.")
        .WithExample("resolve", "42", "--note", "\"Fixed the package name typo\"");

    config.AddBranch("rules", rules =>
    {
        rules.SetDescription("Inspect and manage rule packs.");
        rules.AddCommand<RulesListCommand>("list")
            .WithDescription("List all loaded rules (embedded + user packs).");
    });
});

return app.Run(args);

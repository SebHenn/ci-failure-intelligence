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
});

return app.Run(args);

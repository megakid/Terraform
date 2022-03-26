using Spectre.Console.Cli;

var app = new CommandApp<TerraformMoveInteractiveCommand>();
return app.Run(args);

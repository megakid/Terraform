using Spectre.Console;
using Spectre.Console.Cli;
using System;

var app = new CommandApp<TerraformMoveInteractiveCommand>();
app.Configure(configure =>
{
	configure.PropagateExceptions();
	configure.AddCommand<TerraformMoveInteractiveCommand>("move");
	configure.AddCommand<TerraformPlanTargetInteractiveCommand>("target");
});

try
{
	return app.Run(args);
}
catch (Exception ex)
{
	AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
	return -99;
}

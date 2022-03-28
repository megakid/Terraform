using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

internal sealed class TerraformMoveInteractiveCommand : AsyncCommand<TerraformMoveInteractiveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("tfplan json file to scan.  Use 'terraform plan -out plan.tfplan' then 'terraform show -json .\\plan.tfplan > plan.tfplan.json' to generate.")]
        [CommandOption("--tfplan")]
        public string? TfPlanPath { get; init; }

        [Description("Path to your Terraform project.  This mode will generate the plan for you.")]
        [CommandOption("--tfdir")]
        public string? TfDir { get; init; }

        [CommandOption("-e|--execute")]
        [Description("Offer to execute 'terraform state mv' commands. If TfPlanPath specified, the command assumes TfPlanPath is in your tf project directory.")]
        [DefaultValue(false)]
        public bool Execute { get; init; }


        public override ValidationResult Validate()
        {
            if (TfPlanPath is not null && TfDir is not null)
                return ValidationResult.Error("You must choose --tfplan or --tfdir, not both.");
            if (TfPlanPath is null && TfDir is null)
                return ValidationResult.Error("You must choose either --tfplan or --tfdir.");
            
            if (TfPlanPath is not null && !File.Exists(TfPlanPath))
                return ValidationResult.Error($"File not found: {TfPlanPath}");
            else if (TfDir is not null && !Directory.Exists(TfDir))
                return ValidationResult.Error($"Directory not found: {TfDir}");

            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        AnsiConsole.MarkupLine("[gold3_1]Terramove - move terraform resources interactively.[/]");

        if (settings.TfPlanPath is not null)
        {
            return await ExecuteWithPlan(settings.TfPlanPath, settings.Execute, Path.GetDirectoryName(settings.TfPlanPath!)!);
        }
        else
        {
            return await ExecuteWithDir(settings.TfDir!, settings.Execute);
        }
    }

    private async Task<int> ExecuteWithPlan(string tfPlanPath, bool execute, string tfDir)
    {
        var json = await File.ReadAllTextAsync(tfPlanPath);

        return await ExecuteWithJson(json, execute, tfDir);
    }

    private async Task<int> ExecuteWithDir(string tfDir, bool execute)
    {
        // create temporary file
        var randomFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tfPlanPath = randomFile + ".tfplan";
        var tfPlanJsonPath = randomFile + ".tfplan.json";
        // create tf plan json
        await SimpleExec.Command.RunAsync("terraform", $"-chdir={tfDir} plan -input=false -out {tfPlanPath}", noEcho: true, createNoWindow: true);
        var (stdOut, stdErr) = await SimpleExec.Command.ReadAsync("terraform", $"-chdir={tfDir} show -json -no-color {tfPlanPath}");

        return await ExecuteWithJson(stdOut, execute, tfDir);
    }
    
    private async Task<int> ExecuteWithJson(string json, bool execute, string? tfDir = null)
    {

        var jd = JsonDocument.Parse(json);

        var deletedResources = new Dictionary<string, ResourceDeletion>();
        var addedResources = new Dictionary<string, ResourceAdded>();

        foreach (var je in jd.RootElement.GetProperty("resource_changes").EnumerateArray())
        {
            if (je.TryGetProperty("action_reason", out var actionReason))
            {
                if (actionReason.GetString() == "delete_because_no_resource_config")
                {
                    var address = je.GetProperty("address").GetString()!;
                    deletedResources.Add(address,
                        new ResourceDeletion(
                            Address: address,
                            Type: je.GetProperty("type").GetString()!,
                            Name: je.GetProperty("name").GetString()!,
                            ProviderName: je.GetProperty("provider_name").GetString()!,
                            DeleteReason: je.GetProperty("action_reason").GetString()!,
                            Before: je.GetProperty("change").GetProperty("before")
                        ));
                }
            }
            else if (je.TryGetProperty("change", out var change) && change.TryGetProperty("actions", out var changeActions) && changeActions.EnumerateArray().Any(e => e.GetString() == "create"))
            {
                var address = je.GetProperty("address").GetString()!;
                addedResources.Add(address,
                    new ResourceAdded(
                        Address: address,
                        Type: je.GetProperty("type").GetString()!,
                        Name: je.GetProperty("name").GetString()!,
                        ProviderName: je.GetProperty("provider_name").GetString()!,
                        After: je.GetProperty("change").GetProperty("after")
                    ));
            }
        }

        var moves = new List<(string from, string to)>();

        var anyMovable = false;
        foreach (var resource in deletedResources.Values)
        {
            var choices = addedResources.Values
                .Where(e => !moves.Any(m => m.to == e.Address) && e.ProviderName == resource.ProviderName && e.Type == resource.Type)
                .OrderBy(add => SimilarityScore(resource, add))
                .Select(add => new Choice(add))
                .ToArray();
                        
            if (choices.Length > 0) 
            {
                anyMovable = true;
                var moveTo = AnsiConsole.Prompt(
                    new SelectionPrompt<Choice>()
                        .Title($"Do you want to move missing resource [red]{resource.Address.EscapeMarkup()}[/]?")
                        .PageSize(10)
                        .MoreChoicesText("[grey](Move up and down to reveal more added resources)[/]")
                        .AddChoices(choices.Concat(new[] { new Choice() }))
                    );

                if (!moveTo.SkipResource)
                {
                    AnsiConsole.MarkupLine($"Moving [red]{resource.Address.EscapeMarkup()}[/] to [green]{moveTo.Resource!.Address.EscapeMarkup()}[/]");
                    moves.Add((resource.Address, moveTo.Resource!.Address));
                }
            }
        }

        double SimilarityScore(ResourceDeletion before, ResourceAdded after)
        {
            // TODO: Be way smarter about this
            return Quickenshtein.Levenshtein.GetDistance(before.Address, after.Address);
        }

        if (moves.Count == 0)
        {
            if (anyMovable)
            {
                AnsiConsole.MarkupLine("[gold3_1]No resources moved.  All skipped...[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[gold3_1]No resources found that can be moved.[/]");
            }
            return 0;
        }

        foreach (var move in moves)
        {
            AnsiConsole.MarkupLine($"terraform state mv {move.from} {move.to}".Replace("\"", "\\\"").EscapeMarkup());
        }

        if (execute)
        {
            if (AnsiConsole.Confirm("Are you sure you want to execute the above commands? [red]This will edit your terraform state. Make sure you know what you're doing![/]", defaultValue: false))
            {
                foreach (var move in moves)
                {
                    AnsiConsole.MarkupLine($"Moving [red]{move.from.EscapeMarkup()}[/] to [green]{move.to.EscapeMarkup()}[/]");
                    await SimpleExec.Command.RunAsync("terraform", $"-chdir={tfDir} state mv {move.from.Replace("\"", "\\\"")} {move.to.Replace("\"", "\\\"")}", noEcho: true, createNoWindow: true);
                }
            }
        }

        return 0;
    }

    class Choice
    {
        public Choice()
        {
        }
        
        public Choice(ResourceAdded resource)
        {
            Resource = resource;
        }
        

        public ResourceAdded? Resource { get; }

        public bool SkipResource => Resource is null;

        public override string ToString()
        {
            if (Resource is not null)
            {
                return $"{Resource.Address}".EscapeMarkup();// [grey]({Resource.After})[/]";
            }
            else
            {
                return "(Skip Resource)";
            }
        }
    }


    record Resource(string Address, string Type, string Name, string ProviderName);
    record ResourceAdded(string Address, string Type, string Name, string ProviderName, JsonElement After) : Resource(Address, Type, Name, ProviderName);
    record ResourceDeletion(string Address, string Type, string Name, string ProviderName, string DeleteReason, JsonElement Before) : Resource(Address, Type, Name, ProviderName);
}

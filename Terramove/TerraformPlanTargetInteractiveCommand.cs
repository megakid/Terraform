using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

internal sealed class TerraformPlanTargetInteractiveCommand : AsyncCommand<TerraformPlanTargetInteractiveCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[Description("Path to your Terraform project.")]
		[CommandOption("--tfdir")]
		[DefaultValue(".")]
		public string? TfDir { get; init; }

		[CommandOption("-e|--execute")]
		[Description("Offer to execute 'terraform state mv' commands. If TfPlanPath specified, the command assumes TfPlanPath is in your tf project directory.")]
		[DefaultValue(false)]
		public bool Execute { get; init; }

		[CommandOption("-b|--binary")]
		[Description("Instead of using terraform, use another binary such as terragrunt.  Defaults to 'terraform'.")]
		[DefaultValue("terraform")]
		public string Binary { get; init; }


		public override ValidationResult Validate()
		{
			if (string.IsNullOrWhiteSpace(TfDir))
				return ValidationResult.Error("--tfdir is mandatory.");

			if (TfDir is not null && !Directory.Exists(TfDir))
				return ValidationResult.Error($"Directory not found: {TfDir}");

			return ValidationResult.Success();
		}
	}

	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
	{
		AnsiConsole.MarkupLine("[gold3_1]Terramove - move terraform resources interactively.[/]");

		return await ExecuteWithDir(settings.Binary, settings.TfDir!, settings.Execute);
	}

	public class Node
	{
		static string[] ColourLevels = new string[] { "red", "green", "yellow", "cyan", "orange3", "white"};

		public string Name { get; set; }
		public List<Node> Children { get; set; } = new List<Node>();
		public Node? Parent { get; set; }

		public Node(string name) => Name = name;

		public Node? FindChild(string name) => Children.FirstOrDefault(child => child.Name == name);

		public bool AllLeafDescendantsInList(List<Node> nodeList)
		{
			foreach (var child in Children)
			{
				var isLeaf = child.Children.Count == 0;
				if (isLeaf)
				{
					if (!nodeList.Contains(child))
					{
						return false;
					}
				}
				else // This node is not a leaf, so we check its children
				{
					if (!child.AllLeafDescendantsInList(nodeList))
					{
						return false;
					}
				}
			}
			return true;
		}

		public override string ToString() => $"[{ColourLevels[Depth % ColourLevels.Length]}]{ResourceName.EscapeMarkup()}[/]";
		public override bool Equals(object? obj) => obj is Node node && FullResourceName == node.FullResourceName;
		public override int GetHashCode() => HashCode.Combine(FullResourceName);

		public string FullResourceName => (Parent != null ? $"{Parent.FullResourceName}{(Name.StartsWith('[') ? "" : ".")}{Name}" : Name);
		public string ResourceName => Name; //.StartsWith('[') ? $"{Parent!.ResourceName}{Name}" : Name;
		public int Depth => Parent != null ? Parent.Depth + 1 : 0;

	}
	public static void BuildHierarchy(string input, List<Node> roots)
	{
		// Match any word characters with (up-to) a single period, or match anything between brackets
		const string SplitPattern = @"([\w][\w\-]*[\.]?[\w\-]+|\[.*?\])";

		// Use Regex.Split to split the string by the pattern
		string[] segments = Regex.Matches(input, SplitPattern)
							   .Select(m => m.Value)
							   .ToArray();

		Node? currentNode = null;
		for (int i = 0; i < segments.Length; i += 1)
		{
			var nodeName = segments[i];

			if (currentNode == null)
			{
				currentNode = roots.FirstOrDefault(root => root.Name == nodeName);
				if (currentNode == null)
				{
					currentNode = new Node(nodeName);
					roots.Add(currentNode);
				}
			}
			else
			{
				var childNode = currentNode.FindChild(nodeName);
				if (childNode == null)
				{
					childNode = new Node(nodeName);
					currentNode.Children.Add(childNode);
					childNode.Parent = currentNode;
				}
				currentNode = childNode;
			}
		}

	}
	private async Task<int> ExecuteWithDir(string binary, string tfDir, bool execute)
	{
		string stdOut = "";
		await AnsiConsole.Status()
			.StartAsync("[green]Retreiving state...[/]", async ctx =>
			{
				(stdOut, _) = await SimpleExec.Command.ReadAsync(binary, $"state list", workingDirectory: tfDir);
			});
		
		var prompt = new MultiSelectionPrompt<Node>()
				.Title("What resources do you want to target?")
				.Required()
				.PageSize(25)
				.Mode(SelectionMode.Leaf)
				.HighlightStyle(new Style(background: Color.Grey30))
				.MoreChoicesText("[grey](Move up and down to reveal more)[/]")
				.InstructionsText(
					"[grey](Press [blue]<space>[/] to toggle, " +
					"[green]<enter>[/] to accept)[/]");

		var stateList = stdOut
			.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			// Remove data items
			.Where(resource => !(resource.StartsWith("data.") || resource.Contains(".data.")));

		var roots = new List<Node>();

		foreach (var str in stateList)
		{
			BuildHierarchy(str, roots);
		}


		void FlattenHierarchy(Node node)
		{
			if (node.Children.Count == 1)
			{
				var child = node.Children[0];
				node.Name = $"{node.Name}{(child.Name.StartsWith('[') ? "" : ".")}{child.Name}";
				node.Children = child.Children;
			}

			foreach (var child in node.Children)
			{
				FlattenHierarchy(child);
			}
		}

		foreach (var root in roots)
		{
			FlattenHierarchy(root);
		}

		// Build UI prompt tree.
		void AddChoices(Node node, ISelectionItem<Node> parent)
		{
			foreach (var item in node.Children)
			{
				var choice = parent.AddChild(item);
				AddChoices(item, choice);
			}
		}

		foreach (var root in roots)
		{
			var choice = prompt.AddChoice(root);
			AddChoices(root, choice);
		}

		// This returns just the leaf nodes so we need to 'factorize' them by grouping by the highest common parent whos leaf ancestors are all selected
		var selectedResources = AnsiConsole.Prompt(prompt);

		var factorizedList = Factorize(selectedResources);

		var targetArgs = factorizedList.Select(res => $"--target={res.FullResourceName.Escape()}");

		AnsiConsole.MarkupLine($"{binary} plan {string.Join(' ', targetArgs)}".EscapeMarkup());

		return 0;
	}


	public static List<Node> Factorize(List<Node> selectedNodes)
	{
		var result = new HashSet<Node>();
		// Build a dictionary where each entry is a node and its direct children from the selectedNodes list
		foreach (var node in selectedNodes)
		{
			var validNode = node;
			var parent = node.Parent;

			// Go up the parents until we find one that doesn't have all leafs in the list.
			while (parent != null && parent.AllLeafDescendantsInList(selectedNodes))
			{
				validNode = parent;
				parent = parent.Parent;
			}

			result.Add(validNode);
		}
		return result.ToList();
	}
}

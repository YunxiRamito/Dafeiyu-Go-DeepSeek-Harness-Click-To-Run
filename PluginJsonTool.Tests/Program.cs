using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DshPluginJsonTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: PluginJsonTool.Tests <owner/repo> <output.json>");
            return 2;
        }

        List<string> inputErrors;
        List<RepositoryEntry> entries = RepositoryInputParser.Parse(
            args[0],
            out inputErrors);
        if (entries.Count == 0)
        {
            Console.Error.WriteLine(String.Join(Environment.NewLine, inputErrors));
            return 3;
        }

        using (PluginRepositoryParser parser =
            new PluginRepositoryParser(
                new ProxyConfiguration(),
                message => Console.Error.WriteLine(message)))
        {
            ParseOutcome outcome = await parser.ParseAsync(
                entries,
                System.Threading.CancellationToken.None);
            if (outcome.Errors.Count > 0)
            {
                Console.Error.WriteLine(
                    String.Join(Environment.NewLine, outcome.Errors));
            }

            if (outcome.Items.Count == 0)
            {
                return 4;
            }

            string outputPath = Path.GetFullPath(args[1]);
            string json = JsonOutputBuilder.BuildList(outcome.Items);
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            Console.WriteLine(outputPath);
            return 0;
        }
    }
}

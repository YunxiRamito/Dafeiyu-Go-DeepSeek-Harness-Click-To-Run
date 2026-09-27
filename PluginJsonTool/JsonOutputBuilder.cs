using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshPluginJsonTool
{
    internal static class JsonOutputBuilder
    {
        private static readonly JsonSerializerOptions Options =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        internal static string BuildList(IReadOnlyList<ParsedPlugin> items)
        {
            JsonArray array = new JsonArray();
            if (items != null)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    array.Add(BuildItem(items[index], index + 1, false));
                }
            }

            JsonObject root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["items"] = array
            };
            return root.ToJsonString(Options);
        }

        internal static string BuildSingle(ParsedPlugin item)
        {
            JsonObject root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["plugin"] = BuildItem(item, 0, true)
            };
            return root.ToJsonString(Options);
        }

        private static JsonObject BuildItem(
            ParsedPlugin item,
            int order,
            bool singleImport)
        {
            JsonObject result = new JsonObject
            {
                ["owner"] = item.Owner,
                ["repository"] = item.Repository
            };

            if (singleImport)
            {
                result["repositoryUrl"] =
                    "https://github.com/" + item.Owner + "/" + item.Repository;
            }

            result["description"] = item.Description;
            result["language"] = item.Language;
            result["license"] = item.License;
            result["pushedAt"] = item.PushedAt;
            result["category"] = item.Category;
            result["version"] = item.Version;
            result["stars"] = item.Stars;
            result["verified"] = item.Verified;
            result["defaultBranch"] = item.DefaultBranch;
            result["installStatus"] = item.InstallStatus;
            result["selectedSpecifier"] = item.SelectedSpecifier;
            result["selectedSource"] = item.SelectedSource;
            result["installCandidates"] = BuildCandidates(
                item.InstallCandidates);
            result["sourceSha"] = item.SourceSha;
            result["imageUrl"] = item.ImageUrl;
            result["note"] = item.Note;
            if (!singleImport)
            {
                result["order"] = order;
            }

            return result;
        }

        private static JsonArray BuildCandidates(
            IReadOnlyList<PluginInstallCandidate> candidates)
        {
            JsonArray result = new JsonArray();
            if (candidates == null)
            {
                return result;
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                PluginInstallCandidate candidate = candidates[index];
                result.Add(new JsonObject
                {
                    ["source"] = candidate.Source,
                    ["target"] = candidate.Target,
                    ["action"] = String.IsNullOrWhiteSpace(candidate.Action)
                        ? "add"
                        : candidate.Action,
                    ["specifier"] = candidate.Specifier,
                    ["executable"] = candidate.Executable,
                    ["evidenceSource"] = candidate.EvidenceSource
                });
            }

            return result;
        }
    }
}

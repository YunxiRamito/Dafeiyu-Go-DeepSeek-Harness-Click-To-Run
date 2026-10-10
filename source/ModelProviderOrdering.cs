using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeekHarnessLauncher
{
    internal static class ModelProviderOrdering
    {
        internal static List<string> Apply(IEnumerable<string> available, IEnumerable<string> saved)
        {
            var providers = available.Where(id => !String.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var remaining = new HashSet<string>(providers, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string id in saved ?? Array.Empty<string>())
                if (id != null && remaining.Remove(id))
                    result.Add(providers.First(provider => String.Equals(provider, id, StringComparison.OrdinalIgnoreCase)));
            foreach (string id in providers)
                if (remaining.Remove(id)) result.Add(id);
            return result;
        }
    }
}

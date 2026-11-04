using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Luma.Island.Settings;

/// <summary>Navigation and local draft hooks; no external modules or executable code are loaded.</summary>
public sealed class SettingsSectionRegistry
{
    private readonly ObservableCollection<SettingsSectionDefinition> _sections = new();
    private readonly Dictionary<string, SettingsSectionDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    public ReadOnlyObservableCollection<SettingsSectionDefinition> Sections { get; }
    public SettingsSectionRegistry() => Sections = new(_sections);

    public void Register(SettingsSectionDefinition section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!_byId.TryAdd(section.Id, section)) throw new ArgumentException("A section with this ID is already registered.", nameof(section));
        _sections.Add(section);
    }

    public SettingsSectionDefinition? Find(string id)
        => id is not null && _byId.TryGetValue(id, out var section) ? section : null;

    public IReadOnlyList<SettingsSectionDefinition> Search(string? query)
    {
        string[] terms = Normalize(query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return _sections.Where(section =>
        {
            string text = Normalize(section.Title + " " + section.Description + " " + section.Keywords);
            return terms.All(term => text.Contains(term, StringComparison.Ordinal));
        }).ToArray();
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant().Replace('ё', 'е');
}

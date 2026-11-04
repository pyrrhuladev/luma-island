using System;
using System.Windows;

namespace Luma.Island.Settings;

/// <summary>A local settings page. Content is constructed once, on first navigation.</summary>
public sealed class SettingsSectionDefinition
{
    private readonly Func<FrameworkElement> _factory;
    private FrameworkElement? _content;
    private readonly Action? _onClosed;
    private bool _released;
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Keywords { get; }
    public Action? CaptureDraft { get; }
    public Func<string?>? Validate { get; }
    public bool IsContentCreated => _content is not null;

    public SettingsSectionDefinition(string id, string title, string description, string keywords,
        Func<FrameworkElement> contentFactory, Action? captureDraft = null, Func<string?>? validate = null, Action? onClosed = null)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] is < 'a' or > 'z')
            throw new ArgumentException("Section ID must start with a lower-case ASCII letter.", nameof(id));
        foreach (char value in id)
            if (!(value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                throw new ArgumentException("Section ID contains an unsupported character.", nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Section title is required.", nameof(title));
        Id = id; Title = title.Trim(); Description = description?.Trim() ?? string.Empty;
        Keywords = keywords ?? string.Empty;
        _factory = contentFactory ?? throw new ArgumentNullException(nameof(contentFactory));
        CaptureDraft = captureDraft; Validate = validate;
        _onClosed = onClosed;
    }

    public FrameworkElement GetContent()
        => _content ??= _factory() ?? throw new InvalidOperationException("The settings section returned no content.");

    public void ReleaseContent()
    {
        if (_released) return;
        _released = true;
        if (_content is not null) _onClosed?.Invoke();
    }
}

namespace Bookies.Models;

/// <summary>The add and edit forms are the same shape, so they share one binding target.</summary>
public sealed class BookmarkInput
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public bool Private { get; set; } = true;

    /// <summary>Set when the form was opened by the bookmarklet, so it can close itself on save.</summary>
    public bool Popup { get; set; }
}

/// <summary>Body of the tag suggestion fetch made by the add and edit forms.</summary>
public sealed record SuggestTagsInput(string? Url, string? Title, string? Description);

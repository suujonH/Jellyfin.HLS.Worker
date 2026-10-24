namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The choices of drop-downs that change with the settings, shared by every row that shows them. A list is replaced rather
/// than changed, and the rows are told, so a drop-down never sees its choice taken out and put back.
/// </summary>
/// <typeparam name="T">A choice; a record, so a choice of a list made anew equals the one of the list before.</typeparam>
internal sealed class OptionList<T>
{
    /// <summary>Raised after <see cref="Items"/> is replaced.</summary>
    public event Action? Changed;

    /// <summary>Gets the choices.</summary>
    public IReadOnlyList<T> Items { get; private set; } = [];

    /// <summary>Replaces the choices.</summary>
    /// <param name="items">The choices.</param>
    public void Set(IReadOnlyList<T> items)
    {
        Items = items;
        Changed?.Invoke();
    }
}

/// <summary>A directory a line of a scheme can be.</summary>
/// <param name="ConnectionId">The connection, or <see cref="Configuration.SchemeEntry.JellyfinLibrary"/>.</param>
/// <param name="Label">What is shown.</param>
/// <param name="Type">The type of the connection; <c>null</c> for the folder of the library.</param>
internal sealed record TargetOption(Guid ConnectionId, string Label, Configuration.ConnectionType? Type);

/// <summary>A scheme to choose, or none when <paramref name="Id"/> is <c>null</c>.</summary>
/// <param name="Id">The scheme.</param>
/// <param name="Name">What is shown.</param>
internal sealed record SchemeOption(Guid? Id, string Name)
{
    /// <summary>The choice of no scheme.</summary>
    public static readonly SchemeOption None = new(null, "(无)");
}

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The dialogs the window shows for the view models.
/// </summary>
internal interface IDialogs
{
    /// <summary>
    /// Shows the dialog of an addition or of a start, which loads what it shows.
    /// </summary>
    /// <param name="dialog">The dialog.</param>
    /// <returns>Whether it was confirmed; its choices are then in it.</returns>
    Task<bool> ShowAddDialogAsync(AddDialogViewModel dialog);

    /// <summary>
    /// Asks for a confirmation.
    /// </summary>
    /// <param name="message">What is asked.</param>
    /// <param name="accept">The button that confirms.</param>
    /// <param name="cancel">The button that does not.</param>
    /// <returns>Whether it was confirmed.</returns>
    Task<bool> ConfirmAsync(string message, string accept, string cancel);
}

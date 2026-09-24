using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace FreePolarAlign.App.Services;

/// <summary>
/// Asks the user where to save a frame. An interface so the view model can be
/// tested without a window: the dialog is the one part of saving that cannot
/// run headless.
/// </summary>
public interface IFrameSaveTarget
{
    /// <summary>A stream to write the frame to, or null when the user cancelled.</summary>
    Task<Stream?> OpenAsync(string suggestedFileName);
}

/// <summary>The platform's own save dialog, through the window's storage provider.</summary>
public sealed class StorageProviderFrameSaveTarget : IFrameSaveTarget
{
    private static readonly FilePickerFileType Fits = new("FITS image")
    {
        Patterns = new[] { "*.fits", "*.fit", "*.fts" },
        MimeTypes = new[] { "image/fits", "application/fits" },
    };

    private readonly TopLevel _owner;

    public StorageProviderFrameSaveTarget(TopLevel owner) =>
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<Stream?> OpenAsync(string suggestedFileName)
    {
        IStorageFile? file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save frame",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "fits",
            FileTypeChoices = new[] { Fits },
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);

        return file is null ? null : await file.OpenWriteAsync().ConfigureAwait(true);
    }
}

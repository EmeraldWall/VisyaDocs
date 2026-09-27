using Windows.Storage.Pickers;

namespace VisyaDocs.App.Services;

/// <summary>File and folder pickers bound to the main window (required for unpackaged apps).</summary>
public static class Pickers
{
    public static async Task<string?> OpenFileAsync(params string[] extensions)
    {
        var picker = Init(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(params string[] extensions)
    {
        var picker = Init(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary });
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToArray();
    }

    public static async Task<string?> SaveFileAsync(string suggestedName, string typeName, string extension)
    {
        var picker = Init(new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = extension,
        });
        picker.FileTypeChoices.Add(typeName, [extension]);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = Init(new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary });
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static T Init<T>(T picker) where T : class
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        return picker;
    }
}

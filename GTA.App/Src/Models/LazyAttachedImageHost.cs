using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace GTA.App;

public sealed class LazyAttachedImageHost
{
    private readonly Image  _image;
    private readonly string _path;
    private bool _loaded;

    public LazyAttachedImageHost(Image image, string path)
    {
        _image = image;
        _path  = path;
    }

    /// <summary>Decode the image from disk and show it now, if not already loaded.</summary>
    public void Load()
    {
        if (_loaded) return;
        try
        {
            _image.Source = new Bitmap(_path);
            _loaded = true;
        }
        catch
        {
        }
    }

    /// <summary>Dispose the decoded Bitmap and clear it out of memory, if currently loaded.</summary>
    public void Unload()
    {
        if (!_loaded) return;
        (_image.Source as IDisposable)?.Dispose();
        _image.Source = null;
        _loaded = false;
    }
}

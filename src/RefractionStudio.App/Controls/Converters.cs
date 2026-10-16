using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace RefractionStudio.App.Controls;

/// <summary>Looks up an application resource (e.g. an icon geometry) by its key.</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static readonly ResourceKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key && Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var resource) ? resource : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the value's text equals the converter parameter (used for style classes).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a number with the format given as parameter (invariant culture).</summary>
public sealed class NumberFormatConverter : IValueConverter
{
    public static readonly NumberFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is IFormattable f ? f.ToString(parameter as string ?? "G", CultureInfo.InvariantCulture) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Decodes a base64 PNG/JPEG into a bitmap for previews (null on invalid data).</summary>
public sealed class Base64ImageConverter : IValueConverter
{
    public static readonly Base64ImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } text) return null;
        try
        {
            using var ms = new MemoryStream(System.Convert.FromBase64String(text));
            return new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

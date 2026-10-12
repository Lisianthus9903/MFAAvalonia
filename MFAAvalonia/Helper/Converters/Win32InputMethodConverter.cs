using Avalonia.Data.Converters;
using MaaFramework.Binding;
using Newtonsoft.Json;
using System;
using System.Globalization;

namespace MFAAvalonia.Helper.Converters;

// Maa.Framework 5.10 predates this enum member. Its UInt64 ABI already forwards
// the value unchanged to MaaWin32ControllerCreate. MaaDef.h v5.14.3 defines 1<<10.
public sealed class Win32InputMethodConverter : UniversalEnumConverter<Win32InputMethod>, IValueConverter
{
    public const Win32InputMethod AnchoredTouch = (Win32InputMethod)(1UL << 10);

    public static string GetName(Win32InputMethod value) =>
        value == AnchoredTouch ? nameof(AnchoredTouch) : value.ToString();

    public static bool TryParse(string value, out Win32InputMethod result)
    {
        if (string.Equals(value, nameof(AnchoredTouch), StringComparison.OrdinalIgnoreCase))
        {
            result = AnchoredTouch;
            return true;
        }
        return Enum.TryParse(value, ignoreCase: true, out result);
    }

    public override Win32InputMethod ReadJson(JsonReader reader, Type objectType,
        Win32InputMethod existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String && TryParse(reader.Value?.ToString() ?? "", out var value))
            return value;
        return base.ReadJson(reader, objectType, existingValue, hasExistingValue, serializer);
    }

    public override void WriteJson(JsonWriter writer, Win32InputMethod value, JsonSerializer serializer) =>
        writer.WriteValue(GetName(value));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Win32InputMethod method ? GetName(method) : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

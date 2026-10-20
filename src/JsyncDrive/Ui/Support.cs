using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace JsyncDrive.Ui;

/// <summary>Placeholder text for a TextBox: <c>ui:Hint.Text="Search"</c>.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new PropertyMetadata(""));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);
}

/// <summary>Text in the language of the app. The English text is the key; a missing translation shows it as is.</summary>
public static class Loc
{
    public static string Language { get; private set; } = "en";

    public static void Use(string setting)
    {
        var l = setting == "auto" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        Language = l == "es" ? "es" : "en";
    }

    public static string T(string english) =>
        Language == "es" && Spanish.Map.TryGetValue(english, out var es) ? es : english;

    public static string T(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);
}

/// <summary>XAML: <c>Text="{ui:T 'Send a file'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public string Text { get; set; } = "";
    public TExtension() { }
    public TExtension(string text) => Text = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}

public static class Theme
{
    private static ResourceDictionary? _colors;

    /// <summary>"system", "light" or "dark".</summary>
    public static void Apply(string setting)
    {
        var dark = setting == "dark" || (setting != "light" && SystemUsesDark());
        var uri = new Uri($"pack://application:,,,/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml");
        var next = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;
        // The first call also has to drop the light colours App.xaml starts with.
        foreach (var old in merged.Where(d => d.Source?.OriginalString.Contains("Colors.") == true).ToList()) merged.Remove(old);
        if (_colors != null) merged.Remove(_colors);
        merged.Insert(0, next);
        _colors = next;
        IsDark = dark;
        Changed?.Invoke();
    }

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static bool SystemUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

public static class Fmt
{
    public static string Size(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 45) return Loc.T("just now");
        if (d.TotalMinutes < 60) return Loc.T("{0} min ago", Math.Max(1, (int)d.TotalMinutes));
        if (d.TotalHours < 24 && t.Date == DateTime.Today) return Loc.T("today {0}", t.ToString("t"));
        if (t.Date == DateTime.Today.AddDays(-1)) return Loc.T("yesterday {0}", t.ToString("t"));
        return t.ToString("d MMM yyyy HH:mm");
    }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value != null && !(value is string s && s.Length == 0)) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

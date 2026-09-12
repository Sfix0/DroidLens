using System.ComponentModel;

namespace DroidLens.Client.Services;

/// <summary>
/// Simple key-value localization.
/// Strings live in Assets/Locales/{lang}.lang — a lightweight, hand-editable
/// format (see ParseLangFile below) chosen specifically so a user can add or
/// tweak a translation with a plain text editor, no rebuild required, and
/// still leave "# comment" lines in the file to visually group related keys
/// (e.g. "# ─── Settings ───" above all settings.* keys).
/// Falls back to "en" if a key is missing.
/// Supported languages are detected automatically from the files present
/// in Assets/Locales, so a user can add a new language by just dropping
/// a new {lang}.lang file next to the others.
/// </summary>
public class LocalizationService : INotifyPropertyChanged
{
    public string CurrentLanguage { get; private set; } = "en";

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();

    private const string FallbackLanguage = "en";
    private const string FileExtension = ".lang";

    private static readonly string LocalesDir = Path.Combine(
        AppContext.BaseDirectory, "Assets", "Locales");

    /// <summary>
    /// Language codes discovered from *.lang files in Assets/Locales.
    /// "en" is always included, even if the file is somehow missing,
    /// so there is always a safe fallback.
    /// </summary>
    public static string[] SupportedLanguages => DiscoverLanguages();

    /// <summary>
    /// Display name for a language code, resolved from that language's own
    /// .lang file (the "lang.name" endonym key) — so each file only declares
    /// its own name, not the whole catalog of every other language. Falls
    /// back to the raw code when the file or key is missing (covers a
    /// freshly-dropped .lang that hasn't been given a lang.name yet).
    /// </summary>
    public static string GetLanguageDisplayName(string code)
    {
        string key = "lang.name";
        var file = LoadFile(code);
        return file.TryGetValue(key, out var name) && name.Length > 0 ? name : code;
    }

    private static string[] DiscoverLanguages()
    {
        try
        {
            if (!Directory.Exists(LocalesDir))
                return new[] { FallbackLanguage };

            var codes = Directory.GetFiles(LocalesDir, $"*{FileExtension}")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return codes.Length > 0 ? codes : new[] { FallbackLanguage };
        }
        catch
        {
            return new[] { FallbackLanguage };
        }
    }

    public void Load(string language)
    {
        var supported = SupportedLanguages;
        CurrentLanguage = supported.Contains(language) ? language : FallbackLanguage;
        _fallback = LoadFile(FallbackLanguage);
        _strings  = CurrentLanguage == FallbackLanguage ? _fallback : LoadFile(CurrentLanguage);

        // "{Binding Loc[some.key]}" in the views is an indexer binding.
        // Reflection-based (non-compiled) Avalonia bindings resolve
        // "Loc[key]" as: get MainViewModel.Loc, then read its indexer — and
        // to know the indexer's value can change, Avalonia subscribes
        // directly to PropertyChanged on that Loc instance (since it
        // implements INotifyPropertyChanged), listening for the indexer's
        // conventional property name "Item" (matching the getter's compiler-
        // generated name for `this[string key]`) — not "Item[]", and not a
        // notification from MainViewModel. Previously Loc had no
        // INotifyPropertyChanged at all, so nothing ever told those indexer
        // bindings to re-read Get() after Load() — MainViewModel.SetLanguage's
        // own OnPropertyChanged(string.Empty) only refreshes bindings that
        // are resolved directly against MainViewModel, not a live
        // subscription on the nested Loc object. Firing "Item" here is what
        // actually makes every "{Binding Loc[key]}" across both windows
        // refresh immediately on language change instead of only picking up
        // the new strings the next time something unrelated forces a rebuild
        // (e.g. app restart).
        OnPropertyChanged("Item");
    }

    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out var val)) return val;
        if (_fallback.TryGetValue(key, out var fb)) return fb;
        return key; // return key as last resort
    }

    // Shorthand
    public string this[string key] => Get(key);

    private static Dictionary<string, string> LoadFile(string lang)
    {
        try
        {
            string path = Path.Combine(LocalesDir, $"{lang}{FileExtension}");

            if (!File.Exists(path)) return new();
            return ParseLangFile(File.ReadAllLines(path));
        }
        catch { return new(); }
    }

    /// <summary>
    /// Parses the simple ".lang" format:
    ///   # comment line — starts with '#' (leading whitespace allowed), ignored entirely.
    ///   blank line — ignored.
    ///   key = value — everything up to the FIRST '=' is the key (trimmed),
    ///                 everything after is the value (trimmed at both ends only,
    ///                 so inner spacing is preserved). Lines with no '=' are
    ///                 skipped rather than throwing, so one malformed line
    ///                 (e.g. a user typo) doesn't break the whole file.
    ///   Comments are purely visual for humans grouping related keys — they are
    ///   not tied to any key programmatically.
    /// </summary>
    private static Dictionary<string, string> ParseLangFile(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            int eq = line.IndexOf('=');
            if (eq < 0) continue; // malformed line, skip rather than throw

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            if (key.Length == 0) continue;

            result[key] = value;
        }

        return result;
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
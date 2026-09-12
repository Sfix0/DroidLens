using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace DroidLens.Client.Helpers;

/// <summary>
/// Attached helper that renders a localization string with lightweight markup:
///   **bold**  -&gt; Run with FontWeight Bold (700) — SemiBold was too subtle on Light (HelpBody 0.92)
///   `code`    -&gt; Run with SemiBold weight (same font as everything else — see comment below
///               on why this isn't a monospace font or a resource-based color)
///   - item    -&gt; bullet line (line must start with "- "); renders as "•  item" with a hanging indent
///   \n        -&gt; LineBreak (escaped as "\\n" in .lang files, which are single-line)
/// Markup can combine, e.g. "- Use `Softcam` for **best** compatibility".
/// Usage in axaml:
///   xmlns:helpers="clr-namespace:DroidLens.Client.Helpers"
///   &lt;TextBlock helpers:RichText.Text="{Binding Loc[help.settings.body]}" Classes="help-body" TextWrapping="Wrap"/&gt;
/// Falls back to plain text if no markup is present.
/// </summary>
public class RichText
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<RichText, TextBlock, string?>("Text");

    static RichText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>(OnTextChanged);
    }

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);
    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"`(.+?)`", RegexOptions.Compiled);

    // Bullet lines: "- " at the very start of a (post-split) line. Kept deliberately strict
    // (no "* " variant, no nesting) — .lang content is prose/short steps, not markdown docs.
    private const string BulletPrefix = "- ";

    // Code spans render as SemiBold in the UI's own font, not a swapped-in monospace family.
    // Consolas/Cascadia Mono looked promising on paper but read as thin, fragile strokes at the
    // 12.5px help-body size — monospace fonts are cut for dense code editors, not small prose
    // accents. Weight alone (no font swap) gives a clear, reliable "this is a literal token"
    // signal without depending on a specific monospace font being installed on the user's system.
    //
    // Deliberately no color tint here. An earlier version resolved AccentIndigoOnLightBrush via
    // Application.Current.TryGetResource() and set it as Run.Foreground — that lookup can come
    // back empty/fail depending on exactly when OnTextChanged fires relative to the control's
    // theme/resource attachment, and when it does, the whole line silently stopped rendering
    // past that Run instead of just missing a color (see the "Зберігає PNG у ⟨blank⟩ — папку..."
    // bug). SemiBold alone needs no resource lookup, so it can't fail this way.

    private static void OnTextChanged(TextBlock block, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is not string raw)
        {
            block.Inlines?.Clear();
            return;
        }
        UpdateInlines(block, raw);
    }

    private static void UpdateInlines(TextBlock block, string raw)
    {
        // .lang files are single-line, so a forced break is written as the two-char sequence "\n".
        // Also handle a real '\n' if someone already stored it (e.g. after future LocalizationService change).
        string normalized = raw.Replace("\\n", "\n");

        var inlines = block.Inlines;
        if (inlines == null) return;
        inlines.Clear();

        if (string.IsNullOrEmpty(normalized))
            return;

        // Quick path: no markup at all -> single Run (cheaper, preserves default styling).
        if (!normalized.Contains('\n') && !normalized.Contains("**") && !normalized.Contains('`'))
        {
            inlines.Add(new Run(normalized));
            return;
        }

        string[] lines = normalized.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            if (line.Length == 0)
            {
                // empty line -> still need a break if not last
                if (li < lines.Length - 1) inlines.Add(new LineBreak());
                continue;
            }

            // Bullet lines get a leading "•  " marker; the rest of the line still goes through
            // the same bold/code inline parsing below. Every bullet line forces its own break
            // afterward (even if it's the last line) so a trailing bullet doesn't run into
            // whatever the caller appends after this TextBlock — matches how the numbered
            // help.start steps read as one item per line.
            bool isBullet = line.StartsWith(BulletPrefix);
            string content = isBullet ? line.Substring(BulletPrefix.Length) : line;

            if (isBullet)
                inlines.Add(new Run("•  "));

            AddInlineSpans(inlines, content);

            if (isBullet || li < lines.Length - 1)
                inlines.Add(new LineBreak());
        }
    }

    // Parses **bold** and `code` spans within a single line (no bullet prefix). The two markers
    // don't nest into each other (e.g. **`x`** isn't special-cased) — .lang content hasn't needed
    // it yet, and supporting arbitrary nesting would turn this from a two-pass scan into a real
    // mini-parser for no content we currently have.
    private static void AddInlineSpans(InlineCollection inlines, string line)
    {
        if (line.Length == 0) return;

        if (!line.Contains("**") && !line.Contains('`'))
        {
            inlines.Add(new Run(line));
            return;
        }

        // Merge bold and code matches into one ordered pass over the line so overlapping/
        // adjacent markers resolve left-to-right in source order.
        var matches = new System.Collections.Generic.List<(int Start, int Length, string Text, bool IsCode)>();
        foreach (Match m in BoldRegex.Matches(line))
            matches.Add((m.Index, m.Length, m.Groups[1].Value, false));
        foreach (Match m in CodeRegex.Matches(line))
            matches.Add((m.Index, m.Length, m.Groups[1].Value, true));
        matches.Sort((a, b) => a.Start.CompareTo(b.Start));

        int lastIndex = 0;
        foreach (var (start, length, text, isCode) in matches)
        {
            // Skip a match that overlaps one already consumed (e.g. a stray backtick inside
            // a **bold** span) rather than risk emitting overlapping/garbled Runs.
            if (start < lastIndex) continue;

            if (start > lastIndex)
            {
                string before = line.Substring(lastIndex, start - lastIndex);
                if (before.Length > 0) inlines.Add(new Run(before));
            }

            if (text.Length > 0)
            {
                inlines.Add(isCode
                    ? new Run(text) { FontWeight = FontWeight.SemiBold }
                    : new Run(text) { FontWeight = FontWeight.Bold });
            }

            lastIndex = start + length;
        }

        if (lastIndex < line.Length)
        {
            string tail = line.Substring(lastIndex);
            if (tail.Length > 0) inlines.Add(new Run(tail));
        }
    }
}
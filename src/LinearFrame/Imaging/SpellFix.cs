using System.Globalization;
using System.Runtime.InteropServices;
using LinearFrame.Core;

namespace LinearFrame.Imaging;

/// <summary>
/// Puts Polish letters back into what PP-OCR read - its Latin-script model often drops the marks ("Wiecej", "informaci",
/// "Przesytka"). The spelling dictionary built into Windows (ISpellChecker: offline, the one Edge and Office use) knows
/// the words: a word it doesn't know, whose suggestion is the same word with its marks - or, for a lower-case word, with
/// one letter more or different - gets the suggestion. Names and codes are left alone: a capitalised word only ever gets
/// marks, words with digits, short or upper-case words nothing, and a word the English dictionary knows stays as it is.
/// </summary>
internal static unsafe class SpellFix
{
    private static readonly Guid ClsidFactory = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private static readonly Guid IidFactory = new("8E018A9D-2415-4677-BF08-794EA61F94BB");
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly object Gate = new();
    private static IntPtr _pl, _en;
    private static bool _tried;

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr obj);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

    private static void* Slot(IntPtr obj, int i) => (*(void***)obj)[i];
    private static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(p, 2))(p); }

    /// <summary>The words of a line PP-OCR read, with Polish letters put back where the dictionary is sure of them.</summary>
    public static List<OcrWord> Fix(List<OcrWord> words)
    {
        // the dictionaries live in the shared (multi-threaded) COM apartment: a UI thread hands the work over
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return Task.Run(() => Fix(words)).GetAwaiter().GetResult();
        lock (Gate)
        {
            try
            {
                Ensure();
                if (_pl == IntPtr.Zero) return words;
                var result = new List<OcrWord>(words.Count);
                for (int i = 0; i < words.Count; i++)
                {
                    var w = words[i];
                    string text = w.Text;
                    // the word itself, without punctuation around it
                    int a = 0, b = text.Length;
                    while (a < b && !char.IsLetterOrDigit(text[a])) a++;
                    while (b > a && !char.IsLetterOrDigit(text[b - 1])) b--;
                    var better = Better(text[a..b], afterNumber: i > 0 && words[i - 1].Text.Any(char.IsDigit));
                    result.Add(better == null ? w : w with { Text = text[..a] + better + text[b..] });
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn("Spelling fix: " + ex.Message);
                return words;
            }
        }
    }

    // three-letter words whose marks matter; any other three letters could be an abbreviation ("tel", "ul.")
    private static readonly HashSet<string> Short = new(StringComparer.Ordinal)
        { "się", "cię", "też", "już", "był", "mój", "mąż", "ból", "róg", "sól", "tył", "żal", "zła", "złe", "zły", "łza", "ząb", "nóż", "wąż", "róż", "dół" };

    private static string? Better(string core, bool afterNumber)
    {
        // "zł" after an amount: the Latin model reads it "zt" or "zl"
        if (afterNumber && core is "zt" or "zl") return "zł";
        if (core.Length < 3 || !core.All(c => char.IsLetter(c) || c == '-' || c == '\'')) return null;
        bool upper = core.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (upper && core.Length <= 5) return null; // a code or an abbreviation
        if (Knows(_pl, core) || _en != IntPtr.Zero && Knows(_en, core)) return null;
        bool capital = !upper && char.IsUpper(core[0]);
        foreach (var s in Suggest(_pl, core))
        {
            if (s.Length == 0 || s.Contains(' ')) continue;
            bool ok = MarksOnly(core, s)
                ? core.Length > 3 || Short.Contains(s.ToLower(Polish))
                // a letter the model swallowed ("informaci" → "informacji") - in plain lower-case words only; one letter
                // swapped for another is not taken (a name like "inpost" isn't the dictionary's "impost")
                : !capital && !upper && core.Length >= 6 && s.Length == core.Length + 1 && PaddleOcr.Distance(Plain(s), Plain(core)) == 1;
            if (!ok) continue;
            return upper ? s.ToUpper(Polish) : capital ? char.ToUpper(s[0], Polish) + s[1..] : s;
        }
        return null;
    }

    /// <summary>The same letters, only with marks added or different (Wiecej → Więcej; the model's t or l for ł).</summary>
    private static bool MarksOnly(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char x = char.ToLower(a[i], Polish), y = char.ToLower(b[i], Polish);
            if (x == y) continue;
            if (y == 'ł' && x is 'l' or 't') continue;
            if (Base(x) == Base(y)) continue;
            return false;
        }
        return true;
    }

    private static char Base(char c) => c == 'ł' ? 'l' : c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];

    private static string Plain(string s) => new(s.ToLower(Polish).Select(Base).ToArray());

    private static void Ensure()
    {
        if (_tried) return;
        _tried = true;
        CoInitializeEx(IntPtr.Zero, 0); // multi-threaded; "already" or "another mode" are fine too
        if (CoCreateInstance(ClsidFactory, IntPtr.Zero, 1, IidFactory, out var factory) != 0 || factory == IntPtr.Zero)
        {
            Log.Info("Spelling: no Windows spell checker");
            return;
        }
        try
        {
            _pl = Create(factory, "pl-PL");
            _en = Create(factory, "en-US");
        }
        finally { Release(factory); }
        Log.Info($"Spelling: Polish {(_pl != IntPtr.Zero ? "yes" : "no")}, English {(_en != IntPtr.Zero ? "yes" : "no")}");
    }

    /// <summary>ISpellCheckerFactory: IsSupported (4), CreateSpellChecker (5).</summary>
    private static IntPtr Create(IntPtr factory, string tag)
    {
        int supported = 0;
        IntPtr checker = IntPtr.Zero;
        fixed (char* t = tag)
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, int*, int>)Slot(factory, 4))(factory, t, &supported) != 0 || supported == 0) return IntPtr.Zero;
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(factory, 5))(factory, t, &checker) != 0) return IntPtr.Zero;
        }
        return checker;
    }

    /// <summary>ISpellChecker.Check (4) finds no error in the word.</summary>
    private static bool Knows(IntPtr checker, string word)
    {
        IntPtr errors = IntPtr.Zero;
        fixed (char* w = word)
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(checker, 4))(checker, w, &errors) != 0 || errors == IntPtr.Zero)
                return true; // can't tell: leave the word alone
        try
        {
            IntPtr error = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(errors, 3))(errors, &error); // IEnumSpellingError.Next
            if (hr == 0 && error != IntPtr.Zero)
            {
                Release(error);
                return false;
            }
            return true;
        }
        finally { Release(errors); }
    }

    /// <summary>ISpellChecker.Suggest (5): the dictionary's suggestions, best first.</summary>
    private static List<string> Suggest(IntPtr checker, string word, int max = 6)
    {
        var list = new List<string>();
        IntPtr e = IntPtr.Zero;
        fixed (char* w = word)
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(checker, 5))(checker, w, &e) != 0 || e == IntPtr.Zero)
                return list;
        try
        {
            while (list.Count < max)
            {
                IntPtr s = IntPtr.Zero;
                uint got = 0;
                // IEnumString.Next(1, &s, &got): S_FALSE with nothing when the list is over
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)Slot(e, 3))(e, 1, &s, &got) != 0 || got == 0) break;
                list.Add(Marshal.PtrToStringUni(s) ?? "");
                CoTaskMemFree(s);
            }
        }
        finally { Release(e); }
        return list;
    }
}

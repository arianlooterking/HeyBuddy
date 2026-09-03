using System.Text.RegularExpressions;

namespace Clicky.Core;

public enum ActionCompletionFamily
{
    AnyStateChange,
    OpenApplication,
    TypeText,
    Click,
    KeyPress,
    Navigate,
    WriteContent,
    Save,
    Create,
    Move,
    Copy,
    Rename,
    Delete,
    Send,
    Publish,
    Upload,
    Download,
    Run,
    Install
}

/// <summary>Evidence that must be present before an action request can be reported as complete.</summary>
public sealed record ActionCompletionRequirement(ActionCompletionFamily Family, string Description, string ToolHint)
{
    public bool IsSatisfiedBy(string toolName, RiskLevel effectiveRisk)
    {
        if (effectiveRisk == RiskLevel.ReadOnly || string.IsNullOrWhiteSpace(toolName))
            return false;
        if (Family == ActionCompletionFamily.TypeText)
            return string.Equals(toolName, "desktop_type", StringComparison.Ordinal);
        if (Family == ActionCompletionFamily.Click)
            return string.Equals(toolName, "desktop_click", StringComparison.Ordinal);
        if (Family == ActionCompletionFamily.KeyPress)
            return string.Equals(toolName, "desktop_key", StringComparison.Ordinal);
        if (Family == ActionCompletionFamily.OpenApplication &&
            toolName is "desktop_launch" or "desktop_activate")
            return true;

        var tokens = toolName.Split(['.', '_', '-', '/', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(params string[] expected) => tokens.Any(token => expected.Contains(token, StringComparer.OrdinalIgnoreCase));
        return Family switch
        {
            ActionCompletionFamily.AnyStateChange => true,
            ActionCompletionFamily.OpenApplication => Has("open", "launch", "activate"),
            ActionCompletionFamily.Navigate => string.Equals(toolName, "desktop_click", StringComparison.Ordinal) || Has("navigate", "goto", "visit", "open"),
            ActionCompletionFamily.WriteContent => Has("write", "append", "generate"),
            ActionCompletionFamily.Save => Has("save"),
            ActionCompletionFamily.Create => Has("create", "generate", "write"),
            ActionCompletionFamily.Move => Has("move"),
            ActionCompletionFamily.Copy => Has("copy"),
            ActionCompletionFamily.Rename => Has("rename"),
            ActionCompletionFamily.Delete => Has("delete", "remove"),
            ActionCompletionFamily.Send => Has("send"),
            ActionCompletionFamily.Publish => Has("publish", "post"),
            ActionCompletionFamily.Upload => Has("upload"),
            ActionCompletionFamily.Download => Has("download"),
            ActionCompletionFamily.Run => Has("run", "execute"),
            ActionCompletionFamily.Install => Has("install"),
            _ => false
        };
    }
}

/// <summary>Classifies only the user's own request; attached documents and tool output must never be passed here.</summary>
public static partial class ActionIntent
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Space();

    [GeneratedRegex(@"^(?:(?:hey\s*buddy)[,:\s]+)?(?:(?:please|kindly|first)[,\s]+)*(?:(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?|(?:i(?:'d| would)\s+like|i\s+want)\s+you\s+to\s+|go\s+ahead\s+and\s+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWrapper();

    [GeneratedRegex(@"^(?:how|why|what|when|where|who|which|is|are|was|were|do|does|did|should|may|might|can\s+(?:i|we)|could\s+(?:i|we)|would\s+(?:i|we)|do\s+not|don't|never)\b|^(?:explain|describe|summari[sz]e|analy[sz]e|compare|review|brainstorm|outline|draft|rewrite|translate|answer|tell\s+me|help\s+me\s+understand|show\s+me\s+how)\b|^send\s+me\s+(?:(?:a|an|the)\s+)?(?:draft|summary|explanation|outline|list|reply|response|example)\b|^save\s+me\s+time\b|^run\s+me\s+through\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProseOrQuestion();

    [GeneratedRegex(@"^(?:open|launch|type|click|append|save|move|copy|rename|delete|send|publish|upload|download|run|install|find|inspect|select|press|navigate|go\s+to|take\s+(?:a\s+)?screenshot|capture\s+(?:the\s+)?screen)\b|^start\b(?!\s+(?:by|with)\s+(?:explain|describ|summari|draft|review))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishExternalVerb();

    [GeneratedRegex(@"^create\s+(?:(?:a|an|the|new)\s+)*(?:file|folder|directory|document|spreadsheet|presentation|calendar|event|task|issue|record|project)\b|^write\b.*\b(?:into|in|to)\b.*\b(?:file|document|notepad|editor|app|application|window)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishConcreteCreation();

    [GeneratedRegex(@"^use\s+(?!simple\b|clear\b|plain\b|concise\b|bullets?\b|examples?\b|a\s+metaphor\b|this\s+tone\b|english\b|persian\b|turkish\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishUse();

    [GeneratedRegex(@"\b(?:then|and\s+then)\s+(?:please\s+)?(?:open|launch|start|type|click|append|save|create|move|copy|rename|delete|send|publish|upload|download|run|install|use)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishSequencedAction();

    [GeneratedRegex(@"(?:^|\b(?:then|and\s+then|and)\s+|,\s*(?:and\s+)?)(?:please\s+)?(?:open|launch|start|type|write|click|append|save|create|move|copy|rename|delete|send|publish|upload|download|run|install|select|press|navigate|go\s+to)\b|^use\b.*\bto\s+(?:open|launch|start|type|write|click|append|save|create|move|copy|rename|delete|send|publish|upload|download|run|install|select|press|navigate)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishStateChange();

    [GeneratedRegex(@"(?:^|\b(?:and(?:\s+then)?|then|to)\s+|,\s*(?:and\s+)?)(?:please\s+)?(?<verb>open|launch|start|type|write|click|append|save|create|move|copy|rename|delete|remove|send|publish|post|upload|download|run|execute|install|select|press|navigate|go\s+to)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishCompletionVerb();

    [GeneratedRegex(@"\b(?:do\s+not|don't|never|must\s+not|should\s+not|without|avoid)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProhibition();

    [GeneratedRegex(@"\b(?:but|however|instead)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishProhibitionReset();

    [GeneratedRegex(@"\b(?:notepad|editor|text\s*(?:box|field)|editable\s+document|app|application|window)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishDesktopTextContext();

    [GeneratedRegex(@"^(?:(?:لطفا|لطفاً)\s+)*(?:(?:میشه|می\s*شه|ممکنه|می\s*توانی|میتونی|می‌توانی)\s+)?", RegexOptions.CultureInvariant)]
    private static partial Regex PersianWrapper();

    [GeneratedRegex(@"^(?:چطور|چگونه|چرا|چی|چه\s|کی\s|کجا|آیا\s+باید)|(?:توضیح بده|توضیح دهید|خلاصه کن|خلاصه کنید|پیش\s*نویس|بازنویسی کن|ترجمه کن)", RegexOptions.CultureInvariant)]
    private static partial Regex PersianProseOrQuestion();

    [GeneratedRegex(@"(?:باز|اجرا|تایپ|کلیک|اضافه|ذخیره|جابجا|جابه‌جا|منتقل|کپی|حذف|ارسال|منتشر|آپلود|دانلود|نصب|بررسی|پیدا|مشاهده)\s*(?:کن|کنید|کنی)|(?:بفرست|بفرستید|بفرستی|بخوان|بخوانید|بنویس|بنویسید|بنویسی)|تغییر\s+نام\s+بده|فایل\s+(?:جدید\s+)?(?:ایجاد\s+کن|بساز)|کلید\b.*(?:بزن|فشار\s+بده)|از\s+(?:برنامه|ابزار|مرورگر|سرویس|فایل|پنجره|تلگرام|نوت\s*پد)\b.*استفاده\s+کن", RegexOptions.CultureInvariant)]
    private static partial Regex PersianAction();

    [GeneratedRegex(@"(?:باز|اجرا|تایپ|کلیک|اضافه|ذخیره|جابجا|جابه‌جا|منتقل|کپی|حذف|ارسال|منتشر|آپلود|دانلود|نصب)\s*(?:کن|کنید|کنی)|(?:بفرست|بفرستید|بفرستی|بنویس|بنویسید|بنویسی)|تغییر\s+نام\s+بده|فایل\s+(?:جدید\s+)?(?:ایجاد\s+کن|بساز)|کلید\b.*(?:بزن|فشار\s+بده)", RegexOptions.CultureInvariant)]
    private static partial Regex PersianStateChange();

    [GeneratedRegex(@"^(?:lütfen\s+|rica\s+etsem\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishWrapper();

    [GeneratedRegex(@"\b(?:nasıl|neden|niçin|nedir|ne\s+zaman|hangi)\b|^(?:açıkla|özetle|karşılaştır|incele|taslak|yeniden\s+yaz|çevir)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishProseOrQuestion();

    [GeneratedRegex(@"\b(?:aç|açar\s+mısın|başlat|başlatır\s+mısın|tıkla|tıklar\s+mısın|ekle|kaydet|taşı|kopyala|sil|gönder|yayınla|yükle|indir|çalıştır|kur|seç|bul|oku)\b|(?:dosya|klasör|belge|sunum|tablo)\s+(?:oluştur|yarat)|(?:not\s*defteri|dosya|belge|uygulama|pencere)(?:'?[a-zçğıöşü]+)?\s+.*\b(?:yaz|incele|kontrol\s+et)\b|\b\w+\s+tuşuna\s+bas\b|(?:uygulamayı|aracı|tarayıcıyı|servisi)\s+kullan", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishAction();

    [GeneratedRegex(@"\b(?:aç|açar\s+mısın|başlat|başlatır\s+mısın|tıkla|tıklar\s+mısın|ekle|kaydet|taşı|kopyala|sil|gönder|yayınla|yükle|indir|çalıştır|kur|seç)\b|(?:dosya|klasör|belge|sunum|tablo)\s+(?:oluştur|yarat)|(?:not\s*defteri|dosya|belge|uygulama|pencere)(?:'?[a-zçğıöşü]+)?\s+.*\byaz\b|\b\w+\s+tuşuna\s+bas\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishStateChange();

    [GeneratedRegex(@"\b(?:lütfen|not\s+defteri|dosya|klasör|belge|kaydet|oluştur|başlat|gönder|kullan|yaz|nasıl|neden|özetle|taslak)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishMarker();

    [GeneratedRegex(@"^(?:ma|me|madan|meden|mayın|meyin)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishNegativeSuffix();

    public static bool RequiresExecution(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;
        var text = Space().Replace(userText.Trim(), " ");
        if (ContainsPersian(text))
        {
            var request = PersianWrapper().Replace(text, "");
            return !PersianProseOrQuestion().IsMatch(request) && PersianAction().IsMatch(request);
        }
        if (ContainsTurkish(text))
        {
            var request = TurkishWrapper().Replace(text, "");
            return !TurkishProseOrQuestion().IsMatch(request) && TurkishAction().IsMatch(request);
        }
        var english = EnglishWrapper().Replace(text, "");
        if (EnglishSequencedAction().IsMatch(english))
            return true;
        if (EnglishProseOrQuestion().IsMatch(english))
            return false;
        return EnglishExternalVerb().IsMatch(english) || EnglishConcreteCreation().IsMatch(english) || EnglishUse().IsMatch(english);
    }

    /// <summary>Returns true only when completing the request must change computer, file, or service state.</summary>
    public static bool RequiresStateChange(string? userText)
    {
        if (!RequiresExecution(userText))
            return false;
        var text = Space().Replace(userText!.Trim(), " ");
        if (ContainsPersian(text))
            return PersianStateChange().IsMatch(PersianWrapper().Replace(text, ""));
        if (ContainsTurkish(text))
            return TurkishStateChange().IsMatch(TurkishWrapper().Replace(text, ""));
        return EnglishStateChange().IsMatch(EnglishWrapper().Replace(text, ""));
    }

    /// <summary>Identifies the final concrete mutation whose verified tool result is required for honest completion.</summary>
    public static ActionCompletionRequirement? RequiredCompletion(string? userText)
    {
        if (!RequiresStateChange(userText))
            return null;
        var text = Space().Replace(userText!.Trim(), " ");
        ActionCompletionFamily family;
        if (ContainsPersian(text))
            family = PersianCompletion(text);
        else if (ContainsTurkish(text))
            family = TurkishCompletion(text);
        else
            family = EnglishCompletion(text);
        return Requirement(family);
    }

    private static ActionCompletionFamily EnglishCompletion(string text)
    {
        var request = EnglishWrapper().Replace(text, "");
        var match = EnglishCompletionVerb().Matches(request).Cast<Match>().LastOrDefault(candidate => !IsEnglishProhibited(request, candidate.Index));
        if (match is null)
            return ActionCompletionFamily.AnyStateChange;
        var verb = match.Groups["verb"].Value.ToLowerInvariant();
        return verb switch
        {
            "type" => ActionCompletionFamily.TypeText,
            "write" or "append" when EnglishDesktopTextContext().IsMatch(request) => ActionCompletionFamily.TypeText,
            "write" or "append" => ActionCompletionFamily.WriteContent,
            "click" or "select" => ActionCompletionFamily.Click,
            "press" => ActionCompletionFamily.KeyPress,
            "navigate" or "go to" => ActionCompletionFamily.Navigate,
            "open" or "launch" or "start" => ActionCompletionFamily.OpenApplication,
            "save" => ActionCompletionFamily.Save,
            "create" => ActionCompletionFamily.Create,
            "move" => ActionCompletionFamily.Move,
            "copy" => ActionCompletionFamily.Copy,
            "rename" => ActionCompletionFamily.Rename,
            "delete" or "remove" => ActionCompletionFamily.Delete,
            "send" => ActionCompletionFamily.Send,
            "publish" or "post" => ActionCompletionFamily.Publish,
            "upload" => ActionCompletionFamily.Upload,
            "download" => ActionCompletionFamily.Download,
            "run" or "execute" => ActionCompletionFamily.Run,
            "install" => ActionCompletionFamily.Install,
            _ => ActionCompletionFamily.AnyStateChange
        };
    }

    private static ActionCompletionFamily PersianCompletion(string text)
    {
        var desktopText = text.Contains("نوت پد", StringComparison.Ordinal) || text.Contains("نوت‌پد", StringComparison.Ordinal) ||
            text.Contains("پنجره", StringComparison.Ordinal) || text.Contains("ویرایشگر", StringComparison.Ordinal);
        return LastFamily(text, IsPersianProhibited,
            (ActionCompletionFamily.TypeText, ["تایپ"]),
            (desktopText ? ActionCompletionFamily.TypeText : ActionCompletionFamily.WriteContent, ["بنویس", "اضافه"]),
            (ActionCompletionFamily.Click, ["کلیک"]),
            (ActionCompletionFamily.KeyPress, ["کلید", "فشار بده"]),
            (ActionCompletionFamily.OpenApplication, ["باز"]),
            (ActionCompletionFamily.Save, ["ذخیره"]),
            (ActionCompletionFamily.Create, ["ایجاد", "بساز"]),
            (ActionCompletionFamily.Move, ["جابجا", "جابه‌جا", "منتقل"]),
            (ActionCompletionFamily.Copy, ["کپی"]),
            (ActionCompletionFamily.Rename, ["تغییر نام"]),
            (ActionCompletionFamily.Delete, ["حذف"]),
            (ActionCompletionFamily.Send, ["ارسال", "بفرست"]),
            (ActionCompletionFamily.Publish, ["منتشر"]),
            (ActionCompletionFamily.Upload, ["آپلود"]),
            (ActionCompletionFamily.Download, ["دانلود"]),
            (ActionCompletionFamily.Run, ["اجرا"]),
            (ActionCompletionFamily.Install, ["نصب"]));
    }

    private static ActionCompletionFamily TurkishCompletion(string text)
    {
        var desktopText = text.Contains("not defteri", StringComparison.OrdinalIgnoreCase) || text.Contains("pencere", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("uygulama", StringComparison.OrdinalIgnoreCase) || text.Contains("editör", StringComparison.OrdinalIgnoreCase);
        return LastFamily(text, IsTurkishProhibited,
            (desktopText ? ActionCompletionFamily.TypeText : ActionCompletionFamily.WriteContent, ["yaz", "ekle"]),
            (ActionCompletionFamily.Click, ["tıkla"]),
            (ActionCompletionFamily.KeyPress, ["tuşuna bas"]),
            (ActionCompletionFamily.OpenApplication, ["aç", "başlat"]),
            (ActionCompletionFamily.Save, ["kaydet"]),
            (ActionCompletionFamily.Create, ["oluştur", "yarat"]),
            (ActionCompletionFamily.Move, ["taşı"]),
            (ActionCompletionFamily.Copy, ["kopyala"]),
            (ActionCompletionFamily.Rename, ["yeniden adlandır"]),
            (ActionCompletionFamily.Delete, ["sil"]),
            (ActionCompletionFamily.Send, ["gönder"]),
            (ActionCompletionFamily.Publish, ["yayınla"]),
            (ActionCompletionFamily.Upload, ["yükle"]),
            (ActionCompletionFamily.Download, ["indir"]),
            (ActionCompletionFamily.Run, ["çalıştır"]),
            (ActionCompletionFamily.Install, ["kur"]));
    }

    private static ActionCompletionFamily LastFamily(string text, Func<string, int, int, bool> isProhibited,
        params (ActionCompletionFamily Family, string[] Terms)[] candidates)
    {
        var selected = ActionCompletionFamily.AnyStateChange;
        var selectedIndex = -1;
        foreach (var candidate in candidates)
            foreach (var term in candidate.Terms)
            {
                var index = text.LastIndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index > selectedIndex && !isProhibited(text, index, term.Length))
                {
                    selected = candidate.Family;
                    selectedIndex = index;
                }
            }
        return selected;
    }

    private static bool IsEnglishProhibited(string text, int actionIndex)
    {
        var prefix = text[..actionIndex];
        var prohibition = EnglishProhibition().Matches(prefix).Cast<Match>().LastOrDefault();
        if (prohibition is null)
            return false;
        var sentenceBoundary = prefix.LastIndexOfAny(['.', '?', '!', ';']);
        if (sentenceBoundary > prohibition.Index)
            return false;
        var reset = EnglishProhibitionReset().Matches(prefix).Cast<Match>().LastOrDefault();
        if (reset is not null && reset.Index > prohibition.Index)
            return false;
        return !prohibition.Value.Equals("without", StringComparison.OrdinalIgnoreCase) ||
            prefix.LastIndexOf(',') < prohibition.Index;
    }

    private static bool IsPersianProhibited(string text, int actionIndex, int termLength)
    {
        var clauseStart = LastBoundary(text, actionIndex, ['.', '؟', '!', '؛', ';', '،', ',']);
        var clauseEnd = NextBoundary(text, actionIndex + termLength, ['.', '؟', '!', '؛', ';', '،', ',']);
        var clause = text[clauseStart..clauseEnd];
        return clause.Contains("نکن", StringComparison.Ordinal) || clause.Contains("نزن", StringComparison.Ordinal) ||
            clause.Contains("نده", StringComparison.Ordinal) || clause.Contains("نبند", StringComparison.Ordinal) ||
            clause.Contains("نفرست", StringComparison.Ordinal) || clause.Contains("ننویس", StringComparison.Ordinal) ||
            clause.Contains("هرگز", StringComparison.Ordinal) || clause.Contains("بدون", StringComparison.Ordinal);
    }

    private static bool IsTurkishProhibited(string text, int actionIndex, int termLength)
    {
        var suffix = text[(actionIndex + termLength)..];
        if (TurkishNegativeSuffix().IsMatch(suffix))
            return true;
        var clauseStart = LastBoundary(text, actionIndex, ['.', '?', '!', ';', ',']);
        var prefix = text[clauseStart..actionIndex];
        return prefix.Contains("sakın", StringComparison.OrdinalIgnoreCase) || prefix.Contains("asla", StringComparison.OrdinalIgnoreCase);
    }

    private static int LastBoundary(string text, int before, char[] boundaries)
    {
        if (before <= 0)
            return 0;
        var boundary = text.LastIndexOfAny(boundaries, before - 1);
        return boundary < 0 ? 0 : boundary + 1;
    }

    private static int NextBoundary(string text, int after, char[] boundaries)
    {
        var boundary = text.IndexOfAny(boundaries, after);
        return boundary < 0 ? text.Length : boundary;
    }

    private static ActionCompletionRequirement Requirement(ActionCompletionFamily family) => family switch
    {
        ActionCompletionFamily.TypeText => new(family, "typing the requested text", "desktop_type"),
        ActionCompletionFamily.Click => new(family, "clicking the requested target", "desktop_click"),
        ActionCompletionFamily.KeyPress => new(family, "pressing the requested key", "desktop_key"),
        ActionCompletionFamily.OpenApplication => new(family, "opening the requested application", "desktop_launch or desktop_activate"),
        ActionCompletionFamily.Navigate => new(family, "navigating to the requested destination", "desktop_click or a registered navigation tool"),
        ActionCompletionFamily.WriteContent => new(family, "writing the requested content", "a registered write, append, or generate tool"),
        ActionCompletionFamily.Save => new(family, "saving the requested item", "a registered save tool"),
        ActionCompletionFamily.Create => new(family, "creating the requested item", "a registered create, generate, or write tool"),
        ActionCompletionFamily.Move => new(family, "moving the requested item", "a registered move tool"),
        ActionCompletionFamily.Copy => new(family, "copying the requested item", "a registered copy tool"),
        ActionCompletionFamily.Rename => new(family, "renaming the requested item", "a registered rename tool"),
        ActionCompletionFamily.Delete => new(family, "deleting the requested item", "a registered delete or remove tool"),
        ActionCompletionFamily.Send => new(family, "sending the requested content", "a registered send tool"),
        ActionCompletionFamily.Publish => new(family, "publishing the requested content", "a registered publish or post tool"),
        ActionCompletionFamily.Upload => new(family, "uploading the requested item", "a registered upload tool"),
        ActionCompletionFamily.Download => new(family, "downloading the requested item", "a registered download tool"),
        ActionCompletionFamily.Run => new(family, "running the requested operation", "a registered run or execute tool"),
        ActionCompletionFamily.Install => new(family, "installing the requested item", "a registered install tool"),
        _ => new(family, "performing the requested state change", "a matching registered state-changing tool")
    };

    private static bool ContainsPersian(string value) => value.Any(character => character is >= '\u0600' and <= '\u06ff');
    private static bool ContainsTurkish(string value) => value.Any(character => character is 'ç' or 'Ç' or 'ğ' or 'Ğ' or 'ı' or 'İ' or 'ö' or 'Ö' or 'ş' or 'Ş' or 'ü' or 'Ü') || TurkishMarker().IsMatch(value);
}

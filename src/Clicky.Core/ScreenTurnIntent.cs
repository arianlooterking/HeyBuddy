namespace Clicky.Core;

public enum ScreenTurnKind
{
    None,
    Inspect,
    Locate,
    Walkthrough
}

/// <summary>Classifies only the owner's request. Screen, document, and tool content must never enter this classifier.</summary>
public static class ScreenTurnIntent
{
    private static readonly string[] WalkthroughTerms =
    [
        "walk me through", "guide me", "teach me", "show me how", "step by step", "how do i use", "how can i use",
        "یادم بده", "آموزش بده", "راهنمایی کن", "قدم به قدم", "مرحله به مرحله", "چطور از", "چگونه از",
        "bana öğret", "rehberlik et", "adım adım", "nasıl kullan", "nasıl yap"
    ];

    private static readonly string[] LocateTerms =
    [
        "where is", "where's", "show me where", "point to", "which button", "what button", "where do i click", "what do i click",
        "کجاست", "کجا هست", "کجا کلیک", "کدام دکمه", "کدوم دکمه", "نشانم بده", "نشونم بده",
        "nerede", "göster", "hangi düğme", "hangi buton", "nereye tıkla", "nereyi tıkla"
    ];

    private static readonly string[] InspectTerms =
    [
        "on my screen", "on this screen", "this screen", "what do you see", "look at this", "look at my", "this window", "this app", "this page",
        "روی صفحه", "تو صفحه", "این صفحه", "صفحه من", "اینجا چی", "چی می‌بینی", "این پنجره", "این برنامه",
        "ekranımda", "bu ekranda", "ekranda ne", "ne görüyorsun", "bu pencere", "bu uygulama", "bu sayfa"
    ];

    public static ScreenTurnKind Classify(string? ownerRequest)
    {
        if (string.IsNullOrWhiteSpace(ownerRequest))
            return ScreenTurnKind.None;
        var normalized = Normalize(ownerRequest);
        if (ContainsAny(normalized, WalkthroughTerms))
            return ScreenTurnKind.Walkthrough;
        if (ContainsAny(normalized, LocateTerms))
            return ScreenTurnKind.Locate;
        return ContainsAny(normalized, InspectTerms) ? ScreenTurnKind.Inspect : ScreenTurnKind.None;
    }

    public static bool ShouldCapture(string? ownerRequest) => Classify(ownerRequest) != ScreenTurnKind.None;

    private static bool ContainsAny(string value, IEnumerable<string> terms) => terms.Any(value.Contains);
    private static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace('\u200c', ' ');
}

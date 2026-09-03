using System.Text.RegularExpressions;

namespace Clicky.Core;

/// <summary>Only recognizes a complete, explicit, single-app opening request from the user's own message.</summary>
public static partial class AppOpenRequest
{
    [GeneratedRegex(@"^(?:(?:hey\s*buddy)[,\s]+)?(?:(?:can|could|would)\s+you\s+)?(?:please\s+)?(?:open|launch|start)\s+(?:(?:my|the)\s+)?(?<app>[\p{L}\p{N} +_.&()\-]{1,70}?)(?:\s+(?:app|application))?(?:\s+for\s+me)?(?:\s+please)?[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex English();
    [GeneratedRegex(@"^(?:لطفا\s+|لطفاً\s+)?(?:برنامه\s+)?(?<app>[\p{L}\p{N} +_.&()\-]{1,70}?)\s+(?:را\s+)?(?:باز کن|بازکن|اجرا کن|اجراکن)[.!؟?]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Persian();
    [GeneratedRegex(@"^(?:lütfen\s+)?(?<app>[\p{L}\p{N} +_.&()\-]{1,70}?)(?:['’](?:ı|i|u|ü|yi|yı|yu|yü))?\s+(?:aç|açar mısın|başlat)[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Turkish();
    [GeneratedRegex(@"\b(?:and|then|with|using|in|to|while|ve|sonra)\b|\sو\s|\sبعد\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Compound();

    public static string? Parse(string userText)
    {
        if (userText.Length > 160 || userText.IndexOfAny(['\r', '\n', '<', '>', ':', '/', '\\', ';', '"', '`']) >= 0)
            return null;
        var text = userText.Trim();
        foreach (var pattern in new[] { English(), Persian(), Turkish() })
        {
            var match = pattern.Match(text);
            if (!match.Success)
                continue;
            var app = match.Groups["app"].Value.Trim();
            if (app.Length < 2 || Compound().IsMatch(app))
                return null;
            return app.ToLowerInvariant() switch
            {
                "تلگرام" => "Telegram",
                "دفترچه یادداشت" or "نوت پد" or "نوتپد" or "not defteri" => "Notepad",
                "ماشین حساب" or "hesap makinesi" => "Calculator",
                "ورد" => "Word",
                "اکسل" => "Excel",
                "کروم" => "Chrome",
                "وی اس کد" or "vscode" or "vs code" => "Visual Studio Code",
                _ => app
            };
        }
        return null;
    }
}

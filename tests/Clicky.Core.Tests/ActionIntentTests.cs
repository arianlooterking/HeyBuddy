using Clicky.Core;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class ActionIntentTests
{
    [Theory]
    [InlineData("Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not touch any other document.")]
    [InlineData("Can you open my Telegram and go to my saved messages?")]
    [InlineData("Please type Hello into the Notepad window.")]
    [InlineData("Would you please save this file?")]
    [InlineData("I want you to send the approved message.")]
    [InlineData("Create a new file called notes.txt.")]
    [InlineData("Use the desktop tools to inspect the selected window.")]
    [InlineData("Find instructions for how to delete a file safely.")]
    [InlineData("لطفاً تلگرام را باز کن و پیام‌های ذخیره‌شده را پیدا کن")]
    [InlineData("این متن را در نوت پد تایپ کن")]
    [InlineData("یک فایل جدید ایجاد کن")]
    [InlineData("ایمیل تاییدشده را ارسال کن")]
    [InlineData("Lütfen Telegram'ı aç ve kayıtlı mesajlarıma git")]
    [InlineData("Bu dosyayı kaydet")]
    [InlineData("Not Defteri'ne Merhaba yaz")]
    [InlineData("Yeni bir dosya oluştur")]
    public void ClearExternalActionsRequireExecution(string prompt)
        => Assert.True(ActionIntent.RequiresExecution(prompt));

    [Theory]
    [InlineData("What is two plus two?")]
    [InlineData("How do I open Notepad?")]
    [InlineData("Could you explain how to install this app?")]
    [InlineData("Summarize the attached document.")]
    [InlineData("Draft a message I can send later.")]
    [InlineData("Send me a draft reply I can edit.")]
    [InlineData("Can you create a summary of this text?")]
    [InlineData("Use simple language to explain tool calls.")]
    [InlineData("Should I delete this file?")]
    [InlineData("Do not open Telegram.")]
    [InlineData("The document says: open Telegram.")]
    [InlineData("چگونه نوت پد را باز کنم؟")]
    [InlineData("لطفاً این متن را خلاصه کن")]
    [InlineData("یک پیش نویس پیام برای من بنویس")]
    [InlineData("Not Defteri'ni nasıl açarım?")]
    [InlineData("Bu metni özetle")]
    [InlineData("Gönderebileceğim bir mesaj taslağı yaz")]
    public void QuestionsExplanationsSummariesAndDraftsStayProse(string prompt)
        => Assert.False(ActionIntent.RequiresExecution(prompt));

    [Theory]
    [InlineData("Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not touch any other document.")]
    [InlineData("Please open Telegram.")]
    [InlineData("Use Notepad to type this text.")]
    [InlineData("فایل جدید ایجاد کن")]
    [InlineData("این متن را در نوت پد تایپ کن")]
    [InlineData("Lütfen Telegram'ı aç")]
    [InlineData("Bu dosyayı kaydet")]
    public void ConcreteMutationsRequireAStateChange(string prompt)
        => Assert.True(ActionIntent.RequiresStateChange(prompt));

    [Theory]
    [InlineData("Find the Notepad window and inspect its editable document.")]
    [InlineData("Use the desktop tools to inspect the selected window.")]
    [InlineData("What is two plus two?")]
    [InlineData("پنجره نوت پد را بررسی کن")]
    [InlineData("فایل را بخوان")]
    [InlineData("Not Defteri penceresini kontrol et")]
    [InlineData("Dosyayı oku")]
    public void ReadOnlyActionsAndProseDoNotRequireAStateChange(string prompt)
        => Assert.False(ActionIntent.RequiresStateChange(prompt));

    [Theory]
    [InlineData("Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not touch any other document.", ActionCompletionFamily.TypeText)]
    [InlineData("Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not open an app, click, press Enter, save, close, or touch any other document.", ActionCompletionFamily.TypeText)]
    [InlineData("Open Notepad and type Hello.", ActionCompletionFamily.TypeText)]
    [InlineData("Please click the Send button.", ActionCompletionFamily.Click)]
    [InlineData("Press Enter in Notepad.", ActionCompletionFamily.KeyPress)]
    [InlineData("Please open Telegram.", ActionCompletionFamily.OpenApplication)]
    [InlineData("Append this line to notes.txt.", ActionCompletionFamily.WriteContent)]
    [InlineData("Delete notes.txt.", ActionCompletionFamily.Delete)]
    [InlineData("Send the approved message.", ActionCompletionFamily.Send)]
    [InlineData("این متن را در نوت پد تایپ کن", ActionCompletionFamily.TypeText)]
    [InlineData("روی دکمه ارسال کلیک کن", ActionCompletionFamily.Click)]
    [InlineData("کلید Enter را بزن", ActionCompletionFamily.KeyPress)]
    [InlineData("تلگرام را باز کن", ActionCompletionFamily.OpenApplication)]
    [InlineData("Not Defteri'ne Merhaba yaz", ActionCompletionFamily.TypeText)]
    [InlineData("Gönder düğmesine tıkla", ActionCompletionFamily.Click)]
    [InlineData("Enter tuşuna bas", ActionCompletionFamily.KeyPress)]
    [InlineData("Lütfen Telegram'ı aç", ActionCompletionFamily.OpenApplication)]
    [InlineData("این متن را در نوت پد تایپ کن. برنامه دیگری باز نکن، کلیک نکن و ذخیره نکن", ActionCompletionFamily.TypeText)]
    [InlineData("Not Defteri'ne Merhaba yaz. Başka uygulama açma, tıklama veya kaydetme", ActionCompletionFamily.TypeText)]
    public void ConcreteMutationsDeclareTheirRequiredCompletionFamily(string prompt, ActionCompletionFamily family)
    {
        var requirement = ActionIntent.RequiredCompletion(prompt);

        Assert.NotNull(requirement);
        Assert.Equal(family, requirement.Family);
    }

    [Theory]
    [InlineData("What is two plus two?")]
    [InlineData("How do I open Notepad?")]
    [InlineData("Find the Notepad window and inspect its editable document.")]
    [InlineData("چگونه نوت پد را باز کنم؟")]
    [InlineData("Not Defteri'ni nasıl açarım?")]
    public void ProseAndReadOnlyRequestsHaveNoCompletionRequirement(string prompt)
        => Assert.Null(ActionIntent.RequiredCompletion(prompt));

    [Fact]
    public void NativeCompletionFamiliesRequireExactEffectiveWriteTools()
    {
        var typing = ActionIntent.RequiredCompletion("Type hello into Notepad.")!;
        var opening = ActionIntent.RequiredCompletion("Open Notepad.")!;

        Assert.True(typing.IsSatisfiedBy("desktop_type", RiskLevel.Sensitive));
        Assert.False(typing.IsSatisfiedBy("desktop_activate", RiskLevel.LocalWrite));
        Assert.False(typing.IsSatisfiedBy("desktop_type", RiskLevel.ReadOnly));
        Assert.True(opening.IsSatisfiedBy("desktop_launch", RiskLevel.LocalWrite));
        Assert.True(opening.IsSatisfiedBy("desktop_activate", RiskLevel.LocalWrite));
        Assert.False(opening.IsSatisfiedBy("desktop_snapshot", RiskLevel.ReadOnly));
    }
}

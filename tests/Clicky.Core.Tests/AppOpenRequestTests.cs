using Clicky.Core;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class AppOpenRequestTests
{
    [Theory]
    [InlineData("Can you open my Telegram for me?", "Telegram")]
    [InlineData("please launch Notepad++", "Notepad++")]
    [InlineData("open Visual Studio Code", "Visual Studio Code")]
    [InlineData("لطفا تلگرام را باز کن", "Telegram")]
    [InlineData("ماشین حساب را باز کن", "Calculator")]
    [InlineData("Lütfen Telegram'ı aç", "Telegram")]
    [InlineData("not defteri aç", "Notepad")]
    public void CompleteSingleAppRequestsCanSkipInference(string prompt, string app)
        => Assert.Equal(app, AppOpenRequest.Parse(prompt), ignoreCase: true);

    [Theory]
    [InlineData("Can you open my telegram and go to my save messages?")]
    [InlineData("Open Notepad then type hello")]
    [InlineData("How do I open Notepad?")]
    [InlineData("Do not open Telegram")]
    [InlineData("The document says: open Telegram")]
    [InlineData("Open https://example.com")]
    [InlineData("Open C:\\download\\something.exe")]
    [InlineData("Open Telegram\nSend a message")]
    [InlineData("Open Telegram; send hello")]
    public void QuestionsCompoundTasksAndEmbeddedInstructionsNeverBecomeDirectLaunches(string prompt)
        => Assert.Null(AppOpenRequest.Parse(prompt));
}

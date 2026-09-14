using VrcImageCurator.App.Services;

namespace VrcImageCurator.Tests.App;

public sealed class KeepIncomingPromptTests
{
    [Fact]
    public void TheFirstMatchOfAReviewIsAlwaysAsked()
    {
        var prompt = new KeepIncomingPrompt();

        Assert.True(prompt.MustAsk(Guid.NewGuid(), canRecycle: true));
    }

    [Fact]
    public void TheRestOfTheSameReviewIsNotAskedAgain()
    {
        // The point of the whole class: settling a review with several matches takes one press
        // each, and asking the same question behind every press is what made it tedious.
        var review = Guid.NewGuid();
        var prompt = new KeepIncomingPrompt();
        prompt.Agreed(review, canRecycle: true);

        Assert.False(prompt.MustAsk(review, canRecycle: true));
        Assert.False(prompt.MustAsk(review, canRecycle: true));
    }

    [Fact]
    public void ADifferentReviewIsAskedOnItsOwnAccount()
    {
        var prompt = new KeepIncomingPrompt();
        prompt.Agreed(Guid.NewGuid(), canRecycle: true);

        Assert.True(prompt.MustAsk(Guid.NewGuid(), canRecycle: true));
    }

    [Fact]
    public void AMatchThatCannotBeRecycledIsAskedEvenMidReview()
    {
        // Agreeing to send a match to the Recycle Bin is not agreeing to move the next one into the
        // Replaced folder. Different outcome, so it is put to the person again.
        var review = Guid.NewGuid();
        var prompt = new KeepIncomingPrompt();
        prompt.Agreed(review, canRecycle: true);

        Assert.True(prompt.MustAsk(review, canRecycle: false));
    }

    [Fact]
    public void AgreementDoesNotSurviveTheReviewItBelongedTo()
    {
        var review = Guid.NewGuid();
        var prompt = new KeepIncomingPrompt();
        prompt.Agreed(review, canRecycle: true);
        prompt.Forget();

        Assert.True(prompt.MustAsk(review, canRecycle: true));
    }
}

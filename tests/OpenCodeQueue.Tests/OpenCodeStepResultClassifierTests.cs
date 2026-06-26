using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;

namespace OpenCodeQueue.Tests;

public sealed class OpenCodeStepResultClassifierTests
{
    [Fact]
    public void Classify_WhenCliStderrHasToolExecutionAbortedAndNonZeroExitCode_ReturnsRecoverable()
    {
        var classifier = new OpenCodeStepResultClassifier();
        var result = new OpenCodeMessageResult(false, ExitCode: 1, Stderr: "Tool execution aborted");

        var classification = classifier.Classify(result, new ResilienceSettings());

        Assert.Equal(OpenCodeStepOutcomeKind.RecoverableToolAbort, classification.Kind);
    }

    [Fact]
    public void Classify_WhenCliHasFatalConfigurationError_ReturnsFatalFailure()
    {
        var classifier = new OpenCodeStepResultClassifier();
        var result = new OpenCodeMessageResult(false, ExitCode: 2, Stderr: "unknown argument --bad");

        var classification = classifier.Classify(result, new ResilienceSettings());

        Assert.Equal(OpenCodeStepOutcomeKind.FatalFailure, classification.Kind);
    }

    [Fact]
    public void Classify_WhenSuccessfulAssistantTextRequestsManualIntervention_ReturnsNeedsManualIntervention()
    {
        var classifier = new OpenCodeStepResultClassifier();
        var result = new OpenCodeMessageResult(true, LastAssistantText: "NEEDS_MANUAL_INTERVENTION: нужен секрет");

        var classification = classifier.Classify(result, new ResilienceSettings());

        Assert.Equal(OpenCodeStepOutcomeKind.NeedsManualIntervention, classification.Kind);
        Assert.Contains("нужен секрет", classification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_WhenPermissionRequestReturned_ReturnsPermissionRequest()
    {
        var classifier = new OpenCodeStepResultClassifier();
        var result = new OpenCodeMessageResult(false, LastAssistantText: "permission request: approve bash command");

        var classification = classifier.Classify(result, new ResilienceSettings());

        Assert.Equal(OpenCodeStepOutcomeKind.PermissionRequest, classification.Kind);
    }

    [Fact]
    public void Classify_WhenQuestionReturned_ReturnsQuestionRequest()
    {
        var classifier = new OpenCodeStepResultClassifier();
        var result = new OpenCodeMessageResult(false, LastAssistantText: "please clarify target branch");

        var classification = classifier.Classify(result, new ResilienceSettings());

        Assert.Equal(OpenCodeStepOutcomeKind.QuestionRequest, classification.Kind);
    }
}

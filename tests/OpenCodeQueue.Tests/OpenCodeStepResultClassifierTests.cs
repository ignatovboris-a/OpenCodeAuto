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

        Assert.Equal(OpenCodeStepOutcomeKind.RecoverableInterruption, classification.Kind);
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
}

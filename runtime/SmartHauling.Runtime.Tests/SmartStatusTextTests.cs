namespace SmartHauling.Runtime.Tests;

public sealed class SmartStatusTextTests
{
    [Fact]
    public void ResolveDisplayText_ReturnsOriginalText_WhenItIsNotALocalizationKey()
    {
        // Arrange
        const string text = "Hauling to stockpile";

        // Act
        var result = SmartStatusText.ResolveDisplayText(text);

        // Assert
        Assert.Equal(text, result);
    }

    [Fact]
    public void LooksLikePlaceholderKey_ReturnsTrue_ForWorkerActionKey()
    {
        // Act
        var result = SmartStatusText.LooksLikePlaceholderKey("worker_action_stockpilehaulinggoal");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void LooksLikePlaceholderKey_ReturnsFalse_ForHumanReadableText()
    {
        // Act
        var result = SmartStatusText.LooksLikePlaceholderKey("Hauling to stockpile");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void AppendSuffix_AppendsSuffixOnce_WhenItIsMissing()
    {
        // Arrange
        const string text = "Hauling to stockpile";

        // Act
        var result = SmartStatusText.AppendSuffix(text, "(smart)");

        // Assert
        Assert.Equal("Hauling to stockpile (smart)", result);
    }

    [Fact]
    public void AppendSuffix_DoesNotDuplicateSuffix_WhenItIsAlreadyPresent()
    {
        // Arrange
        const string text = "Hauling to stockpile (smart)";

        // Act
        var result = SmartStatusText.AppendSuffix(text, "(smart)");

        // Assert
        Assert.Equal("Hauling to stockpile (smart)", result);
    }

    [Fact]
    public void AppendSuffix_UsesDefaultSuffix_WhenProvidedSuffixIsBlank()
    {
        // Arrange
        const string text = "Hauling to stockpile";

        // Act
        var result = SmartStatusText.AppendSuffix(text, "");

        // Assert
        Assert.Equal("Hauling to stockpile (smart)", result);
    }

    [Fact]
    public void IsUsableTranslation_ReturnsFalse_ForLocalizationTermPlaceholder()
    {
        // Act
        var result = SmartStatusText.IsUsableTranslation("Mods/SmartHauling/StatusSuffix");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsUsableTranslation_ReturnsFalse_ForBracketedPlaceholder()
    {
        // Act
        var result = SmartStatusText.IsUsableTranslation("[Mods/SmartHauling/StatusSuffix]");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsUsableTranslation_ReturnsTrue_ForActualSuffix()
    {
        // Act
        var result = SmartStatusText.IsUsableTranslation("(smart)");

        // Assert
        Assert.True(result);
    }
}

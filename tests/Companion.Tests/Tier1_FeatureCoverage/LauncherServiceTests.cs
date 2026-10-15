using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class LauncherServiceTests
{
    private readonly LauncherService _sut = new();

    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(18 - 6) / 3", "4")]
    public void TryEvaluateInstantAnswer_EvaluatesArithmetic(string query, string expected)
    {
        _sut.TryEvaluateInstantAnswer(query, out AppShortcut? result).Should().BeTrue();
        result!.Path.Should().Be($"math:{expected}");
    }

    [Fact]
    public void TryEvaluateInstantAnswer_ConvertsHexToRgb()
    {
        _sut.TryEvaluateInstantAnswer("#3a7", out AppShortcut? result).Should().BeTrue();
        result!.Path.Should().Be("math:RGB(51, 170, 119)");
    }

    [Fact]
    public void TryEvaluateInstantAnswer_ConvertsRgbToHex()
    {
        _sut.TryEvaluateInstantAnswer("rgb(16, 32, 255)", out AppShortcut? result).Should().BeTrue();
        result!.Path.Should().Be("math:#1020FF");
    }

    [Fact]
    public void TryEvaluateInstantAnswer_ConvertsUnits()
    {
        _sut.TryEvaluateInstantAnswer("10 km to mi", out AppShortcut? result).Should().BeTrue();
        result!.Path.Should().Be("math:6.214 mi");
    }

    [Fact]
    public void TryEvaluateInstantAnswer_MapsGreaterThanToQuickCommand()
    {
        _sut.TryEvaluateInstantAnswer("> whoami", out AppShortcut? result).Should().BeTrue();
        result!.Path.Should().Be("cmd:whoami");
    }

    [Fact]
    public void Search_PutsInstantAnswerBeforeMatchingApps()
    {
        AppShortcut[] shortcuts =
        [
            new AppShortcut("Calculator", "calculator.lnk"),
            new AppShortcut("Camera", "camera.lnk")
        ];

        IReadOnlyList<AppShortcut> results = _sut.Search(shortcuts, "2 + 2");

        results[0].Path.Should().Be("math:4");
    }

    [Fact]
    public void TryEvaluateInstantAnswer_RejectsExpressionsOutsideArithmeticGrammar()
    {
        _sut.TryEvaluateInstantAnswer("1 + System.Diagnostics.Process.Start('x')", out _).Should().BeFalse();
    }
}

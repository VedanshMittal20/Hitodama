using System;
using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier3_CrossFeatureCombinations;

public sealed class SpringSolverWindowBoundsCombinationsTests
{
    [Fact]
    public void SpringSolver_DrivesIslandDockExpansion_MaintainsScreenBoundsAndCentering()
    {
        // Screen resolution: 1920x1080
        const double screenWidth = 1920.0;
        const double screenHeight = 1080.0;

        // Collapsed dimensions: 164x36
        // Expanded dimensions: 640x390
        var widthSpring = new SpringValue(initial: 164.0) { Target = 640.0 };
        var heightSpring = new SpringValue(initial: 36.0) { Target = 390.0 };

        const double dt = 1.0 / 120.0; // 120Hz sub-stepping
        int steps = 0;

        while ((widthSpring.Step(dt) | heightSpring.Step(dt)) && steps < 500)
        {
            steps++;

            // Island is anchored at Top = 0 and centered horizontally
            double currentWidth = widthSpring.Current;
            double currentHeight = heightSpring.Current;
            double left = (screenWidth - currentWidth) / 2.0;
            double right = left + currentWidth;

            // Invariant assertions during motion
            currentWidth.Should().BeInRange(100.0, 700.0, "width must stay near bounds");
            currentHeight.Should().BeInRange(20.0, 450.0, "height must stay near bounds");
            currentHeight.Should().BeLessThan(screenHeight, "dock height must not exceed screen height");
            left.Should().BeGreaterThan(0, "island must remain inside left edge of screen");
            right.Should().BeLessThan(screenWidth, "island must remain inside right edge of screen");
        }

        // Settled state verification
        widthSpring.Current.Should().Be(640.0);
        heightSpring.Current.Should().Be(390.0);
        widthSpring.Velocity.Should().Be(0.0);
        heightSpring.Velocity.Should().Be(0.0);
        steps.Should().BeLessThan(300);
    }

    [Fact]
    public void SpringSolver_DrivesIslandDockCollapse_ReturnsSmoothlyToInitialPill()
    {
        // Morph from expanded (640x390) back to collapsed (164x36)
        var widthSpring = new SpringValue(initial: 640.0) { Target = 164.0 };
        var heightSpring = new SpringValue(initial: 390.0) { Target = 36.0 };

        const double dt = 1.0 / 120.0;
        int steps = 0;

        while ((widthSpring.Step(dt) | heightSpring.Step(dt)) && steps < 500)
        {
            steps++;
        }

        widthSpring.Current.Should().Be(164.0);
        heightSpring.Current.Should().Be(36.0);
        steps.Should().BeLessThan(300);
    }
}

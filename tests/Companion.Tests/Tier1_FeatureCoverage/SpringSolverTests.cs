using System;
using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class SpringSolverTests
{
    [Fact]
    public void SpringValue_Constructor_InitializesCurrentAndTargetEqually()
    {
        // Act
        var spring = new SpringValue(initial: 164.0, omega: 26.0, zeta: 0.76);

        // Assert
        spring.Current.Should().Be(164.0);
        spring.Target.Should().Be(164.0);
        spring.Velocity.Should().Be(0.0);
        spring.Omega.Should().Be(26.0);
        spring.Zeta.Should().Be(0.76);
    }

    [Fact]
    public void SpringValue_Step_DisplacesTowardsTarget()
    {
        // Arrange: Pill expanding from collapsed width (164) to expanded width (640)
        var spring = new SpringValue(initial: 164.0)
        {
            Target = 640.0
        };

        // Act: Step forward by 1 frame (1/60s = 0.016s)
        bool active = spring.Step(0.016);

        // Assert
        active.Should().BeTrue();
        spring.Velocity.Should().BeGreaterThan(0, "positive target delta causes positive acceleration");
        spring.Current.Should().BeGreaterThan(164.0, "spring moved towards target");
    }

    [Fact]
    public void SpringValue_Step_SettlesAndDetaches_WhenDisplacementAndVelocityBelowEpsilon()
    {
        // Arrange
        var spring = new SpringValue(initial: 164.0)
        {
            Target = 640.0
        };

        // Act: Run physics simulation until settled
        int totalFrames = 0;
        const double dt = 1.0 / 120.0; // 120Hz sub-stepping
        while (spring.Step(dt) && totalFrames < 1000)
        {
            totalFrames++;
        }

        // Assert
        totalFrames.Should().BeLessThan(300, "spring physics should settle in under 2.5 seconds");
        spring.Current.Should().Be(640.0, "snaps exactly to target upon settling");
        spring.Velocity.Should().Be(0.0, "velocity reset to 0 upon settling");

        // Subsequent step must report inactive immediately
        bool stillActive = spring.Step(dt);
        stillActive.Should().BeFalse();
    }

    [Fact]
    public void SpringValue_Substepping_HandlesLargeDtWithoutInstability()
    {
        // Arrange: simulating frame drop (dt = 0.050s = 50ms)
        var spring = new SpringValue(initial: 0.0)
        {
            Target = 100.0
        };

        // Act
        bool active = spring.Step(0.050);

        // Assert
        active.Should().BeTrue();
        double.IsNaN(spring.Current).Should().BeFalse();
        double.IsInfinity(spring.Current).Should().BeFalse();
        spring.Current.Should().BeInRange(0.0, 150.0);
    }

    [Fact]
    public void SpringValue_AlreadyAtTarget_ReturnsFalseImmediately()
    {
        // Arrange
        var spring = new SpringValue(500.0);

        // Act
        bool active = spring.Step(0.016);

        // Assert
        active.Should().BeFalse();
        spring.Current.Should().Be(500.0);
        spring.Velocity.Should().Be(0.0);
    }

    [Fact]
    public void IslandAnimator_CreateSpring_ReturnsConfiguredSpring()
    {
        // Arrange
        var animator = new IslandAnimator();

        // Act
        var widthSpring = animator.CreateSpring(164.0, omega: 28.0, zeta: 0.8);
        var heightSpring = animator.CreateSpring(36.0, omega: 28.0, zeta: 0.8);

        // Assert
        widthSpring.Should().NotBeNull();
        widthSpring.Current.Should().Be(164.0);
        heightSpring.Should().NotBeNull();
        heightSpring.Current.Should().Be(36.0);
    }
}

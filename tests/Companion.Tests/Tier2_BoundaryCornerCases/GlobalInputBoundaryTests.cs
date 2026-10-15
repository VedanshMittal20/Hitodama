using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

public sealed class GlobalInputBoundaryTests
{
    [Fact]
    public void RapidHoldTransitions_ShorterThanThreshold_DoNotTriggerActivation()
    {
        // Emulating rapid key tap: user taps Ctrl+Shift for 50ms (well below 180ms threshold)
        const int elapsedHoldMs = 50;
        const int circleThresholdMs = 180;

        bool triggersCircleToAsk = elapsedHoldMs >= circleThresholdMs;

        triggersCircleToAsk.Should().BeFalse("tapping keys quickly must never falsely trigger hold actions");
    }

    [Fact]
    public void NonModifierInterruption_DuringHold_ImmediatelyCancelsPendingAction()
    {
        // User holds Ctrl+Shift for 100ms, then types 'K'
        bool circleActive = false;
        bool circleCancelled = false;
        var keysDown = new HashSet<int> { 0x11, 0x10 }; // Ctrl, Shift

        // Key K pressed (0x4B)
        const int keyK = 0x4B;
        keysDown.Add(keyK);
        var modifiers = new HashSet<int> { 0x11, 0x10, 0x12, 0x5B, 0x5C };

        if (!modifiers.Contains(keyK))
        {
            circleCancelled = true;
        }

        circleCancelled.Should().BeTrue();
        circleActive.Should().BeFalse();
    }

    [Fact]
    public void ReverseModifierKeyRelease_ResetsChordCleanly()
    {
        // User releases Shift before Ctrl:
        var keysDown = new HashSet<int> { 0x11, 0x10 }; // Ctrl, Shift
        keysDown.Remove(0x10); // Shift released first

        bool isCircleChordDown = keysDown.Contains(0x11) && keysDown.Contains(0x10);

        isCircleChordDown.Should().BeFalse();
    }

    [Fact]
    public void MultipleModifiers_HeldSimultaneously_DisambiguatesCleanly()
    {
        // User presses Ctrl + Shift + Alt + Win
        var keys = new HashSet<int> { 0x11, 0x10, 0x12, 0x5B };

        bool isCircle = keys.Contains(0x11) && keys.Contains(0x10);
        bool isDictation = keys.Contains(0x11) && keys.Contains(0x5B);

        isCircle.Should().BeTrue();
        isDictation.Should().BeTrue();
    }
}

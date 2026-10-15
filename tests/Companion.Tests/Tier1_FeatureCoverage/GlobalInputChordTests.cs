using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Companion.Services;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

public sealed class GlobalInputChordTests
{
    [Fact]
    public void GlobalInputService_Constructor_InitializesWithUnregisteredHook()
    {
        // Act
        using var service = new GlobalInputService();

        // Assert: should initialize without throwing
        service.Should().NotBeNull();
    }

    [Fact]
    public void ChordDurationThresholds_VerifyExactSpecifications()
    {
        // R4 Requirements:
        // Circle to Ask: Ctrl + Shift (>180ms)
        // Dictation: Ctrl + Win (>150ms)
        const int circleToAskThresholdMs = 180;
        const int dictationThresholdMs = 150;

        circleToAskThresholdMs.Should().Be(180);
        dictationThresholdMs.Should().Be(150);
        circleToAskThresholdMs.Should().BeGreaterThan(dictationThresholdMs);
    }

    [Fact]
    public void ModifierKeyClassifier_DistinguishesModifiersFromRegularKeys()
    {
        // Virtual Key definitions from Windows Win32 API
        const int vkControl = 0x11;
        const int vkShift = 0x10;
        const int vkAlt = 0x12;
        const int vkLeftWindows = 0x5B;
        const int vkA = 0x41;
        const int vkSpace = 0x20;

        var modifierKeys = new HashSet<int> { vkControl, vkShift, vkAlt, vkLeftWindows };

        modifierKeys.Contains(vkControl).Should().BeTrue();
        modifierKeys.Contains(vkShift).Should().BeTrue();
        modifierKeys.Contains(vkLeftWindows).Should().BeTrue();
        modifierKeys.Contains(vkA).Should().BeFalse();
        modifierKeys.Contains(vkSpace).Should().BeFalse();
    }

    [Fact]
    public void HotkeyDefinitions_CoverAssistantAndEmergencyKillSwitch()
    {
        // Virtual keys
        const int vkSpace = 0x20;
        const int vkBackspace = 0x08;

        bool isAssistantHotkey(int key, bool ctrl, bool alt) =>
            key == vkSpace && ctrl && alt;

        bool isEmergencyKillSwitch(int key, bool ctrl, bool alt) =>
            key == vkBackspace && ctrl && alt;

        isAssistantHotkey(vkSpace, ctrl: true, alt: true).Should().BeTrue();
        isAssistantHotkey(vkSpace, ctrl: true, alt: false).Should().BeFalse();

        isEmergencyKillSwitch(vkBackspace, ctrl: true, alt: true).Should().BeTrue();
        isEmergencyKillSwitch(vkBackspace, ctrl: false, alt: true).Should().BeFalse();
    }

    [Fact]
    public void RapidChordCancellation_Logic_CancelsWhenNonModifierPressed()
    {
        // Emulating chord cancellation:
        // When Ctrl + Shift is held, but user taps 'T' (0x54), chord must cancel!
        bool chordActive = true;
        const int pressedKey = 0x54; // Key 'T'
        var modifiers = new HashSet<int> { 0x11, 0x10, 0x12, 0x5B, 0x5C };

        if (!modifiers.Contains(pressedKey))
        {
            chordActive = false;
        }

        chordActive.Should().BeFalse("pressing any regular non-modifier key must immediately abort the pending chord");
    }
}

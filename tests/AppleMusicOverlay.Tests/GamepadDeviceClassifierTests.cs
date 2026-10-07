using AppleMusicOverlay.Models;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay.Tests;

public sealed class GamepadDeviceClassifierTests
{
    [Fact]
    public void ClassifyRecognizesXboxFromMicrosoftVendor()
    {
        GamepadDeviceKind kind = GamepadDeviceClassifier.Classify(0x045E, 0x02EA, "Xbox Wireless Controller");

        Assert.Equal(GamepadDeviceKind.Xbox, kind);
    }

    [Fact]
    public void ClassifyRecognizesDualSenseFromSonyVendorAndProduct()
    {
        GamepadDeviceKind kind = GamepadDeviceClassifier.Classify(0x054C, 0x0CE6, "Wireless Controller");

        Assert.Equal(GamepadDeviceKind.DualSense, kind);
    }

    [Fact]
    public void ClassifyKeepsWrappedControllersCompatibleWhenPhysicalTypeIsAmbiguous()
    {
        GamepadDeviceKind kind = GamepadDeviceClassifier.Classify(0x28DE, 0x11FF, "Steam Virtual Gamepad");

        Assert.Equal(GamepadDeviceKind.Compatible, kind);
    }

    [Fact]
    public void MergeDescriptorsPrefersStandardGamepadAndDoesNotDuplicateMatchingRawDevice()
    {
        var descriptors = new[]
        {
            new GamepadDeviceDescriptor(
                RuntimeId: "standard-1",
                DisplayName: "Standard gamepad",
                VendorId: 0,
                ProductId: 0,
                HasStandardGamepad: true,
                MatchedStandardRuntimeId: null),
            new GamepadDeviceDescriptor(
                RuntimeId: "raw-1",
                DisplayName: "Xbox Wireless Controller",
                VendorId: 0x045E,
                ProductId: 0x02EA,
                HasStandardGamepad: false,
                MatchedStandardRuntimeId: "standard-1")
        };

        IReadOnlyList<GamepadDeviceInfo> devices = GamepadDeviceClassifier.MergeDescriptors(descriptors);

        GamepadDeviceInfo device = Assert.Single(devices);
        Assert.Equal("standard-1", device.RuntimeId);
        Assert.Equal(GamepadDeviceKind.Xbox, device.Kind);
        Assert.True(device.HasStandardGamepad);
        Assert.Equal("Xbox Wireless Controller", device.DisplayName);
    }

    [Fact]
    public void MergeDescriptorsIncludesRawOnlyCompatibleController()
    {
        var descriptors = new[]
        {
            new GamepadDeviceDescriptor(
                RuntimeId: "raw-2",
                DisplayName: "Generic USB Joystick",
                VendorId: 0x1234,
                ProductId: 0x5678,
                HasStandardGamepad: false,
                MatchedStandardRuntimeId: null)
        };

        IReadOnlyList<GamepadDeviceInfo> devices = GamepadDeviceClassifier.MergeDescriptors(descriptors);

        GamepadDeviceInfo device = Assert.Single(devices);
        Assert.Equal(GamepadDeviceKind.Compatible, device.Kind);
        Assert.False(device.HasStandardGamepad);
    }
}

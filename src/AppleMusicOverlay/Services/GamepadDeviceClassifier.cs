using AppleMusicOverlay.Models;

namespace AppleMusicOverlay.Services;

public sealed record GamepadDeviceDescriptor(
    string RuntimeId,
    string DisplayName,
    ushort VendorId,
    ushort ProductId,
    bool HasStandardGamepad,
    string? MatchedStandardRuntimeId);

public static class GamepadDeviceClassifier
{
    private const ushort MicrosoftVendorId = 0x045E;
    private const ushort SonyVendorId = 0x054C;

    private static readonly HashSet<ushort> DualSenseProductIds = new()
    {
        0x0CE6,
        0x0DF2
    };

    public static GamepadDeviceKind Classify(ushort vendorId, ushort productId, string? displayName)
    {
        string name = displayName ?? string.Empty;
        if (vendorId == MicrosoftVendorId || name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
        {
            return GamepadDeviceKind.Xbox;
        }

        if (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
            (vendorId == SonyVendorId && DualSenseProductIds.Contains(productId)))
        {
            return GamepadDeviceKind.DualSense;
        }

        return GamepadDeviceKind.Compatible;
    }

    public static IReadOnlyList<GamepadDeviceInfo> MergeDescriptors(IEnumerable<GamepadDeviceDescriptor> descriptors)
    {
        List<GamepadDeviceDescriptor> source = descriptors.ToList();
        Dictionary<string, GamepadDeviceDescriptor> standardDescriptors = source
            .Where(descriptor => descriptor.HasStandardGamepad)
            .GroupBy(descriptor => descriptor.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        List<GamepadDeviceInfo> devices = new();
        HashSet<string> mergedStandardRuntimeIds = new(StringComparer.Ordinal);

        foreach (GamepadDeviceDescriptor raw in source.Where(descriptor => !descriptor.HasStandardGamepad))
        {
            if (!string.IsNullOrWhiteSpace(raw.MatchedStandardRuntimeId) &&
                standardDescriptors.TryGetValue(raw.MatchedStandardRuntimeId!, out GamepadDeviceDescriptor? standard))
            {
                devices.Add(CreateDeviceInfo(
                    standard.RuntimeId,
                    raw.DisplayName,
                    raw.VendorId,
                    raw.ProductId,
                    hasStandardGamepad: true));
                mergedStandardRuntimeIds.Add(standard.RuntimeId);
                continue;
            }

            devices.Add(CreateDeviceInfo(
                raw.RuntimeId,
                raw.DisplayName,
                raw.VendorId,
                raw.ProductId,
                hasStandardGamepad: false));
        }

        foreach (GamepadDeviceDescriptor standard in standardDescriptors.Values)
        {
            if (mergedStandardRuntimeIds.Contains(standard.RuntimeId))
            {
                continue;
            }

            devices.Add(CreateDeviceInfo(
                standard.RuntimeId,
                standard.DisplayName,
                standard.VendorId,
                standard.ProductId,
                hasStandardGamepad: true));
        }

        return devices
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static GamepadDeviceInfo CreateDeviceInfo(string runtimeId, string displayName, ushort vendorId, ushort productId, bool hasStandardGamepad)
    {
        string normalizedName = string.IsNullOrWhiteSpace(displayName) ? LocalizationService.Current.Text("CompatibleGamepad") : displayName.Trim();
        return new GamepadDeviceInfo(
            runtimeId,
            normalizedName,
            Classify(vendorId, productId, normalizedName),
            hasStandardGamepad,
            vendorId,
            productId);
    }
}

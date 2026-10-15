using System.IO;
using System.Security.Cryptography;
using System.Text;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

[Collection("SecretStoreCollection")]
public sealed class SecretStoreMultiKeyTests : IDisposable
{
    private readonly List<string> _testKeysCreated = [];

    public void Dispose()
    {
        foreach (string key in _testKeysCreated)
        {
            try
            {
                SecretStore.DeleteSecret(key);
            }
            catch { }
        }

        // Clean up any quarantine files created during tests
        try
        {
            if (Directory.Exists(AppPaths.Root))
            {
                foreach (string f in Directory.EnumerateFiles(AppPaths.Root, "*.corrupt.*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    [Fact]
    public void MultiKey_SavesAndLoadsMultipleIndependentSecrets()
    {
        // Arrange
        const string key1 = "test_anthropic_key";
        const string val1 = "sk-ant-api03-test123456789";
        const string key2 = "test_custom_token";
        const string val2 = "custom_secret_payload_XYZ";

        _testKeysCreated.Add(key1);
        _testKeysCreated.Add(key2);

        // Act
        SecretStore.SaveSecret(key1, val1);
        SecretStore.SaveSecret(key2, val2);

        // Assert
        SecretStore.HasSecret(key1).Should().BeTrue();
        SecretStore.HasSecret(key2).Should().BeTrue();
        SecretStore.LoadSecret(key1).Should().Be(val1);
        SecretStore.LoadSecret(key2).Should().Be(val2);
    }

    [Fact]
    public void DeleteSecret_RemovesSpecificSecretOnly()
    {
        // Arrange
        const string keyA = "test_delete_a";
        const string keyB = "test_delete_b";
        _testKeysCreated.Add(keyA);
        _testKeysCreated.Add(keyB);

        SecretStore.SaveSecret(keyA, "value_a");
        SecretStore.SaveSecret(keyB, "value_b");

        // Act
        bool deleted = SecretStore.DeleteSecret(keyA);

        // Assert
        deleted.Should().BeTrue();
        SecretStore.HasSecret(keyA).Should().BeFalse();
        SecretStore.LoadSecret(keyA).Should().BeNull();
        SecretStore.LoadSecret(keyB).Should().Be("value_b");
    }

    [Fact]
    public void ListSecretNames_ReturnsAllConfiguredSecrets()
    {
        // Arrange
        const string keyX = "test_list_alpha";
        const string keyY = "test_list_beta";
        _testKeysCreated.Add(keyX);
        _testKeysCreated.Add(keyY);

        SecretStore.SaveSecret(keyX, "valX");
        SecretStore.SaveSecret(keyY, "valY");

        // Act
        var names = SecretStore.ListSecretNames();

        // Assert
        names.Should().Contain(keyX);
        names.Should().Contain(keyY);
    }

    [Fact]
    public void CorruptedSecretFile_IsQuarantinedAndReturnsNullWithoutCrash()
    {
        // Arrange
        const string secretName = "test_corrupted_key";
        _testKeysCreated.Add(secretName);

        string secretPath = SecretStore.GetSecretFilePath(secretName);
        Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);

        // Write unencrypted garbage bytes directly to the file
        byte[] garbage = new byte[128];
        Random.Shared.NextBytes(garbage);
        File.WriteAllBytes(secretPath, garbage);

        // Act: should catch CryptographicException, quarantine the file, and return null
        string? loaded = SecretStore.LoadSecret(secretName);

        // Assert
        loaded.Should().BeNull();
        File.Exists(secretPath).Should().BeFalse("original corrupted file must have been moved/quarantined");

        string dir = Path.GetDirectoryName(secretPath)!;
        var quarantined = Directory.EnumerateFiles(dir, Path.GetFileName(secretPath) + ".corrupt.*").ToList();
        quarantined.Should().NotBeEmpty("corrupted file should be renamed with .corrupt. timestamp suffix");
    }

    [Fact]
    public void ZeroByteSecretFile_ReturnsNullWithoutThrowing()
    {
        // Arrange
        const string secretName = "test_zerobyte_key";
        _testKeysCreated.Add(secretName);

        string secretPath = SecretStore.GetSecretFilePath(secretName);
        Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
        File.WriteAllBytes(secretPath, []);

        // Act
        string? loaded = SecretStore.LoadSecret(secretName);

        // Assert
        loaded.Should().BeNull();
    }
}

using System;
using System.Security.Cryptography;
using System.Text;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier1_FeatureCoverage;

[CollectionDefinition("SecretStoreCollection", DisableParallelization = true)]
public class SecretStoreCollectionDefinition
{
}

[Collection("SecretStoreCollection")]
public sealed class SecretStoreTests : IDisposable
{
    private readonly string? _originalKey;

    public SecretStoreTests()
    {
        // Preserve any existing real key during test execution
        _originalKey = SecretStore.LoadApiKey();
    }

    public void Dispose()
    {
        // Restore previous key state
        if (_originalKey is not null)
        {
            SecretStore.SaveApiKey(_originalKey);
        }
        else
        {
            SecretStore.DeleteApiKey();
        }
    }

    [Fact]
    public void SaveApiKey_And_LoadApiKey_Roundtrip_PreservesPlaintext()
    {
        // Arrange
        const string testKey = "AIzaSyTest_Void_SecretKey_987654321";

        // Act
        SecretStore.SaveApiKey(testKey);
        string? loaded = SecretStore.LoadApiKey();

        // Assert
        loaded.Should().NotBeNull();
        loaded.Should().Be(testKey);
    }

    [Fact]
    public void DeleteApiKey_RemovesKey_SoLoadReturnsNull()
    {
        // Arrange
        SecretStore.SaveApiKey("AIzaSyTest_ToBeDeleted_12345");

        // Act
        SecretStore.DeleteApiKey();
        string? loaded = SecretStore.LoadApiKey();

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public void SaveApiKey_OverwritesPreviousKey()
    {
        // Arrange
        SecretStore.SaveApiKey("AIzaSyFirstKey_11111");

        // Act
        SecretStore.SaveApiKey("AIzaSySecondKey_22222");
        string? loaded = SecretStore.LoadApiKey();

        // Assert
        loaded.Should().Be("AIzaSySecondKey_22222");
    }

    [Fact]
    public void LoadApiKey_WhenNoFileExists_ReturnsNull()
    {
        // Arrange
        SecretStore.DeleteApiKey();

        // Act
        string? loaded = SecretStore.LoadApiKey();

        // Assert
        loaded.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void SaveApiKey_ThrowsOnEmptyOrWhitespace(string invalidKey)
    {
        // Act
        Action act = () => SecretStore.SaveApiKey(invalidKey);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DPAPI_ProtectionScope_DirectEncryptDecrypt_Succeeds()
    {
        // Arrange
        byte[] secret = Encoding.UTF8.GetBytes("SuperSecretToken_For_Void");

        // Act
        byte[] encrypted = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
        byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);

        // Assert
        encrypted.Should().NotBeNullOrEmpty();
        encrypted.Should().NotEqual(secret);
        Encoding.UTF8.GetString(decrypted).Should().Be("SuperSecretToken_For_Void");
    }
}

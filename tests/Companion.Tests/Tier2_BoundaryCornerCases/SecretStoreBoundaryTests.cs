using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Companion.Core;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier2_BoundaryCornerCases;

[Collection("SecretStoreCollection")]
public sealed class SecretStoreBoundaryTests : IDisposable
{
    private readonly string? _originalKey;

    public SecretStoreBoundaryTests()
    {
        _originalKey = SecretStore.LoadApiKey();
    }

    public void Dispose()
    {
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
    public void CorruptCipherBytes_ThrowsCryptographicException_OnUnprotect()
    {
        // Arrange: corrupt encrypted data (random garbage bytes)
        byte[] garbage = new byte[64];
        Random.Shared.NextBytes(garbage);

        // Act & Assert
        Action act = () => ProtectedData.Unprotect(garbage, optionalEntropy: null, DataProtectionScope.CurrentUser);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void SaveApiKey_HugePayload_RoundtripsSuccessfully()
    {
        // Arrange: 100KB API key / token payload
        string hugeKey = "AIzaSy" + new string('X', 100 * 1024);

        // Act
        SecretStore.SaveApiKey(hugeKey);
        string? loaded = SecretStore.LoadApiKey();

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Length.Should().Be(hugeKey.Length);
        loaded.Should().Be(hugeKey);
    }

    [Fact]
    public void Dpapi_MultiThreadedConcurrency_ProtectsAndUnprotectsWithoutDataCorruption()
    {
        // Act: 20 parallel threads concurrently protecting and unprotecting different secrets
        Parallel.For(0, 20, i =>
        {
            string secret = $"AIzaSyThreadSecret_{i}_{Guid.NewGuid():N}";
            byte[] plain = Encoding.UTF8.GetBytes(secret);
            byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            byte[] decrypted = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);

            Encoding.UTF8.GetString(decrypted).Should().Be(secret);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t ")]
    public void SaveApiKey_InvalidInputStrings_AlwaysThrowArgumentException(string invalidKey)
    {
        Action act = () => SecretStore.SaveApiKey(invalidKey);
        act.Should().Throw<ArgumentException>();
    }
}

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Companion.Core;
using Companion.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Companion.Tests.Tier5_AdversarialStress;

[Collection("SecretStoreCollection")]
public sealed class Milestone1AdversarialTests : IDisposable
{
    private readonly TestDirectoryScope _scope = new();
    private readonly List<string> _secretKeysToClean = [];

    public void Dispose()
    {
        _scope.Dispose();

        foreach (string key in _secretKeysToClean)
        {
            try
            {
                SecretStore.DeleteSecret(key);
            }
            catch { }
        }

        try
        {
            if (Directory.Exists(AppPaths.Root))
            {
                foreach (string f in Directory.EnumerateFiles(AppPaths.Root, "*adversarial*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    #region 1. SQLite WAL Mode Concurrency Stress

    [Fact]
    public async Task DatabaseService_WalMode_MultiProcessMultiThread_StressTest()
    {
        // Emulate multiple processes (Shell, Studio, Scheduler, Background Workers)
        // all connecting to the same SQLite database file concurrently with WAL mode.
        string dbPath = _scope.CreateFilePath("wal_stress_shared.db");
        
        const int instanceCount = 5;
        var instances = Enumerable.Range(0, instanceCount)
            .Select(_ => new DatabaseService(dbPath))
            .ToArray();

        const int operationsPerWorker = 30;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Run 5 workers concurrently performing mixed read/write/FTS workloads
        var tasks = new Task[instanceCount];

        // Worker 0: Add memories and immediately query FTS
        tasks[0] = Task.Run(() =>
        {
            try
            {
                var db = instances[0];
                for (int i = 0; i < operationsPerWorker; i++)
                {
                    long id = db.AddMemory($"Worker0 fact number {i} regarding telemetry", $"tag0_{i}");
                    id.Should().BeGreaterThan(0);
                    var search = db.SearchMemoriesFts("telemetry", limit: 5);
                    search.Should().NotBeNull();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Worker 1: Insert, update, and delete memories (testing FTS triggers under concurrent load)
        tasks[1] = Task.Run(() =>
        {
            try
            {
                var db = instances[1];
                for (int i = 0; i < operationsPerWorker; i++)
                {
                    long id = db.AddMemory($"Worker1 temporary fact {i}", "temp_tag");
                    db.UpdateMemory(id, $"Worker1 updated fact {i}", "updated_tag");
                    if (i % 2 == 0)
                    {
                        db.DeleteMemory(id);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Worker 2: Todos and toggling
        tasks[2] = Task.Run(() =>
        {
            try
            {
                var db = instances[2];
                for (int i = 0; i < operationsPerWorker; i++)
                {
                    long id = db.AddTodo($"Worker2 task {i}", "10:00");
                    db.ToggleTodo(id);
                    var active = db.GetActiveTodos();
                    active.Should().NotBeNull();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Worker 3: Increment daily stats concurrently
        tasks[3] = Task.Run(() =>
        {
            try
            {
                var db = instances[3];
                for (int i = 0; i < operationsPerWorker; i++)
                {
                    db.IncrementStat("tools_executed", 1);
                    db.IncrementStat("dictated_words", 5);
                    var stats = db.GetTodayStats();
                    stats.Should().NotBeNull();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Worker 4: Scheduled actions and audit logs
        tasks[4] = Task.Run(() =>
        {
            try
            {
                var db = instances[4];
                for (int i = 0; i < operationsPerWorker; i++)
                {
                    long id = db.AddScheduledAction(
                        $"Worker4 scheduled action {i}",
                        "System.Echo",
                        $"{{\"msg\":{i}}}",
                        DateTime.UtcNow.AddMinutes(i + 1));
                    id.Should().BeGreaterThan(0);
                    db.AddAuditEntry("System.Echo", $"{{\"i\":{i}}}", "Success");
                    var audits = db.GetRecentAuditEntries(10);
                    audits.Should().NotBeNull();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        await Task.WhenAll(tasks);

        // Assert: Absolutely no exceptions (no SQLITE_BUSY or table locks)
        exceptions.Should().BeEmpty("SQLite WAL mode with busy_timeout=5000 must tolerate multi-process concurrency");

        // Assert data consistency
        var finalDb = instances[0];
        var finalStats = finalDb.GetTodayStats();
        finalStats.ToolsExecuted.Should().Be(operationsPerWorker);
        finalStats.DictatedWords.Should().Be(operationsPerWorker * 5);
    }

    [Fact]
    public void DatabaseService_ConcurrentClipboardAdditions_PreservesDpapiAndTrimming()
    {
        string dbPath = _scope.CreateFilePath("clipboard_stress.db");
        var db = new DatabaseService(dbPath);

        const int threadCount = 20;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Concurrently add text and images (each protected with DPAPI under CurrentUser)
        Parallel.For(0, threadCount, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    db.AddClipboardText($"Clipboard text item {i}", maxEntries: 10);
                }
                else
                {
                    byte[] dummyPng = new byte[1024];
                    Random.Shared.NextBytes(dummyPng);
                    db.AddClipboardImage(dummyPng, maxEntries: 10);
                }

                var entries = db.GetClipboardEntries(limit: 10);
                entries.Should().NotBeNull();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty("Concurrent clipboard additions using DPAPI and transactions must not throw");
        var finalEntries = db.GetClipboardEntries(limit: 50);
        finalEntries.Count.Should().BeLessThanOrEqualTo(10, "Ring buffer limit of 10 must be enforced");
    }

    #endregion

    #region 2. Malicious & Adversarial FTS5 Queries

    [Theory]
    // Unmatched quotes and delimiters
    [InlineData("\"")]
    [InlineData("\"\"\"\"\"")]
    [InlineData("'''")]
    [InlineData("\"unclosed quote")]
    [InlineData("unopened quote\"")]
    [InlineData("'unclosed single")]
    [InlineData("unopened single'")]
    [InlineData("\"\"\"")]
    // FTS5 Boolean operators and syntax keywords
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("NOT")]
    [InlineData("NEAR")]
    [InlineData("NEAR()")]
    [InlineData("NEAR/5")]
    [InlineData("AND OR NOT NEAR")]
    [InlineData("AND AND AND")]
    [InlineData("OR OR OR")]
    [InlineData("NOT NOT NOT")]
    // Wildcards, punctuation, and symbols
    [InlineData("*")]
    [InlineData("***")]
    [InlineData("^")]
    [InlineData(":::")]
    [InlineData("col:val")]
    [InlineData("fact:test")]
    [InlineData("nonexistent_col:test")]
    [InlineData("()")]
    [InlineData("(((())))")]
    [InlineData("!@#$%^&*()_+=-[]{};:'\"\\|/?,.<>`~")]
    // SQL Injection attempts
    [InlineData("'; DROP TABLE memories; --")]
    [InlineData("'; DROP TABLE memories_fts; --")]
    [InlineData("' OR '1'='1")]
    [InlineData("\" OR \"\"=\"\"")]
    [InlineData("1; DELETE FROM todos; --")]
    [InlineData("UNION SELECT 1, 2, 3, 4 --")]
    [InlineData("'; UPDATE stats_daily SET tools_executed = 9999; --")]
    // Unicode, diacritics, multilingual, RTL, and emojis
    [InlineData("🔥🚀🤖💡")]
    [InlineData("東京都 渋谷区")]
    [InlineData("مرحبا بكم")]
    [InlineData("नमस्ते दुनिया")]
    [InlineData("Привет мир")]
    [InlineData("café naïve façade Übergrößen")]
    [InlineData("z​w​s")] // Zero-width space
    public void FTS5_AdversarialQueries_NeverThrowOrCorrupt(string query)
    {
        var db = new DatabaseService(_scope.CreateFilePath("fts_adversarial.db"));
        db.AddMemory("Void desktop companion with C# 12 and .NET 8 WPF", "core,shell");
        db.AddMemory("Gemini Live real-time audio pipeline with 24kHz playback", "audio,gemini");
        db.AddMemory("東京都 渋谷区 and café naïve façade Übergrößen with emojis 🔥🚀", "unicode,intl");

        // Act: Must execute without throwing
        Action act = () =>
        {
            var results = db.SearchMemoriesFts(query);
            results.Should().NotBeNull();
        };

        act.Should().NotThrow($"FTS5 query '{query}' must be safely handled without throwing exceptions or corrupting storage");

        // Ensure database tables are completely intact
        db.GetAllMemories().Count.Should().Be(3);
    }

    [Fact]
    public void FTS5_VeryLongQueryString_HandlesGracefully()
    {
        var db = new DatabaseService(_scope.CreateFilePath("fts_long_query.db"));
        db.AddMemory("Standard memory item for search test", "tag");

        // 10,000 characters of repeated tokens
        string longQuery = string.Join(" ", Enumerable.Repeat("keyword", 1500));

        Action act = () =>
        {
            var results = db.SearchMemoriesFts(longQuery);
            results.Should().NotBeNull();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void FTS5_TriggerSync_UpdateAndDeleteReflectedInIndex()
    {
        var db = new DatabaseService(_scope.CreateFilePath("fts_trigger_sync.db"));

        // 1. Insert memory with null tags
        long id = db.AddMemory("UniqueAlphaBetaFact", tags: null);
        var initialSearch = db.SearchMemoriesFts("UniqueAlphaBetaFact");
        initialSearch.Should().HaveCount(1);

        // 2. Update memory to completely different fact
        db.UpdateMemory(id, "ReplacedGammaDeltaFact", tags: "newtags");

        // Old fact must no longer match in FTS5
        var oldSearch = db.SearchMemoriesFts("UniqueAlphaBetaFact");
        oldSearch.Should().BeEmpty();

        // New fact must match in FTS5
        var newSearch = db.SearchMemoriesFts("ReplacedGammaDeltaFact");
        newSearch.Should().HaveCount(1);

        // 3. Delete memory
        db.DeleteMemory(id);

        // Deleted fact must not match in FTS5
        var afterDeleteSearch = db.SearchMemoriesFts("ReplacedGammaDeltaFact");
        afterDeleteSearch.Should().BeEmpty();
    }

    #endregion

    #region 3. SecretStore Adversarial Conditions

    [Fact]
    public void SecretStore_ZeroByteFile_ReturnsNullAndFalse()
    {
        const string key = "adversarial_zerobyte";
        _secretKeysToClean.Add(key);

        string filePath = SecretStore.GetSecretFilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, []);

        SecretStore.HasSecret(key).Should().BeFalse();
        SecretStore.LoadSecret(key).Should().BeNull();
    }

    [Fact]
    public void SecretStore_MissingKey_ReturnsNullAndFalse()
    {
        const string missingKey = "adversarial_non_existent_key_999";
        SecretStore.HasSecret(missingKey).Should().BeFalse();
        SecretStore.LoadSecret(missingKey).Should().BeNull();
        SecretStore.DeleteSecret(missingKey).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SecretStore_NullOrWhitespaceKeys_ThrowArgumentException(string? invalidKey)
    {
        Action save = () => SecretStore.SaveSecret(invalidKey!, "val");
        save.Should().Throw<ArgumentException>();

        Action load = () => SecretStore.LoadSecret(invalidKey!);
        load.Should().Throw<ArgumentException>();

        Action delete = () => SecretStore.DeleteSecret(invalidKey!);
        delete.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SecretStore_CorruptedFile_QuarantinesAndReturnsNull()
    {
        const string key = "adversarial_corrupted";
        _secretKeysToClean.Add(key);

        string filePath = SecretStore.GetSecretFilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Write 128 random non-DPAPI bytes
        byte[] garbage = new byte[128];
        Random.Shared.NextBytes(garbage);
        File.WriteAllBytes(filePath, garbage);

        // Load must return null and quarantine the corrupted file
        string? loaded = SecretStore.LoadSecret(key);
        loaded.Should().BeNull();

        // The corrupted file at original target path should now be gone (moved to quarantine)
        File.Exists(filePath).Should().BeFalse("Corrupted file must be quarantined and moved");

        // Verify quarantine file was created
        string dir = Path.GetDirectoryName(filePath)!;
        var quarantineFiles = Directory.EnumerateFiles(dir, Path.GetFileName(filePath) + ".corrupt.*").ToList();
        quarantineFiles.Should().NotBeEmpty("A .corrupt.<timestamp> file must exist");

        // Cleanup quarantine file
        foreach (var q in quarantineFiles)
        {
            try { File.Delete(q); } catch { }
        }

        // Subsquent save to the same key must work cleanly
        SecretStore.SaveSecret(key, "recovered_secret_123");
        SecretStore.LoadSecret(key).Should().Be("recovered_secret_123");
    }

    [Fact]
    public void SecretStore_TruncatedDpapiCiphertext_QuarantinesAndReturnsNull()
    {
        const string key = "adversarial_truncated";
        _secretKeysToClean.Add(key);

        // Protect valid payload, then truncate it
        byte[] valid = ProtectedData.Protect(Encoding.UTF8.GetBytes("valid secret payload"), null, DataProtectionScope.CurrentUser);
        byte[] truncated = valid[..(valid.Length / 2)];

        string filePath = SecretStore.GetSecretFilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, truncated);

        string? loaded = SecretStore.LoadSecret(key);
        loaded.Should().BeNull();
        File.Exists(filePath).Should().BeFalse();
    }

    [Theory]
    [InlineData("../../traversal_key")]
    [InlineData("..\\..\\traversal_key")]
    [InlineData("key:with:colons")]
    [InlineData("key/with/slashes")]
    [InlineData("key\\with\\backslashes")]
    [InlineData("key with spaces")]
    [InlineData("kèy_ünîcødé_日本語")]
    public void SecretStore_PathTraversalAndUnusualKeyNames_AreSanitizedWithinAppPaths(string unusualKey)
    {
        string filePath = SecretStore.GetSecretFilePath(unusualKey);

        // Must stay inside AppPaths.Root
        string fullRoot = Path.GetFullPath(AppPaths.Root).TrimEnd('\\', '/');
        string fullFile = Path.GetFullPath(filePath);

        fullFile.Should().StartWith(fullRoot, "Secret file path must never escape AppPaths.Root directory");

        // Save, verify, and clean
        _secretKeysToClean.Add(unusualKey);
        SecretStore.SaveSecret(unusualKey, "val_for_" + unusualKey);
        SecretStore.HasSecret(unusualKey).Should().BeTrue();
        SecretStore.LoadSecret(unusualKey).Should().Be("val_for_" + unusualKey);
    }

    [Fact]
    public void SecretStore_ConcurrentSaves_SameKey_DoesNotThrow()
    {
        const string key = "adversarial_concurrent_saves";
        _secretKeysToClean.Add(key);

        const int threadCount = 20;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, i =>
        {
            try
            {
                SecretStore.SaveSecret(key, $"val_{i}");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty("Concurrent SaveSecret calls to the same key should handle file collision gracefully");
        SecretStore.LoadSecret(key).Should().NotBeNull();
    }

    [Fact]
    public void SecretStore_ConcurrentSaves_DifferentKeys_DoesNotThrow()
    {
        const int keyCount = 20;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, keyCount, i =>
        {
            string key = $"adversarial_diff_key_{i}";
            lock (_secretKeysToClean) { _secretKeysToClean.Add(key); }
            try
            {
                SecretStore.SaveSecret(key, $"val_{i}");
                string? loaded = SecretStore.LoadSecret(key);
                loaded.Should().Be($"val_{i}");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty("Concurrent SaveSecret calls to different keys should be fully isolated");
    }

    [Fact]
    public void SecretStore_ConcurrentSavesAndReads_DoNotThrowOrCorrupt()
    {
        const string key = "adversarial_concurrent";
        _secretKeysToClean.Add(key);

        const int threadCount = 30;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Concurrently save and load
        Parallel.For(0, threadCount, i =>
        {
            try
            {
                string secretVal = $"secret_val_iteration_{i}";
                SecretStore.SaveSecret(key, secretVal);
                string? loaded = SecretStore.LoadSecret(key);
                // Loaded must not be null (it will be some valid string written by one of the threads)
                loaded.Should().NotBeNull();
                loaded.Should().StartWith("secret_val_iteration_");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        exceptions.Should().BeEmpty("Concurrent SaveSecret and LoadSecret calls must succeed without file sharing collisions");

        // Final load must succeed cleanly
        SecretStore.LoadSecret(key).Should().NotBeNull();
    }

    [Fact]
    public void SecretStore_ListSecretNames_IgnoresTemporaryAndCorruptFiles()
    {
        const string validKey = "adversarial_valid_listing";
        _secretKeysToClean.Add(validKey);
        SecretStore.SaveSecret(validKey, "valid_payload");

        // Manually place a temporary and a corrupt file in AppPaths.Root
        string tmpFile = Path.Combine(AppPaths.Root, "secret_dummy.tmp");
        string corruptFile = Path.Combine(AppPaths.Root, "secret_dummy.bin.corrupt.20261014");
        File.WriteAllText(tmpFile, "dummy");
        File.WriteAllText(corruptFile, "corrupt");

        try
        {
            var names = SecretStore.ListSecretNames();
            names.Should().Contain(validKey);
            names.Should().NotContain("dummy.tmp");
            names.Should().NotContain("dummy.bin.corrupt.20261014");
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
            try { File.Delete(corruptFile); } catch { }
        }
    }

    #endregion
}

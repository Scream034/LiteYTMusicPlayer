using LMP.Tests.Framework;

namespace LMP.Tests.Unit;

public static class AtomicFileTests
{
    [TestMethod(TestCategory.Unit, "AtomicFile: Sync Text Roundtrip", Group = "Cache", Order = 10)]
    public static async Task TestSyncRoundtripAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "test_sync.json");
            const string payload = "{\"test\": 12345, \"utf8\": \"тест\"}";

            AtomicFile.WriteText(path, payload, createBackup: true);

            var read = AtomicFile.ReadTextWithFallback(path, out bool recovered);

            Assert(read == payload, $"Payload mismatch. Expected '{payload}', got '{read}'");
            Assert(!recovered, "Expected recovered == false on primary file read");
            Assert(File.Exists(path), "Target file does not exist on disk");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        await Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "AtomicFile: Async Text Roundtrip", Group = "Cache", Order = 11)]
    public static async Task TestAsyncRoundtripAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "test_async.json");
            const string payload = "{\"async\": true, \"symbols\": \"♫♪\"}";

            await AtomicFile.WriteTextAsync(path, payload, createBackup: true);

            var (read, recovered) = await AtomicFile.ReadTextWithFallbackAsync(path);

            Assert(read == payload, $"Payload mismatch. Expected '{payload}', got '{read}'");
            Assert(!recovered, "Expected recovered == false on primary file read");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [TestMethod(TestCategory.Unit, "AtomicFile: Backup Rotation (N-1 version)", Group = "Cache", Order = 12)]
    public static async Task TestBackupRotationAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "backup_test.json");
            var backupPath = string.Concat(path, ".bak");
            const string v1 = "Version 1 content";
            const string v2 = "Version 2 content";

            AtomicFile.WriteText(path, v1, createBackup: false);
            Assert(!File.Exists(backupPath), "Backup file shouldn't exist after initial write with createBackup=false");

            AtomicFile.WriteText(path, v2, createBackup: true);

            Assert(File.Exists(backupPath), "Backup file was not created");
            Assert(File.ReadAllText(path) == v2, "Primary file does not contain V2");
            Assert(File.ReadAllText(backupPath) == v1, "Backup file does not contain V1");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        await Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "AtomicFile: Recovery from Corrupted File", Group = "Cache", Order = 13)]
    public static async Task TestCorruptionRecoveryAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "corrupt_test.json");
            const string validPayload = "{\"valid\": true}";

            AtomicFile.WriteText(path, validPayload, createBackup: false);
            AtomicFile.WriteText(path, validPayload, createBackup: true);

            // Имитируем сбой: обнуляем основной файл (0 байт)
            File.WriteAllText(path, string.Empty);

            var result = AtomicFile.ReadTextWithFallback(path, out bool recovered);

            Assert(result == validPayload, "Failed to recover payload from backup");
            Assert(recovered, "Expected recovered == true flag");
            Assert(new FileInfo(path).Length > 0, "Auto-heal failed to repair primary file");
            Assert(File.ReadAllText(path) == validPayload, "Repaired file content mismatch");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }

        await Task.CompletedTask;
    }

    [TestMethod(TestCategory.Unit, "AtomicFile: Cancellation Cleans Temp", Group = "Cache", Order = 14)]
    public static async Task TestCancellationCleansTempAsync()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "cancelled.json");
            var tempPath = string.Concat(path, ".tmp");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            bool threw = false;
            try
            {
                await AtomicFile.WriteTextAsync(path, "cancelled content", createBackup: false, ct: cts.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            Assert(threw, "OperationCanceledException was not thrown");
            Assert(!File.Exists(tempPath), "Temp file was not cleaned up on cancellation");
            Assert(!File.Exists(path), "Target file should not exist on cancellation");
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LMP_AtomicFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"[Assertion Failed] {message}");
    }
}